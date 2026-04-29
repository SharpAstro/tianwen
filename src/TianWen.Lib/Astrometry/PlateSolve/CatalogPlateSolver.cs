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
internal sealed class CatalogPlateSolver(ICelestialObjectDB db, ILogger<CatalogPlateSolver>? logger = null) : IPlateSolver
{
    private readonly ILogger? _logger = logger;

    const int MinStarsForMatch = 6;

    public string Name => "Catalog plate solver";

    public float Priority => 0.99f;

    private readonly record struct SolveAttempt(WCS? Wcs, int ProjectedStars, int MatchedStars, int Iterations, double RmsResidual, double AffineDeterminant);

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

        // Query catalog stars within search radius
        var stageSw = Stopwatch.StartNew();
        var catalogCoords = QueryCatalogStarsInRegion(origin, searchRadiusDeg);
        _logger?.LogDebug("CatalogPlateSolver: catalog query {Count} stars in {Ms}ms (Dec={Dec:F2}°, R={R:F2}°)",
            catalogCoords.Count, stageSw.Elapsed.TotalMilliseconds, origin.CenterDec, searchRadiusDeg);
        _catalogStars = catalogCoords.Count;
        if (catalogCoords.Count < MinStarsForMatch)
        {
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
        const double TargetPixelScaleArcsec = 1.5;
        if (dim.PixelScale > 0 && dim.PixelScale < TargetPixelScaleArcsec)
        {
            // Round (not floor) so a 0.97"/px image gets binned 2x to 1.94"/px.
            // floor(1.5/0.97) = 1 = no bin -- we'd never trigger on
            // sub-arcsec/px setups that need it most.
            detectionScale = (int)Math.Round(TargetPixelScaleArcsec / dim.PixelScale);
            if (detectionScale > 1)
            {
                stageSw.Restart();
                detectionImage = image.Downsample(detectionScale);
                _logger?.LogDebug("CatalogPlateSolver: downsampled {SrcW}x{SrcH} -> {DstW}x{DstH} (factor {Factor}, target {Target}\"/px) in {Ms}ms",
                    image.Width, image.Height, detectionImage.Width, detectionImage.Height, detectionScale, TargetPixelScaleArcsec, stageSw.Elapsed.TotalMilliseconds);
            }
        }

        // Detect stars in image. minStars=50 short-circuits the do-while retry
        // loop in FindStarsAsync as soon as we have enough stars to attempt
        // matching (MinStarsForMatch is 6; 50 is comfortable redundancy). On
        // synthetic SCP frames the first pass already yields >200 stars, so
        // the retry loop never fires.
        stageSw.Restart();
        var detectedStars = await detectionImage.FindStarsAsync(0, snrMin: 5f, maxStars: 500, minStars: 50, cancellationToken: cancellationToken);
        _logger?.LogDebug("CatalogPlateSolver: FindStarsAsync detected {Count} stars in {Ms}ms ({W}x{H})",
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
        _logger?.LogDebug("CatalogPlateSolver: matching iterations std={StdMatched}/{StdIter} mirror={MirrorMatched}/{MirrorIter} in {Ms}ms",
            std.MatchedStars, std.Iterations, mirror.MatchedStars, mirror.Iterations, stageSw.Elapsed.TotalMilliseconds);

        // Validate each by re-projecting bright catalog stars through the WCS
        var stdError = std.Wcs is { } ws ? ReProjectionError(ws, catalogCoords, detectedStars) : double.MaxValue;
        var mirrorError = mirror.Wcs is { } wm ? ReProjectionError(wm, catalogCoords, detectedStars) : double.MaxValue;

        return Result(stdError <= mirrorError ? std : mirror);
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
                ImagedStar? bestMatch = null;

                for (int catRank = 0; catRank < maxProjectedForMatching; catRank++)
                {
                    var cat = projected[catRank];
                    var dx = det.XCentroid - cat.XCentroid;
                    var dy = det.YCentroid - cat.YCentroid;
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
                    matchedProjected.Add(new Vector2(bm.XCentroid, bm.YCentroid));
                }
            }

            if (matchedDetected.Count < MinStarsForMatch)
            {
                projectedCount = projected.Count;
                matchedCount = matchedDetected.Count;
                iterCount = iteration + 1;
                return MakeResult(iteration > 0 ? AttachCDMatrix(currentOrigin, hasMinv ? lastMinv : default(Matrix3x2?), pixelScaleRad, cx, cy, dim, xSign) : null);
            }

            // Early stop if match count dropped significantly (diverging)
            if (iteration > 0 && matchedDetected.Count < peakMatchCount / 2)
            {
                break;
            }
            peakMatchCount = Math.Max(peakMatchCount, matchedDetected.Count);

            // Compute offset using Matrix3x2 affine fit (handles translation + rotation)
            var M = Matrix3x2.FitAffineTransform(CollectionsMarshal.AsSpan(matchedProjected), CollectionsMarshal.AsSpan(matchedDetected));
            if (M is null || !Matrix3x2.Invert(M.Value, out var Minv))
            {
                return MakeResult(iteration > 0 ? AttachCDMatrix(currentOrigin, hasMinv ? lastMinv : default(Matrix3x2?), pixelScaleRad, cx, cy, dim, xSign) : null);
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
                return MakeResult(iteration > 0 ? AttachCDMatrix(currentOrigin, lastMinv, pixelScaleRad, cx, cy, dim, xSign) : null);
            }

            // Check convergence
            var dRA = Math.Abs(refinedWcs.CenterRA - currentOrigin.CenterRA) * 15.0;
            var dDec = Math.Abs(refinedWcs.CenterDec - currentOrigin.CenterDec);


            currentOrigin = refinedWcs;

            projectedCount = projected.Count;
            matchedCount = matchedDetected.Count;
            iterCount = iteration + 1;

            if (dRA < 1e-6 && dDec < 1e-6)
            {
                break;
            }
        }

        return MakeResult(AttachCDMatrix(currentOrigin, hasMinv ? lastMinv : default(Matrix3x2?), pixelScaleRad, cx, cy, dim, xSign));
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

    private List<(double RA, double Dec, double VMag)> QueryCatalogStarsInRegion(WCS origin, double radiusDeg)
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
                            result.Add((obj.RA, obj.Dec, Half.IsNaN(obj.V_Mag) ? 99.0 : (double)obj.V_Mag));
                        }
                    }
                }
            }

            // Tycho-2 entries via the dec-band enumerator (regions deduped, single linear scan).
            foreach (var idx in tycho2.EnumerateStarsInDecBand(minDec, maxDec))
            {
                if (seen.Add(idx) && db.TryLookupByIndex(idx, out var obj) && obj.ObjectType is ObjectType.Star)
                {
                    result.Add((obj.RA, obj.Dec, Half.IsNaN(obj.V_Mag) ? 99.0 : (double)obj.V_Mag));
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
                        result.Add((obj.RA, obj.Dec, Half.IsNaN(obj.V_Mag) ? 99.0 : (double)obj.V_Mag));
                    }
                }
            }
        }

        return result;
    }

    private static List<ImagedStar> ProjectCatalogStars(
        List<(double RA, double Dec, double VMag)> catalogCoords,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign
    )
    {
        var projected = new List<ImagedStar>();

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
                projected.Add(new ImagedStar(2f, 2f, 100f, 1000f, xPix, yPix));
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
