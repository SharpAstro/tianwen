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
/// <para><b>Dense-field hardening</b> (each attempt): a geometric pair-lock seed
/// (<see cref="PairRansacLock"/>) locks translation + rotation + scale from bright star pairs
/// verified against the whole detected field before the proximity iterations run, and each
/// iteration's matching predicts projected positions through the latest affine's linear part so
/// field rotation cannot starve the shrinking tolerance at the frame edges. A final
/// <b>chance-aware acceptance gate</b> counts bright detected stars landing within a few px of a
/// catalog star under the final WCS and rejects any solution that cannot beat the Poisson
/// expectation of random alignment: the dense-field failure mode produced 1,400+ "matches" that
/// were pure nearest-neighbour noise while reporting confident success.</para>
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
    /// Acceptance-gate probe radius in native pixels (scaled up by the detection binning
    /// factor, whose centroid quantisation it must dominate). A genuine solve puts its true
    /// matches within ~1px of the catalog projection; the dense-field failure mode puts them
    /// at the field's random nearest-neighbour distance (tens of px), so 3px separates the
    /// two regimes with a wide margin on both sides.
    /// </summary>
    private const double GateTolerancePx = 3.0;

    /// <summary>
    /// How many rungs of the proximity-match tolerance schedule a successful pair-lock seed skips.
    /// </summary>
    /// <remarks>
    /// The seed verifies its transform at <c>PairRansacLock</c>'s 4 px radius across the whole frame,
    /// so the first rung it justifies is the 0.3%-of-diagonal one (15 px on a 4164x2795 field) rather
    /// than the blind 10% rung (229 px). Three rungs, not more: the point is to enter the loop at a
    /// tolerance the seed has earned, while still leaving the loop room to tighten.
    /// </remarks>
    private const int SeededToleranceRungOffset = 3;

    /// <summary>
    /// Acceptance-gate sample: the brightest N detected stars. Kept to the bright end where
    /// Tycho-2 is complete, so every genuine detected star in the sample has a catalog
    /// counterpart and a real solve scores near 100%.
    /// </summary>
    private const int GateSampleSize = 120;

    /// <summary>Accepted gate hits must exceed this multiple of the Poisson chance expectation.</summary>
    private const double GateChanceSafetyFactor = 5.0;

    /// <summary>
    /// How far clear of the Poisson chance rate a seed must be before it is allowed to STOP the
    /// other parity's search. <see cref="PairRansacLock"/> already refuses to return a lock below
    /// 5x chance, so this is deliberately stricter than merely "it locked": cancelling is an
    /// irreversible-looking act on a hot path, and the fallback that undoes it costs a whole
    /// re-run. Measured across the 96 frozen Vela frames, exactly one parity locks on every one of
    /// them and never both, so a correct winner has never been within reach of this bar.
    /// </summary>
    private const double ParityClaimChanceFactor = 10.0;

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
    /// Test seam: what the parity race did on the most recent solve. Phase A's whole effect is work
    /// that DOES NOT happen, which no output can show -- the WCS is identical either way -- so
    /// without this a test can only assert the solve still works, never that the saving occurred.
    /// </summary>
    internal ParityRaceOutcome LastParityRace { get; private set; }

    /// <summary>
    /// Test seam: what the quad scale recovery answered on the most recent solve, or <c>null</c> if
    /// it declined. Reported for the same reason as <see cref="LastParityRace"/> -- the effect is a
    /// narrower search, so the WCS is identical whether the recovery fired or not and no output can
    /// show that it did.
    /// </summary>
    internal QuadScaleRecovery.Recovery? LastScalePrior { get; private set; }

    /// <param name="AbandonedAParity">One parity was stopped because the other seeded clear of chance.</param>
    /// <param name="ReRanAbandonedParity">The gate needed the abandoned half after all, so it was re-run.</param>
    /// <param name="WinnerIsStd">
    /// Whether the <c>xSign = +1</c> attempt won. Reported because everything downstream of the pick
    /// -- which half may be abandoned, which sign to re-run -- is selected off this, and a solve
    /// looks identical from the outside whichever way it went. A test that never sees the mirror win
    /// leaves that whole half of the branch unexercised.
    /// </param>
    internal readonly record struct ParityRaceOutcome(bool AbandonedAParity, bool ReRanAbandonedParity, bool WinnerIsStd);

    /// <summary>
    /// Catalog-star projection result with the originating sky coordinates
    /// attached, so the matching loop can collect (detected pixel, catalog
    /// RA/Dec) pairs without a second pass over the catalog.
    /// </summary>
    internal readonly record struct ProjectedCatalogStar(ImagedStar Pixel, double RA, double Dec);

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

        // Format-agnostic on purpose. This is reached from the viewer, which solves whatever
        // document is open -- a TIFF, a RAW, a .fz -- not just a FITS. TryReadFitsFile THROWS on a
        // non-FITS file (FITS.Lib reports the bad magic as an IOException), and that exception used
        // to escape PlateSolverFactory's loop and take down the whole fallback chain, so ASTAP never
        // got a turn on a file it would have converted and solved.
        if (!Image.TryReadImageFile(fitsFile, out var image, out var fileWcs))
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

        // The search origin doubles as a scale source. A frame that carries a solved CD matrix but no
        // FOCALLEN -- which is every master TianWen writes -- otherwise fails HERE, in a few ms,
        // before detecting a star, with its own pixel scale sitting unread in the same header.
        if ((imageDim ?? image.GetImageDim(searchOrigin)) is not { } dim)
        {
            _logger.LogWarning(
                "CatalogPlateSolver: no pixel scale available -- the frame states no PIXSCALE, no FOCALLEN + pixel size, and the search origin carries no CD matrix. Pass an explicit ImageDim.");
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
        // maxFirstPassNoiseSigma is the OTHER half of that trade, and it is asked for HERE rather than
        // being the detector's default. With retries pinned off, the first pass is the only pass, and it
        // starts at the histogram's star level -- which extended bright signal owns. On a 60 s M42 Ha sub
        // that meant a 94-sigma first pass, 8 stars, and a solve that failed by one pair-lock hit while
        // the catalogue was offering 63 anchors; capped at the ladder's own top rung it finds 15 and
        // locks with a 0.70 px mirror rms. A solver wants every star it can get and reads their
        // positions in aggregate, so the faint end it admits is worth having -- which is not true of the
        // detector's other callers (see Image.MaxFirstPassNoiseSigma for what capping them cost).
        stageSw.Restart();
        var detectedStars = await detectionImage.FindStarsAsync(detectionImage.ReferenceStarChannel, snrMin: 5f, maxStars: 500, minStars: 50, maxRetries: 0, maxFirstPassNoiseSigma: Image.MaxFirstPassNoiseSigma, logger: _logger, cancellationToken: cancellationToken);
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

        // Recover the plate scale from the stars, ONCE for both parities. A quad descriptor is five
        // scale-free ratios, so this needs no prior -- and it is parity-BLIND, because reflection
        // preserves distances just as rotation does, so the two attempts would compute the identical
        // answer twice. Refusal is normal and simply leaves each attempt on the header's scale.
        stageSw.Restart();
        var scalePriorDetected = new List<ImagedStar>(detectedStars);
        scalePriorDetected.Sort(static (a, b) => b.Flux.CompareTo(a.Flux));
        var scalePriorPoints = new Vector2[scalePriorDetected.Count];
        for (var i = 0; i < scalePriorDetected.Count; i++)
        {
            scalePriorPoints[i] = new Vector2(scalePriorDetected[i].XCentroid, scalePriorDetected[i].YCentroid);
        }

        var scalePriorProjected = ProjectCatalogStars(catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign: 1.0);
        var scalePriorProjectedPoints = new Vector2[scalePriorProjected.Count];
        for (var i = 0; i < scalePriorProjected.Count; i++)
        {
            scalePriorProjectedPoints[i] = new Vector2(
                scalePriorProjected[i].Pixel.XCentroid, scalePriorProjected[i].Pixel.YCentroid);
        }

        var scalePrior = QuadScaleRecovery.TryRecover(scalePriorPoints, scalePriorProjectedPoints, _logger);
        LastScalePrior = scalePrior;
        _logger.LogDebug("CatalogPlateSolver: quad scale recovery {Outcome} in {Ms}ms",
            scalePrior is { } sp
                ? $"ratio {sp.Ratio:F5} (implied {dim.PixelScale / sp.Ratio:F4}\"/px against the header's {dim.PixelScale:F4})"
                : "declined",
            stageSw.Elapsed.TotalMilliseconds);

        // Try both orientations in parallel; pick the one with lower re-projection error.
        //
        // Exactly one of the two is real work: measured over the 96 frozen Vela frames, one parity
        // locks on every single frame and never both, while the loser spends 259.5M hypotheses
        // against the winner's 8.1M -- 97% of the seed's whole cost. So each attempt is given a
        // token the OTHER one can cancel, and the first seed clear of chance stops its sibling.
        //
        // Task.Run keeps the OUTER token, never the per-attempt one: a task started with an
        // already-cancelled token never runs its delegate and faults the await, which would turn
        // the winner's success into the whole solve's failure. Cancellation is observed INSIDE
        // instead, where it returns a WCS-less attempt like any other dead end.
        stageSw.Restart();
        using var stdCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var mirrorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // At most ONE attempt may ever be abandoned. Both parities clearing the bar at once should
        // be impossible -- it has not happened on any of the 96 frozen frames -- but if it did, two
        // unguarded claims would cancel each OTHER and the solve would lose both halves and report
        // failure on a frame it can solve. The interlock makes that outcome unreachable rather than
        // unlikely, which is the only acceptable standard for a fast path guarding a correct one.
        var parityClaimed = 0;
        Action ClaimAgainst(CancellationTokenSource sibling) => () =>
        {
            if (Interlocked.CompareExchange(ref parityClaimed, 1, 0) == 0)
            {
                sibling.Cancel();
            }
        };

        var stdTask = Task.Run(() => TrySolveWithProximityMatching(detectedStars, catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign: 1.0, range, scalePrior, ClaimAgainst(mirrorCts), stdCts.Token), cancellationToken);
        var mirrorTask = Task.Run(() => TrySolveWithProximityMatching(detectedStars, catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign: -1.0, range, scalePrior, ClaimAgainst(stdCts), mirrorCts.Token), cancellationToken);
        await Task.WhenAll(stdTask, mirrorTask);

        var std = stdTask.Result;
        var mirror = mirrorTask.Result;
        var stdAbandoned = stdCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
        var mirrorAbandoned = mirrorCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
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
        // Which of the two won is tracked as a FLAG, never by reference identity: SolveAttempt is a
        // readonly record struct, so ReferenceEquals against it boxes and is unconditionally false.
        SolveAttempt winner, loser;
        bool winnerIsStd;
        if (std.MatchedStars >= 2 * Math.Max(mirror.MatchedStars, 1))
        {
            (winner, loser, winnerIsStd) = (std, mirror, true);
        }
        else if (mirror.MatchedStars >= 2 * Math.Max(std.MatchedStars, 1))
        {
            (winner, loser, winnerIsStd) = (mirror, std, false);
        }
        else
        {
            // Close in match count -- both parities found roughly the same set.
            // Re-projection error then picks the one whose CD matrix actually
            // projects bright catalog stars onto detected stars.
            var stdError = std.Wcs is { } ws ? ReProjectionError(ws, catalogCoords, detectedStars) : double.MaxValue;
            var mirrorError = mirror.Wcs is { } wm ? ReProjectionError(wm, catalogCoords, detectedStars) : double.MaxValue;
            winnerIsStd = stdError <= mirrorError;
            (winner, loser) = winnerIsStd ? (std, mirror) : (mirror, std);
        }

        // Chance-aware acceptance gate. In a dense field the proximity loop can hand back a
        // full complement of matches that are pure nearest-neighbour noise -- observed on a
        // Vela mosaic panel: 1,434 "matches" whose residual median (19.2px) and MAD (6.9px)
        // reproduced the Poisson random-NN prediction (19.5 / 7.0) exactly, i.e. the solve
        // matched nothing while reporting confident success. A genuine lock is separable by
        // one cheap statistic: bright detected stars land within a few px of a catalog star
        // under the final WCS at ~100x the chance rate. Reject anything that cannot beat
        // chance -- a failed solve is recoverable (the factory falls through to ASTAP /
        // astrometry.net), a silently wrong WCS is not.
        var tolerance = Math.Max(GateTolerancePx, GateTolerancePx * detectionScale);
        LastParityRace = new ParityRaceOutcome(stdAbandoned || mirrorAbandoned, false, winnerIsStd);

        // The gate can OVERTURN the parity pick, so it needs a real loser to fall back on -- and the
        // loser may be an attempt we abandoned mid-seed, whose null WCS means "never finished
        // looking", not "nothing there". Re-run it, uncancelled, in that one case. This is the
        // fallback that makes cancelling safe rather than a gamble: it costs a full extra attempt,
        // and it is reached only when the winner has already failed the chance test, which on a
        // solvable frame is rare and on an unsolvable one was going to fail anyway.
        var loserAbandoned = winnerIsStd ? mirrorAbandoned : stdAbandoned;
        if (loserAbandoned && loser.Wcs is null
            && !WouldPassAcceptanceGate(winner, catalogCoords, detectedStars, dim, tolerance))
        {
            _logger.LogDebug("CatalogPlateSolver: winner failed the acceptance gate and the other parity was abandoned mid-seed; re-running it before reporting failure");
            var loserXSign = winnerIsStd ? -1.0 : 1.0;
            loser = TrySolveWithProximityMatching(detectedStars, catalogCoords, origin, pixelScaleRad,
                cx, cy, dim, loserXSign, range, scalePrior, onSeedClearOfChance: null, cancellationToken);
            LastParityRace = LastParityRace with { ReRanAbandonedParity = true };
        }

        winner = ApplyAcceptanceGate(winner, loser, catalogCoords, detectedStars, dim, tolerance);

        return Result(winner);
    }

    /// <summary>
    /// Whether <paramref name="attempt"/> would survive <see cref="ApplyAcceptanceGate"/> on its own
    /// merits. Split out so the caller can ask BEFORE deciding whether it needs to re-run an
    /// abandoned parity, without the gate's logging or its fallback running twice.
    /// </summary>
    private static bool WouldPassAcceptanceGate(
        SolveAttempt attempt,
        List<(double RA, double Dec, double VMag)> catalogCoords,
        StarList detectedStars,
        ImageDim dim,
        double tolerancePx)
    {
        if (attempt.Wcs is not { } wcs)
        {
            return false;
        }

        var v = CountTightMatches(wcs, catalogCoords, detectedStars, dim, tolerancePx);
        return v.Hits >= Math.Max(MinStarsForMatch, GateChanceSafetyFactor * v.ExpectedChance);
    }

    /// <summary>
    /// Verifies the picked parity against the chance model; falls back to the losing parity
    /// when it verifies instead, and strips the WCS entirely when neither does.
    /// </summary>
    private SolveAttempt ApplyAcceptanceGate(
        SolveAttempt winner,
        SolveAttempt loser,
        List<(double RA, double Dec, double VMag)> catalogCoords,
        StarList detectedStars,
        ImageDim dim,
        double tolerancePx)
    {
        if (winner.Wcs is not { } wcs)
        {
            return winner;
        }

        var v = CountTightMatches(wcs, catalogCoords, detectedStars, dim, tolerancePx);
        var threshold = Math.Max(MinStarsForMatch, GateChanceSafetyFactor * v.ExpectedChance);
        if (v.Hits >= threshold)
        {
            _logger.LogDebug("CatalogPlateSolver: acceptance gate passed -- {Hits}/{Sampled} bright detected stars within {Tol:F1}px of a catalog star ({Chance:F1} expected by chance)",
                v.Hits, v.Sampled, tolerancePx, v.ExpectedChance);
            return winner;
        }

        if (loser.Wcs is { } loserWcs)
        {
            var lv = CountTightMatches(loserWcs, catalogCoords, detectedStars, dim, tolerancePx);
            if (lv.Hits >= Math.Max(MinStarsForMatch, GateChanceSafetyFactor * lv.ExpectedChance))
            {
                _logger.LogInformation("CatalogPlateSolver: parity pick overturned by the acceptance gate -- winner scored {WHits}, other parity {LHits}/{Sampled} within {Tol:F1}px",
                    v.Hits, lv.Hits, lv.Sampled, tolerancePx);
                return loser;
            }
        }

        _logger.LogWarning("CatalogPlateSolver: solve REJECTED by the acceptance gate -- {Hits} of {Sampled} bright detected stars have a catalog star within {Tol:F1}px under the final WCS, vs {Chance:F1} expected by chance (threshold {Threshold:F1}); the match set is indistinguishable from noise, reporting failure instead of a wrong WCS",
            v.Hits, v.Sampled, tolerancePx, v.ExpectedChance, threshold);
        return winner with { Wcs = null };
    }

    /// <summary>
    /// Counts how many of the brightest detected stars have a catalog star within
    /// <paramref name="tolerancePx"/> when the catalog is projected through the final WCS
    /// (CD matrix + SIP), alongside the Poisson expectation of that count under a random
    /// alignment -- the discriminator between a genuine lock and dense-field NN noise.
    /// </summary>
    private static (int Hits, int Sampled, int InFrame, double ExpectedChance) CountTightMatches(
        WCS wcs,
        List<(double RA, double Dec, double VMag)> catalogCoords,
        StarList detectedStars,
        ImageDim dim,
        double tolerancePx)
    {
        var inFrame = new List<Vector2>(catalogCoords.Count / 2);
        foreach (var (ra, dec, _) in catalogCoords)
        {
            if (wcs.SkyToPixel(ra, dec) is not { } px)
            {
                continue;
            }
            // NO origin shift. The WCS this gate is handed was built by AttachCDMatrix
            // from the affine that maps PROJECTED pixels onto DETECTED CENTROIDS, and
            // CRVAL is re-derived per iteration as the sky at the frame-centre pixel in
            // that same detected space -- so SkyToPixel already answers in centroid
            // coordinates. Subtracting 1 for a nominal 1-based FITS convention (which
            // this line used to do, copying ReProjectionError) injected a constant
            // (+0.91, +0.89) px bias: measured over 1,209 mutual matches on Vela panel 3
            // and 1,225 on panel 11, a shift sweep put the mean residual at (-0.07, -0.10)
            // px unshifted and grew monotonically with any shift applied. That bias ate
            // 1.27 px of a 3 px tolerance, diluting exactly the count this gate decides on.
            var x = (float)px.X;
            var y = (float)px.Y;
            if (x < -0.5f || x > dim.Width - 0.5f || y < -0.5f || y > dim.Height - 0.5f)
            {
                continue;
            }
            inFrame.Add(new Vector2(x, y));
        }
        if (inFrame.Count == 0)
        {
            return (0, 0, 0, 0);
        }

        var grid = new PairRansacLock.PointGrid(CollectionsMarshal.AsSpan(inFrame), dim.Width, dim.Height, (float)tolerancePx);

        var ranked = new List<ImagedStar>(detectedStars);
        ranked.Sort((a, b) => b.Flux.CompareTo(a.Flux));
        var sampled = Math.Min(GateSampleSize, ranked.Count);
        var hits = 0;
        for (var i = 0; i < sampled; i++)
        {
            if (grid.HasWithin(ranked[i].XCentroid, ranked[i].YCentroid))
            {
                hits++;
            }
        }

        var expected = sampled * (inFrame.Count / ((double)dim.Width * dim.Height)) * Math.PI * tolerancePx * tolerancePx;
        return (hits, sampled, inFrame.Count, expected);
    }

    /// <summary>
    /// Floor on the scale tolerance the pair-lock seed searches with, as a fraction.
    /// </summary>
    /// <remarks>
    /// <para><b>A pair length is not scale-free, so the seed needs a scale prior and this is how wrong
    /// that prior may be.</b> <c>PairRansacLock</c> admits a catalog pair for a detected pair when its
    /// length lands in <c>[dDet/(1+tol) - 3, dDet/(1-tol) + 3]</c> px. The +/-3 px is ABSOLUTE, so it
    /// forgives a FRACTIONAL error only on short baselines, and <c>MinBaselineFraction</c> forbids
    /// those -- 601 px on a 3008 px frame. On a wide frame a scale prior outside the tolerance means
    /// the true pair is never admitted at all, so the failure is total rather than gradual.</para>
    /// <para><b>Why 5% and not the 3% this used to be.</b> The prior comes from
    /// <see cref="Image.GetImageDim"/>, which falls back to <c>FOCALLEN</c> when a frame declares no
    /// <c>PIXSCALE</c> -- and FOCALLEN is whatever a human typed. The two errors measured here are a
    /// 202.5 mm SV545 entered as 205 (1.2%, inside the old window) and a 130 mm lens entered as its
    /// MARKETED 135 (3.9%, outside it by 0.9 of a percentage point, so a 3,065-star frame with 1,197
    /// catalogue stars in it did not solve at all). Marketed-versus-actual focal length is a
    /// systematic error, not a typo, so it will recur; 5% covers it with margin.</para>
    /// <para>ASTAP does not need this at all, and the difference is structural rather than a matter of
    /// tuning: it matches QUADS, whose descriptor is the two inner stars expressed in the frame of the
    /// two outer ones, i.e. pure ratios, which are invariant under scale. Two points can only give a
    /// distance and an orientation, and a distance has units. Widening the window is what a
    /// pair-based seed can do instead.</para>
    /// </remarks>
    private const float MinPairLockScaleTolerance = 0.05f;

    /// <summary>
    /// Scale window the seed is given once the scale came from the STARS rather than the header.
    /// </summary>
    /// <remarks>
    /// <para>Measured over the 48 parity attempts of the frozen Vela mosaic, sweeping the window
    /// against both a declared and a recovered centre. Against the DECLARED scale -- itself 0.26-0.31%
    /// off the solved one on every panel -- locks hold to +/-0.5% (23 of 48) and then collapse: 10 at
    /// +/-0.25%, 2 at +/-0.1%. Against a recovered centre all three hold 23, and hypotheses fall
    /// monotonically: 24.9M at +/-5%, 5.2M at +/-0.5%, 3.4M at +/-0.25%, 2.2M at +/-0.1%. So the
    /// collapse was never a floor in the search, it was the window excluding the truth.</para>
    /// <para><b>0.25% rather than the 0.1% the measurement also permits</b>, because the sweep
    /// centred on the frame's own SOLVED scale while <see cref="QuadScaleRecovery"/> delivers 0.065%
    /// worst-case on 23 of 24 panels: 0.1% would leave 0.035 percentage points of slack over a
    /// one-dataset error estimate, where 0.25% leaves 0.185 -- 3.8x margin for 7.4x fewer hypotheses.
    /// The remaining 1.5x is not worth spending the margin on, and an exact prior (a scale carried
    /// forward from a previous solve, the way parity is) is the thing that would earn it.</para>
    /// </remarks>
    private const float RecoveredScaleTolerance = 0.0025f;

    private SolveAttempt TrySolveWithProximityMatching(
        StarList detectedStars,
        List<(double RA, double Dec, double VMag)> catalogCoords,
        WCS origin,
        double hintPixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign,
        float scaleRange,
        QuadScaleRecovery.Recovery? scalePrior,
        Action? onSeedClearOfChance,
        CancellationToken cancellationToken
    )
    {
        // The scale this attempt WORKS in. It starts as the header's and becomes the star-recovered
        // one if and only if a seed locks against that projection -- and then everything downstream
        // must use the same value, because the seed's transform, the origin InverseTanProject
        // derives from it and the CD matrix AttachCDMatrix builds all live in one projection's
        // space. Correcting the seed's scale alone would mix two, which is the shape of both bugs
        // the acceptance gate found (the origin-convention bias and the SIP reference-pixel
        // mismatch).
        var pixelScaleRad = hintPixelScaleRad;

        var currentOrigin = origin;
        Matrix3x2 lastMinv = default;
        var hasMinv = false;
        var peakMatchCount = 0;
        int projectedCount = 0, matchedCount = 0, iterCount = 0;
        double rmsResidual = 0, affineDet = 0;

        SolveAttempt MakeResult(WCS? wcs) => new SolveAttempt(wcs, projectedCount, matchedCount, iterCount, rmsResidual, affineDet);

        // Rank detected stars by flux once (brightest first); the geometric seed and every
        // matching iteration below consume this ordering.
        var rankedDetected = new List<ImagedStar>(detectedStars);
        rankedDetected.Sort((a, b) => b.Flux.CompareTo(a.Flux));

        // The latest raw-projected -> detected affine. Its LINEAR part predicts where a
        // projection should land before matching (rotation / scale aware); the translation is
        // absorbed by the origin update each iteration, so predictions anchor at the frame
        // centre. Without this, the shrinking tolerance schedule kills true matches at the
        // frame edges of any field rotated more than ~0.1 degrees, because projections are
        // axis-aligned and rotation only ever lived in the final CD matrix.
        Matrix3x2? lastFitted = null;
        var seeded = false;

        // Geometric pair-lock seed (see PairRansacLock): locks translation + rotation + scale
        // from bright-pair hypotheses verified against the whole detected field, immune to the
        // dense-field failure mode where the bright-phase proximity iterations latch onto
        // nearest-neighbour noise and freeze the WCS at the unrefined hint. No lock -> proceed
        // exactly as before (sparse fields are proximity matching's home turf).
        if (rankedDetected.Count >= MinStarsForMatch)
        {
            var detPts = new Vector2[rankedDetected.Count];
            for (var i = 0; i < rankedDetected.Count; i++)
            {
                detPts[i] = new Vector2(rankedDetected[i].XCentroid, rankedDetected[i].YCentroid);
            }

            // Two tiers, tried in order of how well the scale is known.
            //
            // A star-recovered scale earns a window 20x narrower than a typed focal length does
            // (see RecoveredScaleTolerance), and the window's width multiplies the candidate pairs
            // every hypothesis is drawn from. When it locks, this attempt adopts that scale.
            PairRansacLock.LockResult? seedLock = null;
            if (scalePrior is { } recovery)
            {
                var recoveredPixelScaleRad = hintPixelScaleRad / recovery.Ratio;
                seedLock = TrySeedPairLock(catalogCoords, detPts, currentOrigin, recoveredPixelScaleRad,
                    cx, cy, dim, xSign, RecoveredScaleTolerance, out _, _logger, cancellationToken);
                if (seedLock is not null)
                {
                    pixelScaleRad = recoveredPixelScaleRad;
                }
                else
                {
                    _logger.LogDebug("CatalogPlateSolver: no seed at the recovered scale (xSign={XSign}, ratio {Ratio:F5}, +/-{Tol:P2}); retrying on the header's scale",
                        xSign, recovery.Ratio, RecoveredScaleTolerance);
                }
            }

            // The header's scale and the window its unreliability demands. Reached with no
            // recovery, and as the RETRY when a recovery did not pan out -- which is what makes the
            // narrow window safe to try at all: a stale, mis-measured or simply unlucky scale costs
            // one extra pass that is cheap by construction (2.2M hypotheses against 24.9M) and can
            // never turn a solvable frame into a failure.
            seedLock ??= TrySeedPairLock(catalogCoords, detPts, currentOrigin, pixelScaleRad, cx, cy,
                dim, xSign, Math.Max(scaleRange, MinPairLockScaleTolerance), out _, _logger, cancellationToken);

            // A seed this far clear of chance settles the parity, so tell the sibling to stop --
            // deliberately BEFORE the invert/project below, because a lock is evidence about the
            // parity whether or not the transform it carries turns out to be usable. Kept out of
            // the condition chain for the same reason: claiming is a side effect, and a claim that
            // could short-circuit the seed would be a behaviour change wearing an optimisation's
            // clothes.
            if (seedLock is { } strong
                && strong.Hits >= ParityClaimChanceFactor * Math.Max(1.0, strong.ExpectedChanceHits))
            {
                onSeedClearOfChance?.Invoke();
            }

            if (seedLock is { } locked
                && Matrix3x2.Invert(locked.Transform, out var seedInv)
                && InverseTanProject(Vector2.Transform(new Vector2((float)cx, (float)cy), seedInv),
                    currentOrigin, pixelScaleRad, cx, cy, xSign) is { } seededWcs)
            {
                currentOrigin = seededWcs;
                lastMinv = seedInv;
                hasMinv = true;
                lastFitted = locked.Transform;
                seeded = true;
            }
        }

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

            // A pair-lock seed has ALREADY verified this WCS to within PairRansacLock's 4 px verify
            // radius over the whole field, so the coarse rungs below do not apply to it. They exist
            // for the blind case the schedule was written for ("mount pointing may be 45deg off"),
            // and re-opening the tolerance to 10% of the diagonal after a lock does not add slack, it
            // adds AMBIGUITY: on a 4164x2795 field the rung is 229 px while mean star spacing is only
            // 76 px, so several catalog stars sit inside tolerance for every detection, the affine fit
            // is pulled onto the wrong ones, and the WCS walks away from the seed. Measured on a
            // 5.4 deg 4.72"/px frame: the seed locked at consensus 101/160 (chance 1.1) and the loop
            // still ended at 27.75 px rms on 620 pairs, whereupon iteration 3's tighter rung found
            // almost nothing, tripped the divergence floor, and rolled back to that loose set -- the
            // exact rollback the comment on divergeFloor below describes. The gate then rejected a
            // field ASTAP solves every time.
            var toleranceIteration = seeded ? iteration + SeededToleranceRungOffset : iteration;
            var diagFraction = toleranceIteration switch
            {
                0 => 0.10,
                1 => 0.03,
                2 => 0.01,
                3 => 0.003,
                _ => 0.001,
            };
            var spacingFraction = toleranceIteration == 0 ? 3.0 : toleranceIteration == 1 ? 2.0 : toleranceIteration == 2 ? 1.0 : 0.5;
            var matchTolerance = (float)Math.Min(diagonal * diagFraction, avgSpacing * spacingFraction);

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

            // Rotation / scale aware prediction: matching measures distance to where the last
            // affine says each projection SHOULD land, while the fit below keeps consuming the
            // RAW projected coordinates -- prediction only improves pair selection, never the
            // fit target. Anchoring at the frame centre is exact to first order because the
            // origin update each iteration removes the affine's translation component.
            float[]? predX = null, predY = null;
            if (lastFitted is { } fit)
            {
                predX = new float[maxProjectedForMatching];
                predY = new float[maxProjectedForMatching];
                var fcx = (float)cx;
                var fcy = (float)cy;
                for (var pi = 0; pi < maxProjectedForMatching; pi++)
                {
                    var ux = projected[pi].Pixel.XCentroid - fcx;
                    var uy = projected[pi].Pixel.YCentroid - fcy;
                    predX[pi] = fcx + fit.M11 * ux + fit.M21 * uy;
                    predY[pi] = fcy + fit.M12 * ux + fit.M22 * uy;
                }
            }

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
                    var dx = det.XCentroid - (predX is null ? cat.Pixel.XCentroid : predX[catRank]);
                    var dy = det.YCentroid - (predY is null ? cat.Pixel.YCentroid : predY[catRank]);
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
            lastFitted = M.Value;

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
        // the linear WCS's SkyToPixel, which is exactly the "predicted"
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
                // Behind the tangent plane: should not happen for matches
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
        // (median absolute deviation, the robust analogue of σ); generous enough
        // to preserve real corner distortion (~1-3 px) while culling the ~10-15 px
        // mismatches that drag the LS fit toward garbage coefficients.
        // Robust bias correction: median, not mean; the late-iteration
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
            // CRPIX moves below, and (uDet, vDet) are offsets FROM CRPIX, so they must
            // move with it: uDet' = det - (CRPIX - bias) = uDet + bias. Leaving them
            // stale made the post-refit recomputation below mix reference pixels --
            // uTrueNew came off the new CRPIX while uDet still came off the old one, so
            // duFwd picked the bias straight back up. On Vela panel 19 that read as the
            // affine refit "raising" rms from 0.26 to 0.72 px, which is exactly
            // sqrt(0.26^2 + 0.47^2 + 0.47^2) for its bias of (0.47, 0.47) -- and since
            // those recomputed values are what SipPolynomial.Fit is then handed, SIP was
            // being trained to reproduce a constant offset the convention forbids it from
            // having (i + j = 0 lives in CRPIX). It was rejected on 100% of 82 real
            // frames; fail-safe, but for a manufactured reason.
            uDetRaw[i] += biasU;
            vDetRaw[i] += biasV;
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
            // MedianAndMad transforms `work` into deviations in place, so the second
            // collection pass this used to run -- re-filtering on keep[] and recomputing every
            // residual magnitude with a fresh Math.Sqrt -- is not needed. `work` is scratch and
            // nothing reads it after this.
            (medianResidual, mad) = StatisticsHelper.MedianAndMad(work.AsSpan(0, w));
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
        // forward and inverse are consistent inverses by construction; 
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
        // *substantially* more than the overfit-noise floor, for N inliers
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

            // Skip stars outside image bounds
            if (px.X < -0.5 || px.X > imgW + 0.5 || px.Y < -0.5 || px.Y > imgH + 0.5)
            {
                continue;
            }

            // No origin shift -- see the note in CountTightMatches: a WCS from
            // AttachCDMatrix maps sky straight into detected-centroid coordinates, so the
            // 1-based-to-0-based conversion this used to apply was a pure 1.27 px bias.
            // It mattered less here (the same bias lands on every candidate orientation
            // being compared) but it still blunted the comparison it exists to make.
            var wcsX = (float)px.X;
            var wcsY = (float)px.Y;

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

    /// <summary>
    /// Queries every catalog star within <paramref name="radiusDeg"/> of <paramref name="origin"/>,
    /// proper-motion propagated to the image epoch, <b>brightest first</b>. <c>internal</c> rather
    /// than private so the star-list export (see <c>VelaMosaicStarListExport</c> in the test
    /// project) freezes the SAME catalog the solver sees -- a re-derived query in the test would
    /// silently diverge on proper motion or the polar-cap path and turn a real regression into a
    /// data artefact.
    /// </summary>
    /// <remarks>
    /// <para><b>The sort is load-bearing, and its absence is what kept LDN 1089 from solving.</b>
    /// Both loops below accumulate in grid-scan order, RA cell major, so without a sort the list
    /// comes back in a spatial order that has nothing to do with brightness -- on that frame the
    /// first eight entries were V 11.66, 8.90, 11.48, 10.35, ... where the brightest eight in the
    /// region are V 3.40 to 6.92.</para>
    /// <para>Every downstream consumer reads this list as a brightness ranking and TRUNCATES it.
    /// <see cref="PairRansacLock"/> keeps the first 160 as its anchor pool and probes the first 8
    /// of those as its Stage 1 gate, so in scan order that gate asks whether an arbitrary strip of
    /// one RA cell was detected. On LDN 1089 the first 20 anchors matched NOTHING under the true
    /// transform while the first 160 matched 112 -- so the correct hypothesis was generated and
    /// then discarded at Stage 1, every time, and the diagnostics could only report that nothing
    /// correlated. Sorting is the whole fix: the pool becomes the 160 brightest, which is what a
    /// star detector finds and what the gates were designed around.</para>
    /// </remarks>
    internal List<(double RA, double Dec, double VMag)> QueryCatalogStarsInRegion(WCS origin, double radiusDeg, double dtJulianYears)
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

            SortBrightestFirst(result);
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

        SortBrightestFirst(result);
        return result;
    }

    /// <summary>
    /// Brightest first, with a magnitude-less star (<c>V_Mag</c> NaN, mapped to 99) sorting last
    /// rather than first. Both query paths end here, so neither can be the one that forgets.
    /// </summary>
    private static void SortBrightestFirst(List<(double RA, double Dec, double VMag)> stars)
        => stars.Sort(static (a, b) => a.VMag.CompareTo(b.VMag));

    /// <summary>
    /// Searches for the geometric pair-lock seed for one parity, over every anchor-pool policy in
    /// <see cref="PoolPolicies"/>. <c>internal</c> so the frozen-star-list regressions
    /// (<c>VelaMosaicFieldTests</c>) drive this exact code rather than a copy of the pool policy
    /// that could silently drift from it.
    /// </summary>
    /// <remarks>
    /// Three pools, tried in order, because the right one depends on the camera ANGLE and on how
    /// good the hint is, and neither is known when the pool is built.
    /// <para>
    /// DISC (rotation-invariant) is tried first and is the only one that does not assume the
    /// camera is north-up; <see cref="ProjectCatalogStars"/> carries the geometry and the LDN 1089
    /// measurements. It is first rather than last because when it differs from the rectangle it is
    /// RIGHT, and it is cheap when it is unnecessary: a field it can lock, it locks early (LDN
    /// 1089 seeds in 7,003 hypotheses where the rectangle exhausted a 1,000,000 budget).
    /// </para>
    /// <para>
    /// STRICT (no margin) is the default: an anchor outside the frame cannot possibly be detected,
    /// so it can only dilute the consensus -- and worse, since the pool is the brightest N of
    /// whatever is kept, off-frame stars DISPLACE genuine in-frame stars out of it entirely. With
    /// the matching loop's 0.1 margin the kept area is 1.44x the frame, so ~31% of anchors are
    /// undetectable, which starves the staged gates (Stage 1 wants 3 hits among the 8 brightest).
    /// Measured across the 24 Vela panels: strict locks 23 of 24 at 148-160 of 160 hits, while the
    /// 0.1 margin drops that to 76-125 and loses panels 12, 14, 17 and 20 outright at 11-13 hits,
    /// i.e. chance level -- the TRUE hypothesis was discarded at Stage 1 rather than losing on
    /// merit, which is why raising the hypothesis cap to exhaustive coverage did not help them.
    /// </para>
    /// <para>
    /// MARGINED is the last fallback and earns its keep on a BAD hint: panel 20.2's header is 38
    /// arcmin off, which pushes real stars off the projected frame. No pool alone covers the night.
    /// </para>
    /// <para>
    /// All three read the pool as a brightness ranking and truncate it to
    /// <c>PairRansacLock</c>'s 160 anchors, which is why <see cref="QueryCatalogStarsInRegion"/>
    /// sorting is a precondition of every one of them rather than a detail of any.
    /// </para>
    /// </remarks>
    internal static PairRansacLock.LockResult? TrySeedPairLock(
        List<(double RA, double Dec, double VMag)> catalogCoords,
        ReadOnlySpan<Vector2> rankedDetectedPoints,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign,
        float scaleTolerance,
        ILogger? logger = null)
        => TrySeedPairLock(catalogCoords, rankedDetectedPoints, origin, pixelScaleRad, cx, cy, dim,
            xSign, scaleTolerance, out _, logger);

    /// <summary>
    /// As <see cref="TrySeedPairLock(List{ValueTuple{double, double, double}}, ReadOnlySpan{Vector2}, WCS, double, double, double, ImageDim, double, float, ILogger?)"/>,
    /// additionally reporting what the attempt COST.
    /// </summary>
    /// <param name="cost">
    /// Hypotheses summed over every pool policy tried, and how many were tried. A parity that never
    /// locks still runs all of them and today discards each pool's diagnostics, so without this the
    /// expensive half of the parity race is the half nothing can measure.
    /// </param>
    /// <remarks>
    /// This exists so the phase-A baseline can be taken against the REAL pool policy rather than a
    /// copy of it in a test -- the same reason <see cref="TrySeedPairLock(List{ValueTuple{double, double, double}}, ReadOnlySpan{Vector2}, WCS, double, double, double, ImageDim, double, float, ILogger?)"/>
    /// is internal at all. Hypotheses are deterministic for a given input, so they compare across
    /// machines and across builds in a way wall clock does not.
    /// </remarks>
    internal static PairRansacLock.LockResult? TrySeedPairLock(
        List<(double RA, double Dec, double VMag)> catalogCoords,
        ReadOnlySpan<Vector2> rankedDetectedPoints,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign,
        float scaleTolerance,
        out SeedCost cost,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var hypotheses = 0;
        var poolsTried = 0;
        var cancelled = false;
        foreach (var (marginFraction, rotationInvariant) in PoolPolicies)
        {
            var seedProjected = ProjectCatalogStars(catalogCoords, origin, pixelScaleRad, cx, cy, dim, xSign, marginFraction, rotationInvariant);
            if (seedProjected.Count < MinStarsForMatch)
            {
                continue;
            }

            // QueryCatalogStarsInRegion sorts brightest-first, so the projected list is too --
            // and both the 160-anchor truncation below and PairRansacLock's Stage 1 depend on it.
            var catPts = new Vector2[seedProjected.Count];
            for (var i = 0; i < seedProjected.Count; i++)
            {
                catPts[i] = new Vector2(seedProjected[i].Pixel.XCentroid, seedProjected[i].Pixel.YCentroid);
            }

            var lockResult = PairRansacLock.TryLock(catPts, rankedDetectedPoints, rankedDetectedPoints,
                dim.Width, dim.Height, scaleTolerance, out var lockDiagnostics,
                cancellationToken: cancellationToken);
            poolsTried++;
            hypotheses += lockDiagnostics.Hypotheses;
            if (lockResult is { } locked)
            {
                cost = new SeedCost(hypotheses, poolsTried);
                logger?.LogDebug("CatalogPlateSolver: pair-lock seed (xSign={XSign}, anchor pool {Pool}) consensus {Hits}/{Census} (chance {Chance:F1}) after {Hypotheses} hypotheses",
                    xSign, PoolName(marginFraction, rotationInvariant), locked.Hits, locked.Census, locked.ExpectedChanceHits, locked.Hypotheses);
                return locked;
            }

            // No seed is a legitimate outcome on a sparse field, but on a dense one it is the
            // difference between "nothing correlates" and "the scan never got there" -- and only
            // the diagnostics distinguish them.
            logger?.LogDebug("CatalogPlateSolver: no pair-lock seed (xSign={XSign}, anchor pool {Pool}): {Diagnostics}",
                xSign, PoolName(marginFraction, rotationInvariant), lockDiagnostics.ToString());

            // The pools are a fallback chain, so a cancelled scan must not roll on to the next
            // one: three pools each abandoned mid-scan is three times the work saved nothing.
            if (lockDiagnostics.Cancelled)
            {
                cancelled = true;
                break;
            }
        }

        cost = new SeedCost(hypotheses, poolsTried, cancelled);
        return null;
    }

    /// <summary>What one parity's seed attempt spent. See the <c>out cost</c> overload above.</summary>
    /// <param name="Hypotheses">Summed over every pool policy tried.</param>
    /// <param name="PoolsTried">How many of <c>PoolPolicies</c> were reached before locking or giving up.</param>
    internal readonly record struct SeedCost(int Hypotheses, int PoolsTried, bool Cancelled = false);

    /// <summary>
    /// The anchor-pool policies the seed tries, in order. See <see cref="TrySeedPairLock"/> for
    /// why more than one is needed and <see cref="ProjectCatalogStars"/> for what each keeps.
    /// </summary>
    private static readonly (float MarginFraction, bool RotationInvariant)[] PoolPolicies =
        [(0f, true), (0f, false), (0.1f, false)];

    private static string PoolName(float marginFraction, bool rotationInvariant) =>
        rotationInvariant ? "rotation-invariant disc" : $"rectangle, margin {marginFraction:P0}";

    /// <param name="marginFraction">
    /// How far outside the frame a projected star may fall and still be kept, as a fraction of
    /// frame size. The matching loop wants the default 0.1: its early iterations work from a WCS
    /// that is still wrong by up to the hint error, so a star just off the edge may well move in
    /// as the fit converges. The pair-lock SEED wants 0 -- see <see cref="TrySeedPairLock"/>.
    /// </param>
    /// <param name="rotationInvariant">
    /// Keep only stars inside the disc that stays in frame under ANY camera angle, ignoring
    /// <paramref name="marginFraction"/>.
    /// <para><b>The projection here is north-up and the camera is not.</b> This routine has no
    /// rotation to apply -- finding it is the seed's whole job -- so an in-frame test against the
    /// frame RECTANGLE silently assumes a camera angle of zero. On a 3:2 frame at LDN 1089's
    /// 88 degree angle, that rectangle's contents rotate mostly off the sensor: of the 160
    /// brightest anchors it kept, only 92 were really on the frame, and the ones it dropped were
    /// real stars sitting in the sensor's own corners. The rectangle is not a conservative
    /// approximation of the frame, it is a DIFFERENT REGION OF SKY that merely has the same area.
    /// </para>
    /// <para>The largest region that is in frame at every angle is the inscribed disc, and
    /// selecting on it costs the corners (48% of a 3:2 frame) to gain an anchor set every member
    /// of which is genuinely observable: measured on LDN 1089, 125 of 130 disc anchors have a
    /// detection within 4 px, against 87 of 160 for the rectangle. That ratio is what the staged
    /// gates read -- Stage 1 asks whether 3 of the 8 brightest anchors were detected -- so it
    /// decides whether the true hypothesis survives to be counted at all.</para>
    /// </param>
    internal static List<ProjectedCatalogStar> ProjectCatalogStars(
        List<(double RA, double Dec, double VMag)> catalogCoords,
        WCS origin,
        double pixelScaleRad,
        double cx,
        double cy,
        ImageDim dim,
        double xSign,
        float marginFraction = 0.1f,
        bool rotationInvariant = false
    )
    {
        var projected = new List<ProjectedCatalogStar>();

        var alpha0 = origin.CenterRA * HOURS2RADIANS;
        var (sinDelta0, cosDelta0) = Math.SinCos(double.DegreesToRadians(origin.CenterDec));

        var marginX = dim.Width * marginFraction;
        var marginY = dim.Height * marginFraction;

        // Radius of the disc that is in frame at any camera angle. The seed's projection shares
        // its tangent point with the frame centre, so the unknown rotation is about (cx, cy) and
        // this disc is invariant under it.
        var safeRadius = Math.Min(dim.Width, dim.Height) / 2.0;
        var safeRadiusSq = safeRadius * safeRadius;

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

            bool keep;
            if (rotationInvariant)
            {
                var dx = xPix - cx;
                var dy = yPix - cy;
                keep = dx * dx + dy * dy <= safeRadiusSq;
            }
            else
            {
                keep = xPix >= -marginX && xPix <= dim.Width + marginX &&
                       yPix >= -marginY && yPix <= dim.Height + marginY;
            }

            if (keep)
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
