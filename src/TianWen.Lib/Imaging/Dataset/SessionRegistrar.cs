using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// Per-session registration + master integration for the dataset builder
/// (docs/plans/ai-denoise-deconv.md §2.4, task P0/#39). Ties the measure+gate
/// (<see cref="SessionFrameAnalyzer"/>) to the stacker's registration + integration
/// seams and emits, for one <see cref="ImagingSession"/>:
/// <list type="bullet">
///   <item>the <b>registered subs</b>, each surviving light calibrated, debayered, and
///     warped onto a common union canvas, persisted as scratch FITS so the tiler (#40)
///     can read cell footprints back without re-warping. Cell (i, j) of any two subs is
///     an N2N training pair by construction (§2.4).</item>
///   <item>the <b>session master</b>: the robust integration of those subs, the N2N eval
///     truth and the deconv synthetic-degradation source (§2.1/§2.2).</item>
/// </list>
///
/// <para><b>One path with the stacker.</b> Reference pick, quad-tolerance ladder, rigid
/// refinement, union-canvas geometry, rejector selection, and the streaming integrator are
/// the same <c>StackingPipeline</c> code (<see cref="CanvasGeometry"/>,
/// <see cref="RegistrationRefiner"/>, <see cref="StackingPipeline.BuildRejector"/>,
/// <see cref="Float16StagedStrategy"/>), so a dataset master registers byte-for-byte like a
/// <c>tianwen stack</c> master. The only copied code is the two-line
/// <see cref="TryMatchAsync"/> tolerance ladder (verbatim from <c>StackingPipeline</c>).</para>
///
/// <para><b>Zero re-detection.</b> The gate already ran <see cref="Image.FindStarsAsync"/>
/// on every sub and <see cref="SessionFrameAnalyzer.AnalyzedFrame"/> retains the star list,
/// so both the reference pick and the per-sub quad match run off those retained lists; 
/// no image is reloaded to detect stars. Pixels are reloaded exactly once more (the warp
/// pass) because holding every debayered sub in RAM would blow the budget on a large
/// session; the integrator then re-reads the warped scratch FITS (cheap, no debayer).</para>
/// </summary>
public static class SessionRegistrar
{
    /// <summary>
    /// Fewest registered subs a <b>drizzled</b> session needs before it is split into a half-master
    /// pair. Each half has to be a usable integration in its own right, and drizzle's binding
    /// constraint is per-Bayer-position R/B coverage
    /// (<see cref="DrizzleStrategy.AutoSelectMinFrameCount"/>), which applies to each half separately
    /// rather than to the session; so the session needs twice it. Below this a half has coverage
    /// HOLES in red and blue, which no amount of noise-level diversity would be worth.
    /// </summary>
    public const int MinSubsForHalfMastersDrizzled = 2 * DrizzleStrategy.AutoSelectMinFrameCount;

    /// <summary>
    /// Fewest frames a <b>rejection-integrated</b> (AHD + sigma-clip) half needs. Two independent
    /// reasons put it here, and neither is the coverage argument above, which does not apply when
    /// nothing is being deposited per Bayer position:
    /// <list type="number">
    /// <item><b>It must get a real rejector.</b> <c>StackingPipeline.BuildRejector</c> returns
    /// <see langword="null"/> below 5 frames (no rejection at all, which is the very defect that
    /// makes an uncalibrated drizzle unacceptable), then <c>LinearFitClipRejector</c> up to 30. At 20
    /// a half sits comfortably inside the band the stacker itself selects for that depth, with
    /// margin above the dead zone.</item>
    /// <item><b>Its noise must be master-like, not sub-like.</b> 20 frames puts a half at ~0.22x a
    /// single sub, i.e. a genuine master; a 5-frame half would land at ~0.45x, halfway to a sub, and
    /// the sub tiles already cover that regime far more cheaply.</item>
    /// </list>
    /// </summary>
    public const int MinFramesPerRejectedHalfMaster = 20;

    /// <summary><inheritdoc cref="MinFramesPerRejectedHalfMaster" path="/summary/node()"/></summary>
    public const int MinSubsForHalfMastersRejected = 2 * MinFramesPerRejectedHalfMaster;

    /// <summary>
    /// The half-master floor for a session, which depends on HOW its master is integrated: one number
    /// is wrong for both cases. Measured over the 50 sessions of the current dataset (registered sub
    /// counts, median 126, range 15 to 314): the drizzle floor admits 26 of them and the rejected
    /// floor 48.
    ///
    /// <para>Note what the floor does NOT change: the pair's noise RATIO. Each half integrates half
    /// of its own session, so halfA against halfB is sqrt(2) at every floor. Lowering it does not
    /// produce shallower pairs, it admits sessions whose whole master is shallower, which is extra
    /// noise-level diversity and is exactly what the conditioning input exists to span.</para>
    /// </summary>
    public static int MinSubsForHalfMasters(bool drizzled) =>
        drizzled ? MinSubsForHalfMastersDrizzled : MinSubsForHalfMastersRejected;

    /// <summary>
    /// Whether this session's master should be Bayer-drizzled. Two independent conditions, and
    /// the second is not part of the stacker's own gate:
    /// <list type="number">
    /// <item><see cref="DrizzleStrategy.Evaluate"/> must report <c>CanRun</c>: RGGB sensor,
    /// enough matched frames for the per-Bayer-position coverage to fill R/B, and the flux +
    /// weight planes inside the RAM budget. Its <c>Rationale</c> is logged when it refuses, so a
    /// session that silently fell back can be explained after the fact.</item>
    /// <item><b>A matched dark master must exist.</b> Drizzle has no per-cell rejection
    /// (<see cref="IntegrationJob.BadPixelMask"/> exists for exactly this reason), whereas the
    /// AHD path's sigma-clip washes hot pixels out across the whole session. Dark subtraction
    /// removes a hot pixel's offset, so a calibrated session is fine; an UNCALIBRATED one relies
    /// entirely on that rejection, and drizzling it would write uncorrected hot pixels into the
    /// master. Falling back is strictly better than building a mask, because the mask would only
    /// be reconstructing information the dark already carries.</item>
    /// </list>
    /// </summary>
    internal static bool TryDrizzle(IntegrationProbe probe, Calibrator? calibrator, ILogger? logger, string sessionId)
    {
        if (calibrator?.Dark is null)
        {
            logger?.LogInformation(
                "  [{Session}] not drizzling: no matched dark master, so sigma-clip rejection is " +
                "the only thing removing hot pixels and drizzle has none", sessionId);
            return false;
        }

        var fit = new DrizzleStrategy().Evaluate(probe, new ResourceBudget());
        if (!fit.CanRun)
        {
            logger?.LogInformation("  [{Session}] not drizzling: {Rationale}", sessionId, fit.Rationale);
            return false;
        }
        return true;
    }

    /// <summary>Absolute floor of matched stars for a quad fit. Below this the affine
    /// solve is unstable; the sub is dropped rather than misregistered. Mirrors
    /// <c>StackingPipeline.MinStarsForMatch</c>.</summary>
    private const int MinStarsForMatch = 24;

    /// <summary>Cap on the brightest stars used to build quad fingerprints. Bright stars
    /// reproduce across detection-threshold jitter between frames, so the top-K signature
    /// stays stable.
    /// <para>100, NOT the stacker's 500, and the difference is load-bearing rather than a
    /// preference. A quad matches only when the same four stars form it in both frames, so with a
    /// fraction p of the top-K detections real, at most about p^4 of quads can match. p falls with
    /// depth: measured on Helix 2025-08-09, mono detections were 68% real at top-50, 59% at
    /// top-100, 41% at top-200 and 32% over all 601. At 500 that tail dominates the fingerprint set
    /// (p^4 near 1%, roughly 4 usable quads of 375, under the minimumCount of 6) and the session
    /// registered 0 of 316 subs; at 100 it registered 314 of 314, with the match tolerances
    /// tightening from mostly-0.5 to mostly-0.1/0.2. The stacker still uses 500 and has the same
    /// exposure on a thin field, but changing it needs its own end-to-end validation.</para></summary>
    private const int QuadStars = 100;

    /// <summary>Quad-match tolerance ladder: try tight first, loosen on failure. Verbatim
    /// from <c>StackingPipeline.QuadTolerances</c>.</summary>
    private static readonly float[] QuadTolerances = [0.008f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f];

    /// <summary>One surviving light registered onto the session's union canvas.</summary>
    /// <param name="Source">The original raw light (header-only handle). Carries the FITS
    /// metadata, gain, exposure, filter, temperature, that the tile manifest (#40) needs;
    /// the scratch FITS holds pixels only.</param>
    /// <param name="WarpedPath">Scratch FITS of the calibrated + debayered sub warped to the
    /// canvas grid (float32, linear, NaN outside the source footprint). Shares the exact
    /// pixel grid with every other sub and the master, so cell (i, j) is a fixed sky footprint
    /// across the whole session.</param>
    /// <param name="TransformToCanvas">Composed source→canvas affine (registration transform
    /// left-multiplied by the union-canvas shift).</param>
    /// <param name="Metrics">The sub's PSF metrics from the gate (retained, not recomputed); 
    /// median HFD/FWHM/ellipticity + star count. Feeds the per-tile manifest + stats report.</param>
    public sealed record RegisteredSub(
        FrameInfo Source,
        string WarpedPath,
        Matrix3x2 TransformToCanvas,
        FrameMetrics Metrics);

    /// <summary>The registered + integrated output for one session.</summary>
    /// <param name="Session">The source session.</param>
    /// <param name="Master">Integrated session master on the union canvas (RGB float, linear,
    /// median-normalised by the integrator). N2N eval truth + deconv degradation source.</param>
    /// <param name="Subs">The registered subs, in registration order. Every warped scratch FITS
    /// shares the canvas grid with <paramref name="Master"/>.</param>
    /// <param name="CanvasWidth">Union-canvas width (pixels). Shared by the master + every sub.</param>
    /// <param name="CanvasHeight">Union-canvas height (pixels).</param>
    /// <param name="StatsRect">The all-frames-overlap intersection rectangle; the region where
    /// every sub contributes, useful for the tiler's structure-biased cell sampling and for
    /// per-frame stretch statistics.</param>
    /// <param name="Reference">The sub chosen as the registration reference (identity transform).</param>
    /// <param name="GatedCount">Subs that survived the quality gate (registration candidates).</param>
    /// <param name="RegisteredCount">Subs that registered successfully (== <see cref="Subs"/> length).</param>
    /// <param name="SkippedCount">Gated subs that failed to register (too few stars / no quad fit).</param>
    /// <param name="MasterStrategy">Which integrator produced <paramref name="Master"/>.
    /// <b>Callers that persist a master MUST stamp this into the FITS header and the tile
    /// manifest.</b> Drizzle is gated per session (§ <see cref="TryDrizzle"/>), so a dataset
    /// legitimately contains both kinds; a mixed population with nothing recording which is
    /// which is the same silent confound as the channel-0 PSF sampling was, and it would make
    /// any per-channel PSF statistic meaningless (AHD reconstructs green from closer
    /// neighbours, so it measures sharper than red for reasons that are not optical).</param>
    /// <param name="HalfMasterA">Integration of one half of <paramref name="Subs"/>, or
    /// <c>null</c> when the session has too few subs to split. Together with
    /// <paramref name="HalfMasterB"/> this is an <b>independent</b> N2N pair at close to the
    /// noise level a real master has, which is the regime a denoiser is actually deployed in.
    /// Measured: a single sub is 5.42x the master's background noise and the deepest pair the
    /// 8-subs-per-cell tiles allow (4v4) is still 2.96x, so a model trained on tiles alone has
    /// to extrapolate. Two half-masters land at ~1.41x (sqrt 2), which closes it.</param>
    /// <param name="HalfMasterB">The complementary half. See <paramref name="HalfMasterA"/>.</param>
    public sealed record RegisteredSession(
        ImagingSession Session,
        Image Master,
        ImmutableArray<RegisteredSub> Subs,
        int CanvasWidth,
        int CanvasHeight,
        Rectangle StatsRect,
        FrameInfo Reference,
        int GatedCount,
        int RegisteredCount,
        int SkippedCount,
        IntegrationStrategyKind MasterStrategy = IntegrationStrategyKind.Float16Staged,
        Image? HalfMasterA = null,
        Image? HalfMasterB = null);

    /// <summary>
    /// Measures + gates a session's lights, registers the survivors to a common reference,
    /// warps them onto the union canvas (persisted to <paramref name="scratchDir"/>), and
    /// integrates the session master. Returns <c>null</c> when the session cannot yield a
    /// usable master: too few survivors after the gate, or fewer than two subs register.
    /// </summary>
    /// <param name="session">The session to register.</param>
    /// <param name="calibrator">Bias/dark/flat masters resolved by header match, or <c>null</c>
    /// to register uncalibrated (test path only, real N2N pairs MUST be calibrated so the two
    /// subs don't share a fixed-pattern dark-current signal, which would violate the
    /// noise-independence assumption).</param>
    /// <param name="scratchDir">Root for per-session warped-sub + integration scratch. The
    /// session's subdirectory is wiped + recreated; the caller deletes it after tiling.</param>
    /// <param name="qualityRejectSigma">Session-relative MAD gate threshold (0 disables the
    /// relative gate; zero-star frames are still dropped). See <see cref="SessionFrameAnalyzer.ApplyGate"/>.</param>
    /// <param name="qualityMaxRejectFraction">Keep-floor for the gate (purity over yield, 0.5
    /// for the dataset vs the stacker's 0.2).</param>
    /// <param name="minSubs">Minimum survivors required to build a session master.</param>
    /// <param name="minSubsForHalfMasters">Registered subs required before the session is also split
    /// into a half-master pair; below it <see cref="RegisteredSession.HalfMasterA"/> and
    /// <see cref="RegisteredSession.HalfMasterB"/> stay null. <see langword="null"/> (the default)
    /// picks per master strategy via <see cref="MinSubsForHalfMasters(bool)"/>, which is the correct
    /// behaviour: the drizzled floor answers a coverage constraint and the rejected floor answers a
    /// rejection one, so a single number is wrong for one of them. Pass a value only to override
    /// deliberately.</param>
    /// <param name="debayerAlgorithm">Debayer used for both measurement and warping.</param>
    /// <param name="logger">Optional progress log.</param>
    public static async Task<RegisteredSession?> RegisterAsync(
        ImagingSession session,
        Calibrator? calibrator,
        string scratchDir,
        float qualityRejectSigma = 3f,
        float qualityMaxRejectFraction = 0.5f,
        int minSubs = 10,
        int? minSubsForHalfMasters = null,
        DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Measure every light: calibrate -> debayer -> detect stars -> PSF metrics.
        //    The star list is retained on each AnalyzedFrame so nothing below re-detects.
        var analyzed = new List<SessionFrameAnalyzer.AnalyzedFrame>(session.Lights.Length);
        foreach (var light in session.Lights)
        {
            cancellationToken.ThrowIfCancellationRequested();
            analyzed.Add(await SessionFrameAnalyzer.MeasureAsync(
                light, calibrator, debayerAlgorithm, cancellationToken: cancellationToken));
        }

        // 2. Session-relative quality gate (star-count-led; see SessionFrameAnalyzer doc).
        var gate = SessionFrameAnalyzer.ApplyGate(analyzed, qualityRejectSigma, qualityMaxRejectFraction);
        logger?.LogInformation(
            "  [{Session}] gate: kept {Kept}/{Total} ({Rejected} rejected{Floor})",
            session.Id, gate.Kept.Length, analyzed.Count, gate.Rejected.Length,
            gate.KeepFloorTriggered ? ", floor" : "");
        if (gate.Kept.Length < minSubs)
        {
            logger?.LogWarning("  [{Session}] {Kept} subs survived the gate (< {Min}) -- skipped",
                session.Id, gate.Kept.Length, minSubs);
            return null;
        }

        var survivors = gate.Kept;

        // 3. Reference pick: composite PSF-quality score over the RETAINED metrics (no reload,
        //    no re-detection). Same formula as StackingPipeline -- most stars, penalised by broad
        //    PSF (HFD) and elongation (ellipticity). Rewards sharp-round-many simultaneously.
        var reference = survivors[0];
        var bestScore = float.NegativeInfinity;
        foreach (var f in survivors)
        {
            var m = f.Metrics;
            var score = m.StarCount / (MathF.Max(m.MedianHfd, 1f) * (1f + 4f * m.MedianEllipticity));
            if (score > bestScore)
            {
                bestScore = score;
                reference = f;
            }
        }
        var refW = reference.Frame.Width;
        var refH = reference.Frame.Height;
        logger?.LogInformation(
            "  [{Session}] reference {File} (stars={Stars} hfd={Hfd:F2} ecc={Ecc:F3} score={Score:F1})",
            session.Id, Path.GetFileName(reference.Frame.Path),
            reference.Metrics.StarCount, reference.Metrics.MedianHfd, reference.Metrics.MedianEllipticity, bestScore);

        // 4. Register each survivor against the reference from the RETAINED star lists.
        using var referenceSorted = new SortedStarList(reference.Stars);
        var referenceQuads = await referenceSorted.FindQuadsAsync(maxStars: QuadStars, cancellationToken: cancellationToken);
        logger?.LogInformation("  [{Session}] reference quads={Quads} from {Stars} retained stars (top {Cap})",
            session.Id, referenceQuads.Count, referenceSorted.Count, QuadStars);
        var matched = new List<(SessionFrameAnalyzer.AnalyzedFrame Frame, Matrix3x2 Transform)>(survivors.Length);
        var skippedTooFewStars = 0;
        var skippedNoQuadFit = 0;
        var minLightQuads = int.MaxValue;
        var maxLightQuads = 0;
        foreach (var f in survivors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(f, reference))
            {
                matched.Add((f, Matrix3x2.Identity));
                continue;
            }
            if (f.Stars.Count < MinStarsForMatch)
            {
                skippedTooFewStars++;
                logger?.LogDebug("  [{Session}] {File} stars={Stars} (< {Min}) -> skip (too few)",
                    session.Id, Path.GetFileName(f.Frame.Path), f.Stars.Count, MinStarsForMatch);
                continue;
            }
            using var lightSorted = new SortedStarList(f.Stars);
            var lightQuads = await lightSorted.FindQuadsAsync(maxStars: QuadStars, cancellationToken: cancellationToken);
            minLightQuads = Math.Min(minLightQuads, lightQuads.Count);
            maxLightQuads = Math.Max(maxLightQuads, lightQuads.Count);
            var (solution, quadTolerance, rmsResidualPx) = await TryMatchAsync(lightSorted, referenceSorted, QuadStars);
            if (solution is null)
            {
                skippedNoQuadFit++;
                // Both counts are already in hand, so state them: "no fit" on its own cannot
                // separate a DETECTION problem (few quads on this sub) from a GEOMETRY one
                // (plenty of quads on both sides that still do not correspond), and those two
                // have opposite fixes.
                logger?.LogDebug(
                    "  [{Session}] {File} stars={Stars} quads={Quads} vs reference quads={RefQuads} -> skip (no quad fit up to tolerance {MaxTol})",
                    session.Id, Path.GetFileName(f.Frame.Path), f.Stars.Count, lightQuads.Count,
                    referenceQuads.Count, QuadTolerances[^1]);
                continue;
            }
            logger?.LogDebug(
                "  [{Session}] {File} stars={Stars} quads={Quads} -> matched at tolerance {Tol} (rms {Rms:F2} px)",
                session.Id, Path.GetFileName(f.Frame.Path), f.Stars.Count, lightQuads.Count,
                quadTolerance, rmsResidualPx);
            // Rigid (rotation + isotropic scale + translation) refinement on top of the bulk
            // quad fit -- closes the sub-pixel residual the fingerprint match averages away.
            var refined = RegistrationRefiner.RefineRigid(lightSorted, referenceSorted, solution.Value).Refined;
            matched.Add((f, refined));
        }
        logger?.LogInformation(
            "  [{Session}] registered {Matched}/{Survivors} (skipped {Skipped}: {TooFew} too-few-stars, {NoFit} no-quad-fit)",
            session.Id, matched.Count, survivors.Length, skippedTooFewStars + skippedNoQuadFit,
            skippedTooFewStars, skippedNoQuadFit);
        if (matched.Count < 2)
        {
            // WARNING level, which is what a bake log actually shows, so it has to be
            // self-diagnosing. The bare form of this message cost a whole-session drop that could
            // not be explained without re-running the session at Debug: it named neither the star
            // counts nor the quad counts, both of which were already computed right here.
            //
            // Read the two together, because they separate the only two causes and those have
            // opposite fixes. FEW quads everywhere is a detection problem. PLENTY of quads on both
            // sides that still do not correspond is a PURITY problem in the quad-forming set, and
            // that is what the Helix 2025-08-09 drop turned out to be: a quad matches only when
            // the same four stars form it in both frames, so with a fraction p of the top-K
            // detections real, only about p^4 of quads can match at all. Measured on that session,
            // detecting on the VNG-interpolated RED plane gave p = 0.08 (1024 detections, 384
            // reference quads, 0 of 316 subs matched); the quarter-density R and B planes
            // manufacture ~1000 spurious detections per frame that interpolation smooths into
            // plausible round blobs. Hence detection now runs on the pre-debayer image, which
            // routes through BilinearMono, and QuadStars is 100 rather than 500 to keep the
            // fingerprint set at the bright end where p is high: p = 0.59 at top-100 versus 0.32
            // over all 601 mono detections, which took the same session to 314/314.
            logger?.LogWarning(
                "  [{Session}] fewer than 2 registered subs -- skipped. survivors={Survivors}, reference {RefFile} stars={RefStars} quads={RefQuads}, other subs' quads {MinQuads}..{MaxQuads}, skipped {TooFew} too-few-stars + {NoFit} no-quad-fit",
                session.Id, survivors.Length, Path.GetFileName(reference.Frame.Path),
                referenceSorted.Count, referenceQuads.Count,
                minLightQuads == int.MaxValue ? 0 : minLightQuads, maxLightQuads,
                skippedTooFewStars, skippedNoQuadFit);
            return null;
        }

        // 5. Union canvas: the bounding box covering every warped source footprint, plus the
        //    per-frame footprints and the all-frames intersection rect (stretch/sampling stats).
        var transforms = new List<Matrix3x2>(matched.Count);
        foreach (var (_, t) in matched)
        {
            transforms.Add(t);
        }
        var (canvasShift, _, _, canvasW, canvasH) = CanvasGeometry.ComputeUnionCanvas(transforms, refW, refH);
        var (footprints, statsRect) =
            CanvasGeometry.ComputeFootprintsAndStatsRect(transforms, canvasShift, refW, refH, canvasW, canvasH);
        logger?.LogInformation("  [{Session}] canvas {W}x{H}, stats-rect {Rect}",
            session.Id, canvasW, canvasH, statsRect);

        // 6. Warp pass: reload -> calibrate -> debayer -> warp onto the canvas -> scratch FITS.
        //    One sub in RAM at a time; the scratch FITS is the shared artifact the master
        //    integration and the tiler (#40) both read.
        var sessionScratch = Path.Combine(scratchDir, Sanitize(session.Id));
        if (Directory.Exists(sessionScratch))
        {
            Directory.Delete(sessionScratch, recursive: true);
        }
        Directory.CreateDirectory(sessionScratch);

        var subs = ImmutableArray.CreateBuilder<RegisteredSub>(matched.Count);
        // Captured off the first raw load: the drizzle gate keys off it and there is no cheaper
        // authoritative source here. Invariant within a session by construction (frames with a
        // different sensor land in a different group).
        var sensorType = SensorType.Unknown;
        for (var i = 0; i < matched.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (f, transform) = matched[i];
            var raw = await f.Frame.LoadFullAsync(cancellationToken);
            if (i == 0)
            {
                sensorType = raw.ImageMeta.SensorType;
            }
            var calibrated = calibrator?.Apply(raw) ?? raw;
            var debayered = await calibrated.DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
            var shifted = transform * canvasShift;
            var warped = await debayered.WarpToReferenceGridAsync(shifted, canvasW, canvasH, cancellationToken);
            var warpedPath = Path.Combine(sessionScratch, $"warped_{i:D4}.fits");
            warped.WriteToFitsFile(warpedPath);
            subs.Add(new RegisteredSub(f.Frame, warpedPath, shifted, f.Metrics));
        }
        var subsList = subs.MoveToImmutable();

        // 7. Integrate the session master from the scratch warped subs. Reuses the stacker's
        //    rejector selection + streaming float16-staged integrator (bounded RAM regardless
        //    of sub count). The producer re-reads each warped FITS one at a time.
        //
        //    ApplyNormalization is OFF (unlike a display stack): the dataset master must be a
        //    plain linear combine on the SAME scale as the warped subs. The tiler (#40) runs both
        //    the master and the subs through the identical inference pre-stretch (auto-detect ->
        //    MtfStretch to median 0.25), which is only scale-consistent if the master is a genuine
        //    linear frame like the subs -- a median-normalised master (→0.5) could trip the
        //    auto-detect differently and land the master and its own subs at different medians,
        //    breaking N2N pair comparability + sub-vs-master eval. A quality-gated session already
        //    has consistent sky levels, so per-frame normalisation buys the rejector little here.
        //    The STRATEGY is gated per session rather than fixed (see TryDrizzle): Bayer drizzle
        //    when it can run, because it deposits every raw CFA sample into its own channel and
        //    never lets a neighbour invent a colour value, which is exactly what a dataset meant
        //    to serve as TRUTH needs. AHD + sigma-clip otherwise. Both kinds legitimately coexist,
        //    hence RegisteredSession.MasterStrategy.
        var probe = IntegrationProbe.Snapshot(
            frameCount: subsList.Length,
            frameWidth: refW,
            frameHeight: refH,
            channelCount: 3,
            canvasWidth: canvasW,
            canvasHeight: canvasH,
            stagingDir: sessionScratch,
            sensorType: sensorType);
        var useDrizzle = TryDrizzle(probe, calibrator, logger, session.Id);

        var all = Enumerable.Range(0, subsList.Length).ToImmutableArray();
        var master = await IntegrateSubsetAsync(all, "_integrate");
        logger?.LogInformation(
            "  [{Session}] master integrated via {Strategy} ({Frames} frames)",
            session.Id, useDrizzle ? nameof(IntegrationStrategyKind.BayerDrizzle) : nameof(IntegrationStrategyKind.Float16Staged),
            subsList.Length);

        // 7b. Half-master pair: two integrations over DISJOINT halves, so they share the scene and
        //     nothing else. The split is INTERLEAVED, not the first-half/second-half it is natural
        //     to reach for: seeing, transparency and focus drift monotonically through a session, so
        //     contiguous halves differ systematically in PSF and sky level, and an N2N pair whose two
        //     sides disagree about the signal teaches the model to average that disagreement away.
        //     Interleaving spreads the drift evenly across both sides.
        //
        //     Both halves take the SAME strategy as the master (useDrizzle is shared), which is worth
        //     protecting even though drizzling them costs a second and third pass over the session's
        //     raw lights on the archive disk: the pair is what the model trains on and the master is
        //     what it is deployed on, so a drizzled master with AHD halves would train it on a PSF and
        //     colour character it never meets at inference. Cheapening the halves to AHD would buy
        //     ~N raw loads per session and reintroduce exactly the demosaic artifact this gate exists
        //     to remove.
        Image? halfA = null;
        Image? halfB = null;
        // Per strategy unless the caller insists: the drizzled floor answers R/B coverage and the
        // rejected one answers rejection strength, so one number would be wrong for one of them.
        var halfMasterFloor = minSubsForHalfMasters ?? MinSubsForHalfMasters(useDrizzle);
        if (subsList.Length >= halfMasterFloor)
        {
            halfA = await IntegrateSubsetAsync(
                all.Where(i => i % 2 == 0).ToImmutableArray(), "_half_a");
            halfB = await IntegrateSubsetAsync(
                all.Where(i => i % 2 == 1).ToImmutableArray(), "_half_b");
            logger?.LogInformation(
                "  [{Session}] half-master pair integrated ({A} + {B} frames)",
                session.Id, (subsList.Length + 1) / 2, subsList.Length / 2);
        }
        else
        {
            // Names the floor that applied AND why, because the two floors differ by 3x and a reader
            // of the log cannot otherwise tell a coverage refusal from a rejection one.
            logger?.LogInformation(
                "  [{Session}] no half-master pair: {Frames} subs is under the {Min} a {Kind} half needs ({Reason})",
                session.Id, subsList.Length, halfMasterFloor,
                useDrizzle ? nameof(IntegrationStrategyKind.BayerDrizzle) : nameof(IntegrationStrategyKind.Float16Staged),
                useDrizzle ? "per-Bayer-position R/B coverage" : "enough frames for a real rejector");
        }

        return new RegisteredSession(
            session, master, subsList, canvasW, canvasH, statsRect,
            reference.Frame, survivors.Length, matched.Count, skippedTooFewStars + skippedNoQuadFit,
            useDrizzle ? IntegrationStrategyKind.BayerDrizzle : IntegrationStrategyKind.Float16Staged,
            halfA, halfB);

        async Task<Image> IntegrateSubsetAsync(ImmutableArray<int> pick, string scratchName)
        {
            var scratch = Path.Combine(sessionScratch, scratchName);
            var job = new IntegrationJob(
                WarpedFrames: token => WarpedProducer(pick, token),
                ExpectedFrameCount: pick.Length,
                Options: new IntegrationOptions(Rejector: StackingPipeline.BuildRejector(pick.Length), ApplyNormalization: false),
                StagingDir: scratch,
                StatsRect: statsRect,
                // Footprints are indexed in registration order, the same order subsList is built
                // in, so the subset has to be taken in lockstep or every frame's coverage is
                // attributed to the wrong frame.
                FrameFootprints: [.. pick.Select(i => footprints[i])],
                CanvasWidth: canvasW,
                CanvasHeight: canvasH,
                RawBayerFrames: useDrizzle ? token => RawBayerProducer(pick, token) : null,
                DrizzleOptions: useDrizzle ? new DrizzleOptions() : null);
            var run = useDrizzle
                ? await new DrizzleStrategy().RunAsync(job, cancellationToken)
                : await new Float16StagedStrategy().RunAsync(job, cancellationToken);
            return run.Master;
        }

        async IAsyncEnumerable<Image> WarpedProducer(
            ImmutableArray<int> pick, [EnumeratorCancellation] CancellationToken token)
        {
            foreach (var i in pick)
            {
                token.ThrowIfCancellationRequested();
                var sub = subsList[i];
                if (!Image.TryReadFitsFile(sub.WarpedPath, out var img))
                {
                    throw new IOException($"Failed to re-read warped scratch FITS: {sub.WarpedPath}");
                }
                yield return img;
            }
        }

        // Drizzle forward-projects the RAW CFA itself, so it cannot consume the warped scratch
        // FITS (those are debayered, which is the whole thing being avoided). That costs one extra
        // load+calibrate pass over the session's lights; the registration work is not repeated
        // because RegisteredSub already carries the composed source-to-canvas affine.
        async IAsyncEnumerable<RawBayerFrame> RawBayerProducer(
            ImmutableArray<int> pick, [EnumeratorCancellation] CancellationToken token)
        {
            foreach (var i in pick)
            {
                token.ThrowIfCancellationRequested();
                var sub = subsList[i];
                var raw = await sub.Source.LoadFullAsync(token);
                yield return new RawBayerFrame(calibrator?.Apply(raw) ?? raw, sub.TransformToCanvas);
            }
        }
    }

    /// <summary>Quad match across the tolerance ladder; tight first, loosen on failure.
    /// Verbatim from <c>StackingPipeline.TryMatchAsync</c>; <c>FindFitAsync</c> memoises the
    /// quad build per <paramref name="maxStars"/> key, so looser retries only re-run the match
    /// pass, not the (expensive) quad construction.</summary>
    private static async Task<(Matrix3x2? Solution, float QuadTolerance, float RmsResidualPx)> TryMatchAsync(
        SortedStarList light, SortedStarList reference, int maxStars)
    {
        foreach (var tol in QuadTolerances)
        {
            var (solution, rmsPx) = await light.FindOffsetAndRotationWithRmsAsync(
                reference, minimumCount: 6, quadTolerance: tol, maxStars: maxStars);
            if (solution is not null)
            {
                return (solution, tol, rmsPx);
            }
        }
        return (null, float.NaN, float.NaN);
    }

    /// <summary>Maps a portable session id (<c>relative/dir|CAMERA</c>) to a single
    /// filesystem-safe scratch folder name.</summary>
    private static string Sanitize(string id)
    {
        var buf = id.ToCharArray();
        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] is '/' or '\\' or '|' or ':' or '*' or '?' or '"' or '<' or '>')
            {
                buf[i] = '_';
            }
        }
        return new string(buf);
    }
}
