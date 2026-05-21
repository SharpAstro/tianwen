using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using static TianWen.Lib.Astrometry.Constants;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Astrometry.PlateSolve;

/// <summary>
/// A plate solver that matches detected image stars against a local Tycho-2 star catalog
/// to determine the World Coordinate System (WCS) centre of an image.
///
/// <para><b>Algorithm overview</b></para>
/// <list type="number">
///   <item>Query <see cref="ICelestialObjectDB"/> for catalog stars within the search radius
///         around the supplied <c>searchOrigin</c>. Stars are sorted by Johnson V magnitude
///         (brightest first) to enable brightness-aware matching.</item>
///   <item>Detect stars in the image via <see cref="Image.FindStarsAsync"/> (SNR ≥ 5, up to 500 stars).</item>
///   <item>Project the catalog stars onto the image plane using a gnomonic (tangent-plane) projection
///         centred on the current WCS estimate, with pixel scale derived from <see cref="ImageDim"/>.</item>
///   <item>Match projected catalog stars to detected image stars using a proximity search with a
///         <b>soft brightness-rank penalty</b>: detected stars are ranked by flux (brightest first),
///         and projected catalog stars inherit rank order from the brightness-sorted catalog.
///         The matching score is <c>spatialDistance + |detRank − catRank| × scale</c>, which
///         prefers both spatially close and brightness-similar pairs without hard cutoffs.</item>
///   <item>Fit a least-squares affine transform (<see cref="System.Numerics.Matrix3x2"/>) from
///         matched projected positions to detected positions, invert it to find the image centre
///         in catalog coordinates, then update the WCS estimate via inverse tangent projection.</item>
///   <item>Repeat steps 3–5 for up to 10 iterations (with shrinking match tolerance) until
///         convergence (ΔRA &lt; 10⁻⁶° and ΔDec &lt; 10⁻⁶°). Both standard and mirror-flipped
///         orientations are attempted; the orientation with more matched stars is selected.</item>
/// </list>
///
/// <para>Requires a pre-initialised <see cref="ICelestialObjectDB"/> with Tycho-2 data and a
/// valid <c>searchOrigin</c>; blind solving (no search hint) is not supported and returns
/// <c>null</c>.</para>
/// </summary>
internal sealed class CatalogPlateSolver(ICelestialObjectDB db, ILogger logger) : IPlateSolver
{
    private readonly ILogger _logger = logger;

    const int MinStarsForMatch = 6;

    /// <summary>
    /// Minimum inlier count required before we attempt a SIP polynomial fit
    /// on top of the linear CD matrix. Order-2 SIP has 5 unknowns per axis
    /// (i + j ∈ [1, 2]) = 10 in total; 30 inliers is a comfortable 3× over-
    /// determine and below this threshold we fall back to the linear WCS.
    /// </summary>
    const int MinMatchesForSipFit = 30;

    /// <summary>
    /// SIP polynomial order. <c>0</c> disables the fit (emits linear WCS);
    /// the default <c>2</c> matches what astrometry.net emits and covers
    /// the residual distortion observed on hobby-grade Newtonians at
    /// 1-3 arcsec/px. Tests / advanced callers may bump this for
    /// wider-field optics.
    /// </summary>
    internal int SipOrder { get; set; } = 3;

    public string Name => "Catalog plate solver";

    public float Priority => 0.99f;

    private readonly record struct SolveAttempt(WCS? Wcs, int ProjectedStars, int MatchedStars, int Iterations, double RmsResidual, double AffineDeterminant);

    /// <summary>
    /// Catalog-star projection result with the originating sky coordinates
    /// attached, so the matching loop can collect (detected pixel, catalog
    /// RA/Dec) pairs without a second pass over the catalog.
    /// </summary>
    private readonly record struct ProjectedCatalogStar(ImagedStar Pixel, double RA, double Dec);

    private int _catalogStars, _detectedStars;

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);

    public Task<PlateSolveResult> SolveFileAsync(
        string fitsFile,
        ImageDim? imageDim = default,
        float range = IPlateSolver.DefaultRange,
        WCS? searchOrigin = default,
        double? searchRadius = default,
        CancellationToken cancellationToken = default
    )
    {
        var sw = Stopwatch.StartNew();

        if (!Image.TryReadFitsFile(fitsFile, out var image, out var fileWcs))
        {
            return Task.FromResult(new PlateSolveResult(null, sw.Elapsed));
        }

        // Fall back to the file's own WCS (approximate RA/Dec from headers) when no explicit search origin
        searchOrigin ??= fileWcs;

        if (searchOrigin is null)
        {
            return Task.FromResult(new PlateSolveResult(null, sw.Elapsed));
        }

        return SolveImageAsync(image, imageDim, range, searchOrigin, searchRadius, cancellationToken);
    }

    public async Task<PlateSolveResult> SolveImageAsync(
        Image image,
        ImageDim? imageDim = default,
        float range = IPlateSolver.DefaultRange,
        WCS? searchOrigin = default,
        double? searchRadius = default,
        CancellationToken cancellationToken = default
    )
    {
        var sw = Stopwatch.StartNew();

        PlateSolveResult Result(SolveAttempt a) => new PlateSolveResult(a.Wcs, sw.Elapsed)
        {
            CatalogStars = _catalogStars,
            DetectedStars = _detectedStars,
            ProjectedStars = a.ProjectedStars,
            MatchedStars = a.MatchedStars,
            Iterations = a.Iterations
        };

        var empty = new SolveAttempt(null, 0, 0, 0, 0, 0);

        if (searchOrigin is not { } origin)
        {
            return Result(empty);
        }

        if ((imageDim ?? image.GetImageDim()) is not { } dim)
        {
            return Result(empty);
        }

        var fov = dim.FieldOfView;
        var searchRadiusDeg = searchRadius ?? Math.Max(fov.width, fov.height) * 0.75;

        // Self-init the celestial-object DB so any caller works regardless of whether
        // they remembered to InitDBAsync upstream. Idempotent: after the first call
        // _isInitialized makes this an instant fast-path. Without it the catalog query
        // returns 0 stars and we bail in tens of ms with no useful diagnostic.
        await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: cancellationToken);

        // Map FITS DATE-OBS to fractional Julian years since J2000.0 so the
        // catalog query can propagate Tycho-2 J2000 positions to the image
        // epoch via proper motion. The Year > 1900 guard turns missing /
        // synthetic DATE-OBS metadata into dtYr=0 (no propagation) rather
        // than catastrophically applying a ~-2000yr shift.
        var exposureStart = image.ImageMeta.ExposureStartTime;
        var dtYr = exposureStart.Year > 1900 ? exposureStart.JulianYearsSinceJ2000() : 0.0;

        // Query catalog stars within search radius
        var stageSw = Stopwatch.StartNew();
        var catalogCoords = QueryCatalogStarsInRegion(origin, searchRadiusDeg, dtYr);
        _logger.LogDebug("CatalogPlateSolver: catalog query {Count} stars in {Ms}ms (Dec={Dec:F2}°, R={R:F2}°, dtYr={DtYr:F2})",
            catalogCoords.Count, stageSw.Elapsed.TotalMilliseconds, origin.CenterDec, searchRadiusDeg, dtYr);
        _catalogStars = catalogCoords.Count;
        if (catalogCoords.Count < MinStarsForMatch)
        {
            // Most common cause: ICelestialObjectDB.InitDBAsync was never called.
            // The DB returns an empty CoordinateGrid until Tycho-2 bulk decode lands.
            // Callers (StackingPipeline/MasterPostProcessor) explicitly init before
            // solving; the CLI's `solve` subcommand initialises in the same fashion.
            _logger.LogWarning("CatalogPlateSolver: only {Count} catalog stars in search region (need {Min}); did you forget to InitDBAsync the celestial object DB?",
                catalogCoords.Count, MinStarsForMatch);
            return Result(empty);
        }

        // Downsample heavily oversampled frames to ~1.5"/px before star
        // detection. Plate solving doesn't need sub-arcsec centroid accuracy --
        // catalog projections are already arcsec-scale via the WCS fit -- but
        // FindStarsAsync's per-pass cost scales with pixel count. A 0.97"/px
        // 9576x6388 polar preview binned 2x drops to 4788x3194 and runs ~4x
        // faster per pass. Centroid coords come back in binned pixel space, so
        // we scale them back to original-image pixels before matching.
        var detectionImage = image;
        var detectionScale = 1;
        // Integer-tenths comparison: pixelScale * 10 vs 15 dodges the
        // floating-point edge where round(1.5 / 1.293) collapses to 1 and
        // leaves the 600mm/3.76um polar preview running FindStars on the
        // full 9576x6388 frame -- ~5 s wall-clock, which blows the polar
        // ramp's 5.5 s rung-1 budget. With this gate, anything finer than
        // 1.5"/px gets binned to ~1.5-3.0"/px (still well above seeing,
        // still plenty for plate-solving centroid accuracy).
        const int TargetPixelScaleX10 = 15;
        var pixelScaleX10 = (int)Math.Round(dim.PixelScale * 10);
        if (pixelScaleX10 > 0 && pixelScaleX10 < TargetPixelScaleX10)
        {
            // Ceiling so finer-than-target inputs always get at least 2x bin.
            detectionScale = (TargetPixelScaleX10 + pixelScaleX10 - 1) / pixelScaleX10;
            if (detectionScale > 1)
            {
                stageSw.Restart();
                detectionImage = image.Downsample(detectionScale);
                _logger.LogDebug("CatalogPlateSolver: downsampled {SrcW}x{SrcH} -> {DstW}x{DstH} (factor {Factor}, target {Target}\"/px) in {Ms}ms",
                    image.Width, image.Height, detectionImage.Width, detectionImage.Height, detectionScale, TargetPixelScaleX10 / 10.0, stageSw.Elapsed.TotalMilliseconds);
            }
        }

        // Detect stars in image. minStars=50 short-circuits the do-while retry
        // loop in FindStarsAsync as soon as we have enough stars to attempt
        // matching (MinStarsForMatch is 6; 50 is comfortable redundancy). On
        // synthetic SCP frames the first pass already yields >200 stars, so
        // the retry loop never fires. maxRetries=0 caps wall-clock on the
        // failure path: when an under-exposed polar-align rung has only a
        // handful of detectable stars, FindStarsAsync's two extra passes (at
        // progressively lower SNR) can't conjure more stars out of nothing
        // and just burn 2 x ~1-2 s on the un-binned IMX455 image, blowing
        // through the rung budget. Without retries, a starved frame returns
        // its few real stars and the ramp moves on.
        stageSw.Restart();
        var detectedStars = await detectionImage.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0, logger: _logger, cancellationToken: cancellationToken);
        _logger.LogDebug("CatalogPlateSolver: FindStarsAsync detected {Count} stars in {Ms}ms ({W}x{H})",
            detectedStars.Count, stageSw.Elapsed.TotalMilliseconds, detectionImage.Width, detectionImage.Height);

        // Scale centroids back to original-image pixel space if we downsampled.
        // (factor*x + factor/2 - 0.5) puts the binned-pixel centre back to
        // the centre of its original block.
        if (detectionScale > 1)
        {
            var halfBlock = detectionScale / 2.0f - 0.5f;
            var rescaled = new System.Collections.Concurrent.ConcurrentBag<ImagedStar>();
            foreach (var s in detectedStars)
            {
                rescaled.Add(s with
                {
                    XCentroid = s.XCentroid * detectionScale + halfBlock,
                    YCentroid = s.YCentroid * detectionScale + halfBlock,
                });
            }
            detectedStars = new StarList(rescaled);
        }
        _detectedStars = detectedStars.Count;
        if (detectedStars.Count < MinStarsForMatch)
        {
            return Result(empty);
        }

        // Sort catalog stars by brightness (lowest mag = brightest first).
        // This improves proximity matching since the brightest projected catalog stars
        // are most likely to correspond to detected stars.
        catalogCoords.Sort((a, b) => a.VMag.CompareTo(b.VMag));

        var pixelScaleRad = double.DegreesToRadians(dim.PixelScale / 3600.0);
        var cx = image.Width / 2.0;
        var cy = image.Height / 2.0;

        // Try both orientations in parallel; pick the one with lower re-projection error
        stageSw.Restart();
        var stdTask = Task.Run(() => TrySolveWithProximityMatching(detectedStars, catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign: 1.0, cancellationToken), cancellationToken);
        var mirrorTask = Task.Run(() => TrySolveWithProximityMatching(detectedStars, catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign: -1.0, cancellationToken), cancellationToken);
        await Task.WhenAll(stdTask, mirrorTask);

        var std = stdTask.Result;
        var mirror = mirrorTask.Result;
        _logger.LogDebug("CatalogPlateSolver: matching iterations std={StdMatched}/{StdIter} (rms {StdRms:F2}px) mirror={MirrorMatched}/{MirrorIter} (rms {MirRms:F2}px) in {Ms}ms",
            std.MatchedStars, std.Iterations, std.RmsResidual, mirror.MatchedStars, mirror.Iterations, mirror.RmsResidual, stageSw.Elapsed.TotalMilliseconds);

        // Pick the parity. Match count is the *primary* signal: if one attempt
        // has dramatically more matched stars, it's the right answer regardless
        // of what re-projection error says. The tiebreaker case (close match
        // counts) is where re-projection error legitimately discriminates.
        //
        // Why this matters: TrySolveWithProximityMatching's early-return case
        // (match count drops below MinStarsForMatch in iter > 0) returns the
        // *previous iteration's* CD matrix as a "best effort" WCS with only
        // the failed iter's match count. ReProjectionError computed against
        // such a WCS can fortuitously be low (the WCS is in the right ballpark,
        // and there are 80+ detected stars on the frame, so projecting 20
        // bright catalog candidates almost always hits *some* nearby detected
        // star). That used to flip the parity at Dec near -90 deg, picking
        // std=3-matched garbage over mirror=30-matched correct.
        SolveAttempt winner;
        if (std.MatchedStars >= 2 * Math.Max(mirror.MatchedStars, 1))
        {
            winner = std;
        }
        else if (mirror.MatchedStars >= 2 * Math.Max(std.MatchedStars, 1))
        {
            winner = mirror;
        }
        else
        {
            // Close in match count -- both parities found roughly the same set.
            // Re-projection error then picks the one whose CD matrix actually
            // projects bright catalog stars onto detected stars.
            var stdError = std.Wcs is { } ws ? ReProjectionError(ws, catalogCoords, detectedStars) : double.MaxValue;
            var mirrorError = mirror.Wcs is { } wm ? ReProjectionError(wm, catalogCoords, detectedStars) : double.MaxValue;
            winner = stdError <= mirrorError ? std : mirror;
        }

        return Result(winner);
    }

    private SolveAttempt TrySolveWithProximityMatching(
        StarList detectedStars,
        List<(double RA, double Dec, double VMag)> catalogCoords,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign,
        CancellationToken cancellationToken
    )
    {
        var currentOrigin = origin;
        Matrix3x2 lastMinv = default;
        var hasMinv = false;
        var peakMatchCount = 0;
        int projectedCount = 0, matchedCount = 0, iterCount = 0;
        double rmsResidual = 0, affineDet = 0;

        SolveAttempt MakeResult(WCS? wcs) => new SolveAttempt(wcs, projectedCount, matchedCount, iterCount, rmsResidual, affineDet);

        // Track the best-so-far match set so SIP can be fit on it after the
        // loop (or at an early-return). These trail one iteration behind the
        // active matched* lists because we only commit them when the iter's
        // count makes it past the peakMatchCount filter.
        List<Vector2>? finalMatchedDetected = null;
        List<(double RA, double Dec)>? finalMatchedCatalogSky = null;

        // Iteratively refine: project → match → fit affine → update WCS → repeat
        for (int iteration = 0; iteration < 10; iteration++)
        {
            // Cooperative cancellation per iteration: the inner detRank x catRank
            // loop is O(n^2) and on dense fields each iteration can run ~1 s.
            // Without this, AdaptiveExposureRamp.ProbeAsync's per-rung
            // CancelAfter fires but cancellation doesn't propagate until this
            // iteration finishes naturally -- elapsed wall-clock can run
            // 5-10 s past the budget on a tight rung. Clean exit (return
            // null result) instead of throw -- the caller already treats a
            // null Solution as "ramp moves to next rung", and exceptions are
            // only worth raising when there's no other way out.
            if (cancellationToken.IsCancellationRequested)
            {
                return MakeResult(null);
            }

            var projected = ProjectCatalogStars(catalogCoords, currentOrigin, pixelScaleRad, cx, cy, dim, xSign);
            if (projected.Count < MinStarsForMatch)
            {
                return MakeResult(null);
            }

            // Match tolerance shrinks geometrically with each iteration so the
            // final WCS converges to sub-pixel precision instead of plateauing
            // at ~3% of diagonal (~345 px on IMX455 / ~520 arcsec at 1.5"/px),
            // which left the polar-refining plate solve picking up spurious
            // anchor matches and produced visible jitter on the displayed
            // (Az, Alt) error. Schedule:
            //   iter 0: 10% diag (blind, mount pointing may be 45deg off)
            //   iter 1: 3%  diag (post first WCS estimate, large slack ok)
            //   iter 2: 1%  diag
            //   iter 3: 0.3% diag
            //   iter 4+: 0.1% diag (sub-pixel; only true matches survive)
            // Scale by average inter-star spacing to keep dense fields (>500
            // stars) from overlapping multiple catalog candidates per detection.
            var diagonal = Math.Sqrt(dim.Width * dim.Width + dim.Height * dim.Height);
            var avgSpacing = Math.Sqrt((double)dim.Width * dim.Height / Math.Max(projected.Count, 1));
            var diagFraction = iteration switch
            {
                0 => 0.10,
                1 => 0.03,
                2 => 0.01,
                3 => 0.003,
                _ => 0.001,
            };
            var spacingFraction = iteration == 0 ? 3.0 : iteration == 1 ? 2.0 : iteration == 2 ? 1.0 : 0.5;
            var matchTolerance = (float)Math.Min(diagonal * diagFraction, avgSpacing * spacingFraction);

            // Rank detected stars by flux (brightest first) for brightness-aware matching.
            var rankedDetected = new List<ImagedStar>(detectedStars);
            rankedDetected.Sort((a, b) => b.Flux.CompareTo(a.Flux));

            // In dense fields (>500 projected), limit early iterations to brightest
            // stars where spatial matching is least ambiguous.
            var isDense = projected.Count > 500;
            var maxDetectedForMatching = isDense && iteration < 2
                ? Math.Min(iteration == 0 ? 50 : 100, rankedDetected.Count)
                : rankedDetected.Count;
            var maxProjectedForMatching = isDense && iteration < 2
                ? Math.Min(iteration == 0 ? 50 : 100, projected.Count)
                : projected.Count;

            var rankPenaltyScale = projected.Count > 0 ? matchTolerance * 0.25f / projected.Count : 0f;

            var matchedDetected = new List<Vector2>();
            var matchedProjected = new List<Vector2>();
            var matchedCatalogSky = new List<(double RA, double Dec)>();

            for (int detRank = 0; detRank < maxDetectedForMatching; detRank++)
            {
                // Mid-iteration cancellation: at IMX455 size with hundreds of
                // detections × hundreds of projections, a single iter 0 can
                // run several seconds. Polling every 64 detections caps the
                // overrun at a few hundred ms past budget.
                if ((detRank & 63) == 0 && cancellationToken.IsCancellationRequested)
                {
                    return MakeResult(null);
                }

                var det = rankedDetected[detRank];
                var bestScore = matchTolerance;
                ProjectedCatalogStar? bestMatch = null;

                for (int catRank = 0; catRank < maxProjectedForMatching; catRank++)
                {
                    var cat = projected[catRank];
                    var dx = det.XCentroid - cat.Pixel.XCentroid;
                    var dy = det.YCentroid - cat.Pixel.YCentroid;
                    var dist = MathF.Sqrt(dx * dx + dy * dy);

                    if (dist < matchTolerance)
                    {
                        var rankPenalty = Math.Abs(detRank - catRank) * rankPenaltyScale;
                        var score = dist + rankPenalty;

                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestMatch = cat;
                        }
                    }
                }

                if (bestMatch is { } bm)
                {
                    matchedDetected.Add(new Vector2(det.XCentroid, det.YCentroid));
                    matchedProjected.Add(new Vector2(bm.Pixel.XCentroid, bm.Pixel.YCentroid));
                    matchedCatalogSky.Add((bm.RA, bm.Dec));
                }
            }

            if (matchedDetected.Count < MinStarsForMatch)
            {
                projectedCount = projected.Count;
                // When we return iteration N's WCS as a best-effort fallback,
                // report peakMatchCount (the support for that WCS) instead of
                // the failed iter's match count. Otherwise the parity tiebreak
                // sees "WCS exists but only 3 matched" and a downstream
                // MatchedStars gate rejects an otherwise-fine WCS that was
                // built from peakMatchCount inliers.
                matchedCount = iteration > 0 ? peakMatchCount : matchedDetected.Count;
                iterCount = iteration + 1;
                return MakeResult(iteration > 0
                    ? MaybeFitSip(AttachCDMatrix(currentOrigin, hasMinv ? lastMinv : default(Matrix3x2?), pixelScaleRad, cx, cy, dim, xSign), finalMatchedDetected, finalMatchedCatalogSky)
                    : null);
            }

            // Early stop if match count drops catastrophically -- the divergence
            // protection. Originally compared to peakMatchCount/2, but that floor
            // is too aggressive past iter 2 where matchTolerance shrinks ~3x per
            // iter (40px -> 12px -> 4px) and naturally cuts the match count to
            // 1/3 - 1/2 of the previous iter without indicating any divergence.
            // Net effect: iter 3's tighter (and cleaner) inlier set got rolled
            // back to iter 2's loose set, leaving SIP unable to clear its gate.
            // Use peakMatchCount/4 from iter 3 onward (still catches catastrophic
            // collapse) while requiring a hard floor of MinMatchesForSipFit so
            // we never accept an inlier set too small for the post-loop SIP fit.
            var divergeFloor = iteration < 3
                ? peakMatchCount / 2
                : Math.Max(peakMatchCount / 4, MinMatchesForSipFit);
            if (iteration > 0 && matchedDetected.Count < divergeFloor)
            {
                break;
            }
            peakMatchCount = Math.Max(peakMatchCount, matchedDetected.Count);

            // Save the best-so-far match set for the post-loop SIP fit. We
            // commit only at iterations that survive the count gate above,
            // so a transient bad iteration does not poison the SIP inputs.
            finalMatchedDetected = matchedDetected;
            finalMatchedCatalogSky = matchedCatalogSky;

            // Compute offset using Matrix3x2 affine fit (handles translation + rotation)
            var M = Matrix3x2.FitAffineTransform(CollectionsMarshal.AsSpan(matchedProjected), CollectionsMarshal.AsSpan(matchedDetected));
            if (M is null || !Matrix3x2.Invert(M.Value, out var Minv))
            {
                return MakeResult(iteration > 0
                    ? MaybeFitSip(AttachCDMatrix(currentOrigin, hasMinv ? lastMinv : default(Matrix3x2?), pixelScaleRad, cx, cy, dim, xSign), finalMatchedDetected, finalMatchedCatalogSky)
                    : null);
            }

            lastMinv = Minv;
            hasMinv = true;

            // Track affine quality metrics
            affineDet = M.Value.M11 * M.Value.M22 - M.Value.M12 * M.Value.M21;
            if (matchedDetected.Count > 0)
            {
                double sumSqResidual = 0;
                for (int i = 0; i < matchedDetected.Count; i++)
                {
                    var transformed = Vector2.Transform(matchedProjected[i], M.Value);
                    var rdx = transformed.X - matchedDetected[i].X;
                    var rdy = transformed.Y - matchedDetected[i].Y;
                    sumSqResidual += rdx * rdx + rdy * rdy;
                }
                rmsResidual = Math.Sqrt(sumSqResidual / matchedDetected.Count);
            }


            var centerInProjected = Vector2.Transform(new Vector2((float)cx, (float)cy), Minv);
            var refined = InverseTanProject(centerInProjected, currentOrigin, pixelScaleRad, cx, cy, xSign);

            if (refined is not { } refinedWcs)
            {
                return MakeResult(iteration > 0
                    ? MaybeFitSip(AttachCDMatrix(currentOrigin, lastMinv, pixelScaleRad, cx, cy, dim, xSign), finalMatchedDetected, finalMatchedCatalogSky)
                    : null);
            }

            // Check convergence
            var dRA = Math.Abs(refinedWcs.CenterRA - currentOrigin.CenterRA) * 15.0;
            var dDec = Math.Abs(refinedWcs.CenterDec - currentOrigin.CenterDec);


            currentOrigin = refinedWcs;

            projectedCount = projected.Count;
            matchedCount = matchedDetected.Count;
            iterCount = iteration + 1;

            // Don't break before iter 3 has actually run. The center stabilises
            // quickly (it pivots around the inlier centroid which is stable
            // from iter 1 onward), but the match TOLERANCE keeps shrinking
            // through iter 3 (0.003 of diagonal ~= 12 px on a 4k master).
            // Stopping at the convergence threshold after only iter 2 leaves
            // matchTolerance at 0.01 of diagonal ~= 40 px, which admits
            // wrong-star pairings whose residuals dwarf real distortion and
            // prevent SIP from clearing its improvement gate. Forcing the
            // loop to reach iter 3's tighter tolerance hardens the inlier
            // set for both the final Kabsch affine AND the downstream SIP fit.
            const int MinIterationsBeforeConvergenceBreak = 3;
            if (iteration >= MinIterationsBeforeConvergenceBreak && dRA < 1e-6 && dDec < 1e-6)
            {
                break;
            }
        }

        return MakeResult(MaybeFitSip(
            AttachCDMatrix(currentOrigin, hasMinv ? lastMinv : default(Matrix3x2?), pixelScaleRad, cx, cy, dim, xSign),
            finalMatchedDetected,
            finalMatchedCatalogSky));
    }

    /// <summary>
    /// Fits a SIP polynomial of <see cref="SipOrder"/> onto the linear
    /// <paramref name="linearWcs"/>, returning the SIP-augmented WCS when
    /// the fit reduces residuals; otherwise returns the unchanged linear
    /// WCS. No-op when SIP is disabled, when too few matches are available,
    /// or when the WCS has no CD matrix to layer on top of.
    /// </summary>
    private WCS MaybeFitSip(
        WCS linearWcs,
        List<Vector2>? matchedDetected,
        List<(double RA, double Dec)>? matchedCatalogSky)
    {
        if (SipOrder < 1 || !linearWcs.HasCDMatrix
            || matchedDetected is null || matchedCatalogSky is null)
        {
            return linearWcs;
        }
        var n = matchedDetected.Count;
        if (n < MinMatchesForSipFit || n != matchedCatalogSky.Count)
        {
            return linearWcs;
        }

        // Each match contributes one (u_det, v_det) → (u_true, v_true) pair:
        // observed pixel offset relative to CRPIX, and the offset the catalog
        // (RA, Dec) would land at under a perfect WCS. The latter comes from
        // the linear WCS's SkyToPixel — which is exactly the "predicted"
        // pixel the affine fit produces today. The SIP polynomial fits the
        // (true − detected) residual.
        var uDetRaw = new double[n];
        var vDetRaw = new double[n];
        var duFwdRaw = new double[n];   // u_true − u_det (forward SIP target)
        var dvFwdRaw = new double[n];

        for (var i = 0; i < n; i++)
        {
            var det = matchedDetected[i];
            var sky = matchedCatalogSky[i];
            var pred = linearWcs.SkyToPixel(sky.RA, sky.Dec);
            if (pred is null)
            {
                // Behind the tangent plane — should not happen for matches
                // we already validated by proximity, but be defensive.
                return linearWcs;
            }
            uDetRaw[i] = det.X - linearWcs.CRPix1;
            vDetRaw[i] = det.Y - linearWcs.CRPix2;
            var uTrue = pred.Value.X - linearWcs.CRPix1;
            var vTrue = pred.Value.Y - linearWcs.CRPix2;
            duFwdRaw[i] = uTrue - uDetRaw[i];
            dvFwdRaw[i] = vTrue - vDetRaw[i];
        }

        // Outlier filter: the solver's late-iteration match tolerance is several
        // pixels, so the raw inlier set typically contains 5-15% false positives
        // whose residuals dwarf the actual distortion signal. Clip by 5 × MAD
        // (median absolute deviation, the robust analogue of σ) — generous enough
        // to preserve real corner distortion (~1-3 px) while culling the ~10-15 px
        // mismatches that drag the LS fit toward garbage coefficients.
        // Robust bias correction: median, not mean — the late-iteration
        // matching tolerance admits a long-tail outlier distribution
        // (matches up to ~13.5 px on the SoL master) so the mean is pulled
        // off-true. Median is the right estimator for the constant shift,
        // and SIP polynomials by convention have no constant term (i + j = 0
        // is absorbed into CRPIX), so we shift CRPIX itself to take it.
        var workU = new double[n];
        var workV = new double[n];
        Array.Copy(duFwdRaw, workU, n);
        Array.Copy(dvFwdRaw, workV, n);
        var biasU = StatisticsHelper.MedianFast(workU);
        var biasV = StatisticsHelper.MedianFast(workV);
        for (var i = 0; i < n; i++)
        {
            duFwdRaw[i] -= biasU;
            dvFwdRaw[i] -= biasV;
        }
        linearWcs = linearWcs with
        {
            CRPix1 = linearWcs.CRPix1 - biasU,
            CRPix2 = linearWcs.CRPix2 - biasV,
        };

        // Iterative MAD clip to converge on the inlier cluster. The matching
        // step's late-iter tolerance is several pixels, so a single MAD pass
        // still leaves several-px outliers in the keep set (their long-tail
        // pulls the MAD up); two more passes tighten the cluster to its
        // genuine width. Each pass shrinks the active set and recomputes MAD
        // on the survivors, terminating either when no further outliers are
        // found or when the count drops below the SIP-fit minimum.
        var work = new double[n];
        var keep = new bool[n];
        for (var i = 0; i < n; i++) keep[i] = true;
        var nKept = n;
        double medianResidual = 0, mad = 0, clipThreshold = 0;
        for (var pass = 0; pass < 3; pass++)
        {
            var w = 0;
            for (var i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                work[w++] = Math.Sqrt(duFwdRaw[i] * duFwdRaw[i] + dvFwdRaw[i] * dvFwdRaw[i]);
            }
            medianResidual = StatisticsHelper.MedianFast(work.AsSpan(0, w));
            w = 0;
            for (var i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                var resMag = Math.Sqrt(duFwdRaw[i] * duFwdRaw[i] + dvFwdRaw[i] * dvFwdRaw[i]);
                work[w++] = Math.Abs(resMag - medianResidual);
            }
            mad = StatisticsHelper.MedianFast(work.AsSpan(0, w));
            // sigma ≈ 1.4826 × MAD for normal-distributed residuals. Cap below
            // 0.5 px so the clip threshold never collapses below typical
            // centroid noise on a tight final cluster.
            var robustSigma = 1.4826 * mad;
            clipThreshold = Math.Max(medianResidual + 3 * robustSigma, 0.5);

            var newKept = 0;
            for (var i = 0; i < n; i++)
            {
                if (!keep[i]) continue;
                var resMag = Math.Sqrt(duFwdRaw[i] * duFwdRaw[i] + dvFwdRaw[i] * dvFwdRaw[i]);
                if (resMag > clipThreshold) keep[i] = false;
                else newKept++;
            }
            if (newKept == nKept) break;   // converged
            nKept = newKept;
            if (nKept < MinMatchesForSipFit) break;
        }
        _logger?.LogDebug("CatalogPlateSolver SIP fit: bias=({BiasU:F2},{BiasV:F2}) px, residual median={Med:F2} px, MAD={Mad:F2} px, clip={Clip:F1} px (kept {Kept}/{N})",
            biasU, biasV, medianResidual, mad, clipThreshold, nKept, n);

        if (nKept < MinMatchesForSipFit)
        {
            _logger?.LogDebug("CatalogPlateSolver SIP fit: skipped (only {N} clean inliers after iterative MAD clip from {Raw} raw matches)",
                nKept, n);
            return linearWcs;
        }
        var uDet = new double[nKept];
        var vDet = new double[nKept];
        var duFwd = new double[nKept];
        var dvFwd = new double[nKept];
        var inlierIndices = new int[nKept];
        double sumSqLinear = 0;
        var idx = 0;
        for (var i = 0; i < n; i++)
        {
            if (!keep[i]) continue;
            uDet[idx] = uDetRaw[i];
            vDet[idx] = vDetRaw[i];
            duFwd[idx] = duFwdRaw[i];
            dvFwd[idx] = dvFwdRaw[i];
            inlierIndices[idx] = i;
            sumSqLinear += duFwdRaw[i] * duFwdRaw[i] + dvFwdRaw[i] * dvFwdRaw[i];
            idx++;
        }
        var nFiltered = nKept;
        var rmsLinearPre = Math.Sqrt(sumSqLinear / nFiltered);

        // Affine M re-fit on the clean inlier subset. The matching pipeline's
        // late-iter Kabsch fit ran against the loose-tolerance inlier set
        // (~13.5 px on the SoL master) which admitted ~10-15% wrong-star
        // pairings; their large residuals biased the CD matrix toward a
        // rotation/scale that doesn't actually fit the bulk of stars. Re-
        // fitting a 2x2 linear map M: (uDet, vDet) -> (uTrue, vTrue) on just
        // the MAD-cleaned inliers removes that bias, and absorbing M into
        // the CD matrix (CD_new = CD_old * M) leaves SIP only the genuine
        // non-linear distortion residual to capture -- which lets SIP clear
        // its 30% improvement gate on masters where the contaminated linear
        // fit alone would prevent it.
        // <para>
        // Translation is intentionally omitted from the affine: the median
        // bias step above already absorbed the dominant constant shift into
        // CRPIX, and reintroducing it here would force a second bias
        // adjustment to keep SIP's constant-term-free convention valid.
        // </para>
        var design = new double[nFiltered, 2];
        var uTrueArr = new double[nFiltered];
        var vTrueArr = new double[nFiltered];
        for (var k = 0; k < nFiltered; k++)
        {
            design[k, 0] = uDet[k];
            design[k, 1] = vDet[k];
            uTrueArr[k] = uDet[k] + duFwd[k];
            vTrueArr[k] = vDet[k] + dvFwd[k];
        }
        var affineRowU = PolynomialLeastSquares.Solve(design, uTrueArr);
        var affineRowV = PolynomialLeastSquares.Solve(design, vTrueArr);
        if (affineRowU is not null && affineRowV is not null)
        {
            // CD_new = CD_old * M, where M = [[a11, a12], [a21, a22]] and
            // affineRowU = (a11, a12), affineRowV = (a21, a22). Derivation:
            // sky_offset = CD_old * (uTrue, vTrue) = CD_old * M * (uDet, vDet);
            // we want sky_offset = CD_new * (uDet, vDet), so CD_new = CD_old * M.
            var a11 = affineRowU[0]; var a12 = affineRowU[1];
            var a21 = affineRowV[0]; var a22 = affineRowV[1];
            var oldCD11 = linearWcs.CD1_1; var oldCD12 = linearWcs.CD1_2;
            var oldCD21 = linearWcs.CD2_1; var oldCD22 = linearWcs.CD2_2;
            linearWcs = linearWcs with
            {
                CD1_1 = oldCD11 * a11 + oldCD12 * a21,
                CD1_2 = oldCD11 * a12 + oldCD12 * a22,
                CD2_1 = oldCD21 * a11 + oldCD22 * a21,
                CD2_2 = oldCD21 * a12 + oldCD22 * a22,
            };

            // Recompute (duFwd, dvFwd) under the refitted WCS. The catalog
            // sky is unchanged; only the WCS's pixel mapping shifted, so
            // SkyToPixel produces fresh uTrue values. Inliers stay the same
            // (we don't re-run MAD clip -- doing so risks a feedback loop
            // where the tightened fit recursively trims its own training set).
            double sumSqLinearPost = 0;
            for (var k = 0; k < nFiltered; k++)
            {
                var i = inlierIndices[k];
                var sky = matchedCatalogSky[i];
                var pred = linearWcs.SkyToPixel(sky.RA, sky.Dec);
                if (pred is null)
                {
                    // Catalog star fell behind the tangent plane under the
                    // refitted CD -- extremely unlikely for inliers, but if
                    // it happens we abandon the refit rather than partially
                    // updating duFwd/dvFwd.
                    sumSqLinearPost = double.NaN;
                    break;
                }
                var uTrueNew = pred.Value.X - linearWcs.CRPix1;
                var vTrueNew = pred.Value.Y - linearWcs.CRPix2;
                duFwd[k] = uTrueNew - uDet[k];
                dvFwd[k] = vTrueNew - vDet[k];
                sumSqLinearPost += duFwd[k] * duFwd[k] + dvFwd[k] * dvFwd[k];
            }

            if (!double.IsNaN(sumSqLinearPost))
            {
                var rmsLinearPost = Math.Sqrt(sumSqLinearPost / nFiltered);
                _logger?.LogDebug("CatalogPlateSolver SIP fit: affine refit dropped rms {Pre:F2} -> {Post:F2} px on {N} clean inliers (M=[[{A11:F5},{A12:F5}],[{A21:F5},{A22:F5}]])",
                    rmsLinearPre, rmsLinearPost, nFiltered, a11, a12, a21, a22);
                sumSqLinear = sumSqLinearPost;
            }
            else
            {
                _logger?.LogDebug("CatalogPlateSolver SIP fit: affine refit abandoned (catalog sky behind tangent plane under refitted CD)");
                // Don't trust the partially-mutated state; we can't easily
                // unwind the with-expression on linearWcs without re-running
                // SkyToPixel on every inlier under the original CD. Take the
                // robust path: bail on SIP entirely. The pre-refit linearWcs
                // is the caller's already-attached candidate which is fine.
                return linearWcs;
            }
        }
        else
        {
            _logger?.LogDebug("CatalogPlateSolver SIP fit: affine refit skipped (rank-deficient design on {N} inliers)", nFiltered);
        }
        var rmsLinear = Math.Sqrt(sumSqLinear / nFiltered);

        var fwdA = SipPolynomial.Fit(uDet, vDet, duFwd, SipOrder);
        var fwdB = SipPolynomial.Fit(uDet, vDet, dvFwd, SipOrder);
        if (fwdA is null || fwdB is null)
        {
            _logger?.LogDebug("CatalogPlateSolver SIP fit: forward fit failed (rank-deficient design, {N} matches, order {Order})", nFiltered, SipOrder);
            return linearWcs;
        }

        // Inverse SIP: given (u_true, v_true), recover (u_det, v_det). We fit
        // the inverse polynomial against the residual that takes the
        // POST-forward-corrected coordinate back to the observed pixel.
        // Crucially we evaluate at the *post-forward* coords (u + A(u, v),
        // v + B(u, v)) rather than the noisy (u_true_linear) targets, so
        // forward and inverse are consistent inverses by construction —
        // SkyToPixel then PixelToSky round-trips to within the polynomial's
        // own residual rather than to (noise_A + noise_AP).
        var uPostFwdArr = new double[nFiltered];
        var vPostFwdArr = new double[nFiltered];
        var duInv = new double[nFiltered];
        var dvInv = new double[nFiltered];
        for (var i = 0; i < nFiltered; i++)
        {
            var aHere = SipPolynomial.Apply(uDet[i], vDet[i], fwdA);
            var bHere = SipPolynomial.Apply(uDet[i], vDet[i], fwdB);
            uPostFwdArr[i] = uDet[i] + aHere;
            vPostFwdArr[i] = vDet[i] + bHere;
            // We want AP(uPostFwd, vPostFwd) = -aHere so that
            // SkyToPixel: (uPostFwd) + AP(uPostFwd) = uDet.
            duInv[i] = -aHere;
            dvInv[i] = -bHere;
        }
        var invAP = SipPolynomial.Fit(uPostFwdArr, vPostFwdArr, duInv, SipOrder);
        var invBP = SipPolynomial.Fit(uPostFwdArr, vPostFwdArr, dvInv, SipOrder);

        var candidate = linearWcs with
        {
            SipOrder = SipOrder,
            SipA = fwdA,
            SipB = fwdB,
            SipAP = invAP,
            SipBP = invBP,
        };

        // Sanity-check the fit by re-evaluating SkyToPixel on every clean
        // inlier (post outlier clip) and comparing to the detected centroid.
        // We reject SIP unless it brings the pixel-space RMS down by
        // *substantially* more than the overfit-noise floor — for N inliers
        // and K coefficients per axis, fitting pure noise reduces RMS by
        // ~sqrt(K/N), so we demand at least a 30% relative improvement to
        // be confident the polynomial captured a real distortion pattern
        // rather than centroid noise.
        double sumSqSip = 0;
        for (var k = 0; k < nFiltered; k++)
        {
            var i = inlierIndices[k];
            var det = matchedDetected[i];
            var corrected = candidate.SkyToPixel(matchedCatalogSky[i].RA, matchedCatalogSky[i].Dec);
            if (corrected is null)
            {
                return linearWcs;
            }
            var ddx = corrected.Value.X - det.X;
            var ddy = corrected.Value.Y - det.Y;
            sumSqSip += ddx * ddx + ddy * ddy;
        }
        var rmsSip = Math.Sqrt(sumSqSip / nFiltered);

        const double SipImprovementThreshold = 0.7;
        if (rmsSip > rmsLinear * SipImprovementThreshold)
        {
            _logger?.LogDebug("CatalogPlateSolver SIP fit: rejected (rms {Sip:F2} px vs linear {Lin:F2} px; needed ≤ {Threshold:F2} px, {N} clean of {Raw} raw, clip {Clip:F1} px)",
                rmsSip, rmsLinear, rmsLinear * SipImprovementThreshold, nFiltered, n, clipThreshold);
            return linearWcs;
        }

        _logger?.LogDebug("CatalogPlateSolver SIP fit: rms {Sip:F2} px (down from {Lin:F2} px linear, {N} clean of {Raw} raw, clip {Clip:F1} px, order {Order})",
            rmsSip, rmsLinear, nFiltered, n, clipThreshold, SipOrder);
        return candidate;
    }


    /// <summary>
    /// Computes the FITS CD matrix from the inverse affine transform and attaches it to the WCS.
    /// <para>
    /// The gnomonic projection maps sky offsets (ξ, η) in radians to pixel offsets as:
    /// <c>Δx = xSign · ξ / pixelScaleRad</c>, <c>Δy = −η / pixelScaleRad</c>.
    /// The inverse affine <paramref name="minv"/> maps detected pixels back to projected pixels,
    /// so the combined Jacobian ∂(RA,Dec)/∂(pixel) gives the CD matrix in degrees/pixel.
    /// </para>
    /// </summary>
    private static WCS AttachCDMatrix(WCS wcs, Matrix3x2? minv, double pixelScaleRad, double cx, double cy, ImageDim dim, double xSign)
    {
        if (minv is not { } inv)
        {
            return wcs;
        }

        // pixelScaleRad is the gnomonic scale: radians per pixel.
        // The projection is: xPix = cx + xSign * ξ/pixelScaleRad, yPix = cy - η/pixelScaleRad
        // So: ξ = xSign * (xPix - cx) * pixelScaleRad, η = -(yPix - cy) * pixelScaleRad
        // The inverse affine Minv maps from detected pixel to projected pixel via
        //   Vector2.Transform(det, Minv):
        //   projX = det.X * M11 + det.Y * M21 + M31
        //   projY = det.X * M12 + det.Y * M22 + M32
        // Chain rule gives CD matrix (degrees/pixel):
        //   CD1_1 = ∂u/∂dx = psd * xSign * M11,  CD1_2 = ∂u/∂dy = psd * xSign * M21
        //   CD2_1 = ∂v/∂dx = -psd * M12,          CD2_2 = ∂v/∂dy = -psd * M22
        var pixelScaleDeg = double.RadiansToDegrees(pixelScaleRad);

        return wcs with
        {
            CRPix1 = (dim.Width + 1) / 2.0,
            CRPix2 = (dim.Height + 1) / 2.0,
            CD1_1 = xSign * pixelScaleDeg * inv.M11,
            CD1_2 = xSign * pixelScaleDeg * inv.M21,
            CD2_1 = -pixelScaleDeg * inv.M12,
            CD2_2 = -pixelScaleDeg * inv.M22,
        };
    }

    /// <summary>
    /// Validates a WCS solution by projecting the brightest catalog stars through SkyToPixel
    /// and measuring the average distance to the nearest detected star. Lower = better orientation.
    /// </summary>
    private static double ReProjectionError(WCS wcs, List<(double RA, double Dec, double VMag)> catalogCoords, StarList detectedStars)
    {
        if (!wcs.HasCDMatrix || detectedStars.Count == 0)
        {
            return double.MaxValue;
        }

        // Image dimensions from CRPIX (center of 1-based image)
        var imgW = (wcs.CRPix1 - 0.5) * 2;
        var imgH = (wcs.CRPix2 - 0.5) * 2;

        double sumSqDist = 0;
        int matched = 0;

        // Use bright catalog stars that project within the image bounds
        for (int i = 0; i < catalogCoords.Count && matched < 20; i++)
        {
            var (ra, dec, _) = catalogCoords[i];
            if (wcs.SkyToPixel(ra, dec) is not { } px)
            {
                continue;
            }

            // Skip stars outside image bounds (1-based coordinates)
            if (px.X < 0.5 || px.X > imgW + 0.5 || px.Y < 0.5 || px.Y > imgH + 0.5)
            {
                continue;
            }

            // Convert from 1-based WCS to 0-based image coordinates
            var wcsX = (float)(px.X - 1.0);
            var wcsY = (float)(px.Y - 1.0);

            // Find nearest detected star
            var bestDistSq = float.MaxValue;
            foreach (var det in detectedStars)
            {
                var ddx = det.XCentroid - wcsX;
                var ddy = det.YCentroid - wcsY;
                var distSq = ddx * ddx + ddy * ddy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                }
            }

            sumSqDist += bestDistSq;
            matched++;
        }

        return matched > 0 ? Math.Sqrt(sumSqDist / matched) : double.MaxValue;
    }

    /// <summary>
    /// Propagates a candidate's J2000 RA/Dec to the image epoch via Tycho-2
    /// proper motion when one is available. Uses <c>obj.Index</c> so that
    /// cross-walked HIP/HD candidates (which arrive here with their TYC
    /// resolution already in <see cref="CelestialObject.Index"/>) pick up
    /// pm too, the same way SPCC's matcher does (see 9d933f8).
    /// </summary>
    private static (double Ra, double Dec) MaybePropagate(
        ICelestialObjectDB db, in CelestialObject obj, double dtJulianYears)
    {
        if (dtJulianYears == 0.0) return (obj.RA, obj.Dec);
        if (!db.TryGetTycho2Star(obj.Index, out var tyc)) return (obj.RA, obj.Dec);
        if (tyc.PmRaTenthMasPerYr == 0 && tyc.PmDecTenthMasPerYr == 0) return (obj.RA, obj.Dec);
        return CoordinateUtils.PropagatePm(
            obj.RA, obj.Dec,
            tyc.PmRaMasPerYr, tyc.PmDecMasPerYr,
            dtJulianYears);
    }

    private List<(double RA, double Dec, double VMag)> QueryCatalogStarsInRegion(WCS origin, double radiusDeg, double dtJulianYears)
    {
        var result = new List<(double RA, double Dec, double VMag)>();

        var centerRA = origin.CenterRA;     // hours
        var centerDec = origin.CenterDec;   // degrees

        // RA search radius in hours, adjusted for cos(dec)
        var cosDecl = Math.Cos(double.DegreesToRadians(centerDec));
        var radiusRA = cosDecl > 0.01 ? radiusDeg / (15.0 * cosDecl) : 24.0;

        var minRA = centerRA - radiusRA;
        var maxRA = centerRA + radiusRA;
        var minDec = Math.Max(-90.0, centerDec - radiusDeg);
        var maxDec = Math.Min(90.0, centerDec + radiusDeg);

        var seen = new HashSet<CatalogIndex>();

        // Polar-cap fast path: when the query covers the full 24h of RA the
        // per-cell scan re-walks the same handful of polar GSC regions hundreds
        // of times -- 200+ seconds for a single solve at the SCP. Detect this
        // case (cosDecl threshold matches the radiusRA = 24 branch above) and
        // delegate to Tycho2RaDecIndex.EnumerateStarsInDecBand, which scans each
        // unique region's binary entries exactly once. Mirrors the polar-pan
        // optimisation in commit 69c7266 ("Sky map pan: 5x faster when the pole
        // is in view"). Falls back to the per-cell path when Tycho-2 isn't
        // loaded or when we're not at the pole.
        if (cosDecl <= 0.01 && db.CoordinateGrid is CompositeRaDecIndex { Tycho2: { } tycho2 } composite)
        {
            // Deep-sky polar cells (cheap: a few dozen Dec cells x 360 RA cells,
            // most empty -- the primary index uses Array.Empty for empty cells).
            const double raCellSize = 1.0 / 15.0;
            for (var cellRA = raCellSize * 0.5; cellRA < 24.0; cellRA += raCellSize)
            {
                for (var cellDec = Math.Floor(minDec) + 0.5; cellDec <= maxDec; cellDec += 1.0)
                {
                    foreach (var idx in composite.Primary[cellRA, cellDec])
                    {
                        if (seen.Add(idx) && db.TryLookupByIndex(idx, out var obj) && obj.ObjectType is ObjectType.Star)
                        {
                            var (ra, dec) = MaybePropagate(db, obj, dtJulianYears);
                            result.Add((ra, dec, Half.IsNaN(obj.V_Mag) ? 99.0 : (double)obj.V_Mag));
                        }
                    }
                }
            }

            // Tycho-2 entries via the dec-band enumerator (regions deduped, single linear scan).
            foreach (var idx in tycho2.EnumerateStarsInDecBand(minDec, maxDec))
            {
                if (seen.Add(idx) && db.TryLookupByIndex(idx, out var obj) && obj.ObjectType is ObjectType.Star)
                {
                    var (ra, dec) = MaybePropagate(db, obj, dtJulianYears);
                    result.Add((ra, dec, Half.IsNaN(obj.V_Mag) ? 99.0 : (double)obj.V_Mag));
                }
            }

            return result;
        }

        // General path: per-cell scan with cos(dec)-divided RA range.
        // Grid cells are 1/15 hour in RA and 1° in Dec
        const double raCellSizeGeneral = 1.0 / 15.0;

        for (var cellRA = Math.Floor(minRA / raCellSizeGeneral) * raCellSizeGeneral + raCellSizeGeneral * 0.5;
             cellRA <= maxRA;
             cellRA += raCellSizeGeneral)
        {
            var queryRA = ConditionRA(cellRA);
            for (var cellDec = Math.Floor(minDec) + 0.5; cellDec <= maxDec; cellDec += 1.0)
            {
                foreach (var idx in db.CoordinateGrid[queryRA, cellDec])
                {
                    if (seen.Add(idx) && db.TryLookupByIndex(idx, out var obj) && obj.ObjectType is ObjectType.Star)
                    {
                        var (ra, dec) = MaybePropagate(db, obj, dtJulianYears);
                        result.Add((ra, dec, Half.IsNaN(obj.V_Mag) ? 99.0 : (double)obj.V_Mag));
                    }
                }
            }
        }

        return result;
    }

    private static List<ProjectedCatalogStar> ProjectCatalogStars(
        List<(double RA, double Dec, double VMag)> catalogCoords,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign
    )
    {
        var projected = new List<ProjectedCatalogStar>();

        var alpha0 = origin.CenterRA * HOURS2RADIANS;
        var (sinDelta0, cosDelta0) = Math.SinCos(double.DegreesToRadians(origin.CenterDec));

        var marginX = dim.Width * 0.1;
        var marginY = dim.Height * 0.1;

        foreach (var (ra, dec, _) in catalogCoords)
        {
            var alpha = ra * HOURS2RADIANS;
            var deltaAlpha = alpha - alpha0;

            var (sinDelta, cosDelta) = Math.SinCos(double.DegreesToRadians(dec));
            var cosDeltaAlpha = Math.Cos(deltaAlpha);

            var cosC = sinDelta0 * sinDelta + cosDelta0 * cosDelta * cosDeltaAlpha;
            if (cosC <= 0)
            {
                continue; // behind the tangent plane
            }

            var xi = cosDelta * Math.Sin(deltaAlpha) / cosC;
            var eta = (cosDelta0 * sinDelta - sinDelta0 * cosDelta * cosDeltaAlpha) / cosC;

            var xPix = (float)(cx + xSign * xi / pixelScaleRad);
            var yPix = (float)(cy - eta / pixelScaleRad);

            if (xPix >= -marginX && xPix <= dim.Width + marginX &&
                yPix >= -marginY && yPix <= dim.Height + marginY)
            {
                projected.Add(new ProjectedCatalogStar(
                    new ImagedStar(2f, 2f, 100f, 1000f, xPix, yPix, 0f), ra, dec));
            }
        }

        return projected;
    }

    private static WCS? InverseTanProject(
        Vector2 pixelPos,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        double xSign
    )
    {
        var alpha0 = origin.CenterRA * HOURS2RADIANS;
        var (sinDelta0, cosDelta0) = Math.SinCos(double.DegreesToRadians(origin.CenterDec));

        var xi = xSign * (pixelPos.X - cx) * pixelScaleRad;
        var eta = -(pixelPos.Y - cy) * pixelScaleRad;

        var rho = Math.Sqrt(xi * xi + eta * eta);

        if (rho < 1e-12)
        {
            return origin;
        }

        var (sinC, cosC) = Math.SinCos(Math.Atan(rho));

        var centerDec = double.RadiansToDegrees(Math.Asin(cosC * sinDelta0 + eta * sinC * cosDelta0 / rho));
        var centerRA = (alpha0 + Math.Atan2(xi * sinC, rho * cosDelta0 * cosC - eta * sinDelta0 * sinC)) * RADIANS2HOURS;

        return new WCS(ConditionRA(centerRA), centerDec);
    }
}
