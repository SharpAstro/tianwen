using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using TianWen.Lib.Geometry;
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
///     warped onto a common union canvas. Cell (i, j) of any two subs is an N2N training
///     pair by construction (§2.4). A STAGED session persists them as scratch FITS, which
///     is what its integrator consumes; a DRIZZLED one persists nothing, because its
///     integrator takes the raw CFA instead and the tiler re-warps what it needs
///     (<see cref="WarpedSubSource"/>, which is how either kind is read back).</item>
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

    /// <summary>Label for the side the session STARTED on, in <see cref="ImagingSession.FlipSide"/>
    /// and so in the session id. Time order rather than West/East, because the split is measured off
    /// the field rotation and a rotation does not say which side of the pier the tube was on.</summary>
    public const string FlipSideFirst = "a";

    /// <summary>Label for the side after the flip. See <see cref="FlipSideFirst"/>.</summary>
    public const string FlipSideSecond = "b";

    /// <summary>Half a turn, the rotation a meridian flip puts between the two halves of a session.</summary>
    private const float FlipDegrees = 180f;

    /// <summary>
    /// Splits registered subs by FIELD ORIENTATION: the ones lying like the first sub, and the ones
    /// about half a turn from it. Empty second group when the session never flipped, which is the
    /// ordinary case and the one that must cost nothing.
    ///
    /// <para>The angle comes from each sub's own source-to-canvas transform, so it measures what the
    /// registration actually found rather than trusting a PIERSIDE card (absent from 21 of the 91
    /// sessions in the 2026-09-17 bake). The threshold is a quarter turn, which is not a tuning knob:
    /// a flip is 180 degrees and ordinary field rotation over a night is a fraction of a degree, so
    /// anything in between is a session whose frames do not belong on one canvas at all.</para>
    /// </summary>
    internal static (ImmutableArray<int> First, ImmutableArray<int> Second) SplitByFieldRotation(
        ImmutableArray<RegisteredSub> subs)
    {
        if (subs.Length < 2)
        {
            return ([], []);
        }
        var first = ImmutableArray.CreateBuilder<int>();
        var second = ImmutableArray.CreateBuilder<int>();
        var anchor = RotationDegrees(subs[0].TransformToCanvas);
        for (var i = 0; i < subs.Length; i++)
        {
            var delta = MathF.Abs(NormaliseDegrees(RotationDegrees(subs[i].TransformToCanvas) - anchor));
            (delta <= FlipDegrees / 2f ? first : second).Add(i);
        }
        return (first.ToImmutable(), second.ToImmutable());
    }

    /// <summary>The rotation an affine places its source at, in degrees. A warp's linear part is a
    /// rotation times a scale, so the first column's angle is the rotation whatever the scale.</summary>
    private static float RotationDegrees(Matrix3x2 m) => MathF.Atan2(m.M12, m.M11) * (180f / MathF.PI);

    /// <summary>An angle difference folded into (-180, 180], so 359 degrees reads as -1 and not as
    /// most of a turn.</summary>
    private static float NormaliseDegrees(float degrees)
    {
        var d = degrees % 360f;
        if (d > 180f)
        {
            d -= 360f;
        }
        else if (d <= -180f)
        {
            d += 360f;
        }
        return d;
    }

    /// <summary>What a staged session's warped scratch would need against what the scratch drive has,
    /// when the first does not fit in the second.</summary>
    /// <param name="NeedBytes">Bytes the warped subs would occupy.</param>
    /// <param name="FreeBytes">Bytes free on <paramref name="Drive"/> when asked.</param>
    /// <param name="Drive">Root of the scratch drive, for a message that names where to look.</param>
    internal sealed record ScratchShortfallReport(long NeedBytes, long FreeBytes, string Drive);

    /// <summary>
    /// Whether the warped subs of a STAGED session fit on the scratch drive, answered before the
    /// first one is written. Null means they fit (or that the drive could not be interrogated, which
    /// is never worth refusing a session over).
    ///
    /// <para>The requirement is exactly the file the warp pass writes, once per sub: a canvas-sized
    /// float32 FITS of <paramref name="channels"/> planes (see <see cref="WarpedChannelCount"/> --
    /// three for anything that debayers, ONE for a mono session). A tenth is added on top for the
    /// integrator's own staging and the FITS headers, so a session that just fits is not admitted to
    /// fail at its last frame.</para>
    ///
    /// <para>Asked because the alternative is how the failure actually presented: the 861-sub
    /// ASI294MC eta Car session measured, registered and warped for about forty minutes before dying
    /// at <c>warped_0572.fits</c> with <c>nom.tam.fits.FitsException: IO Error on image write: There
    /// is not enough space on the disk</c>, an exception that names a frame number and a path and
    /// nothing about the session being three times the size of the disk it was given.</para>
    /// </summary>
    /// <summary>
    /// Planes the warp pass writes per sub. <see cref="Image.DebayerAsync"/> is a documented NO-OP
    /// for <see cref="SensorType.Monochrome"/> and <see cref="FrameRegistration.WarpToCanvasAsync"/>
    /// debayers BEFORE warping, so a mono session's <c>warped_*.fits</c> carries one plane where a
    /// CFA mosaic or an already-colour frame carries three.
    /// <para>
    /// Worth its own method because assuming three is not a harmless over-estimate: it refuses a mono
    /// session three times sooner than the disk requires. The eta Car Ha session -- 328 subs on a
    /// 4714x3558 canvas -- was refused at a claimed 67.6 GB against 59.0 GB free, when its real
    /// requirement was 24.2 GB and it would have fitted with room to spare.
    /// </para>
    /// </summary>
    internal static int WarpedChannelCount(SensorType sensorType)
        => sensorType == SensorType.Monochrome ? 1 : 3;

    internal static ScratchShortfallReport? ScratchShortfall(
        string scratchDir, int subCount, int canvasWidth, int canvasHeight, int channels)
    {
        var need = (long)subCount * canvasWidth * canvasHeight * channels * sizeof(float);
        need += need / 10;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(scratchDir));
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }
            var free = new DriveInfo(root).AvailableFreeSpace;
            return free < need ? new ScratchShortfallReport(need, free, root) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A drive that will not answer is not evidence of anything; let the write decide.
            return null;
        }
    }

    /// <summary>The cap on the brightest stars that form the quad fingerprints. Lives in
    /// <see cref="FrameRegistration"/> now: it used to be declared here AND in
    /// <c>StackingPipeline</c>, which is how the two ended up at 100 and 500 respectively with only
    /// a comment noting the divergence. (The matched-star floor is applied inside the shared
    /// <see cref="RegisterLoop"/> and needs no alias here.)</summary>
    private const int QuadStars = FrameRegistration.DefaultQuadStars;

    /// <summary>One surviving light registered onto the session's union canvas.</summary>
    /// <param name="Source">The original raw light (header-only handle). Carries the FITS
    /// metadata, gain, exposure, filter, temperature, that the tile manifest (#40) needs;
    /// the scratch FITS holds pixels only.</param>
    /// <param name="WarpedPath">Scratch FITS of the calibrated + debayered sub warped to the
    /// canvas grid (float32, linear, NaN outside the source footprint). Shares the exact
    /// pixel grid with every other sub and the master, so cell (i, j) is a fixed sky footprint
    /// across the whole session. <c>null</c> on a DRIZZLED session, which writes none: nothing
    /// there reads a warped sub until the tiler does, and it re-warps from
    /// <paramref name="Source"/> instead. Read one through
    /// <see cref="RegisteredSession.WarpedSubs"/> rather than this path, and never assume it is
    /// on disk.</param>
    /// <param name="TransformToCanvas">Composed source→canvas affine (registration transform
    /// left-multiplied by the union-canvas shift). Also what a re-warp places the sub with, so it
    /// is the one number both routes to a warped sub share.</param>
    /// <param name="Metrics">The sub's PSF metrics from the gate (retained, not recomputed);
    /// median HFD/FWHM/ellipticity + star count. Feeds the per-tile manifest + stats report.</param>
    public sealed record RegisteredSub(
        FrameInfo Source,
        string? WarpedPath,
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
    /// <summary>
    /// One light that was MEASURED and did not reach the master, with the stage that dropped it and
    /// the metrics it was judged on.
    /// <para>
    /// Kept because the store recorded only the subs that registered, so a dropped one left no trace
    /// at all: answering "why did this session lose 33 frames" meant listing the archive folder,
    /// diffing it against the store's own sub list to recover the names, and re-measuring frames by
    /// hand. Everything here was already computed by the measure pass -- the gate runs after it -- so
    /// this costs a list, not a measurement.
    /// </para>
    /// </summary>
    /// <param name="Stage">Which stage dropped it: <see cref="DropStageGate"/> suffixed with the
    /// failing criterion (the reason is a FLAGS enum, so a frame can fail several at once),
    /// <see cref="DropStageTooFewStars"/>, or <see cref="DropStageNoQuadFit"/>.</param>
    public sealed record DroppedSub(FrameInfo Source, string Stage, FrameMetrics Metrics);

    /// <summary>The session-relative quality gate, which is where most drops happen: 523 of the 607
    /// lost in the 2026-09-19 bake, against 84 for registration.</summary>
    public const string DropStageGate = "gate";

    /// <summary>Fewer detected stars than <see cref="FrameRegistration.MinStarsForMatch"/>.</summary>
    public const string DropStageTooFewStars = "too-few-stars";

    /// <summary>Quads that matched the reference at no rung of the tolerance ladder.</summary>
    public const string DropStageNoQuadFit = "no-quad-fit";

    public sealed record RegisteredSession(
        ImagingSession Session,
        Image Master,
        ImmutableArray<RegisteredSub> Subs,
        int CanvasWidth,
        int CanvasHeight,
        PixelRect StatsRect,
        FrameInfo Reference,
        int GatedCount,
        int RegisteredCount,
        int SkippedCount,
        IntegrationStrategyKind MasterStrategy = IntegrationStrategyKind.Float16Staged,
        Image? HalfMasterA = null,
        Image? HalfMasterB = null)
    {
        /// <summary>
        /// Every measured light that did NOT reach the master, with the stage that dropped it. Empty
        /// on a session that lost nothing, and on a hand-built session in a test.
        /// <para><see cref="GatedCount"/> and <see cref="SkippedCount"/> are the same information
        /// counted; this is the same information ITEMISED, which is what turns "a third of that
        /// session vanished" from a re-run into a sort.</para>
        /// </summary>
        public ImmutableArray<DroppedSub> Dropped { get; init; } = [];

        /// <summary>How to obtain a sub warped onto this session's canvas, whichever way the session
        /// was built: the scratch FITS where one was written, a re-warp of the source where it was
        /// not. <b>Every reader of a warped sub goes through this</b>, because
        /// <see cref="RegisteredSub.WarpedPath"/> is null for every drizzled session.
        /// <c>null</c> only on a hand-built session in a test that never asks for one.</summary>
        public WarpedSubSource? WarpedSubs { get; init; }

        /// <summary>
        /// The integration's per-pixel map for <see cref="Master"/>: accumulated WEIGHT from a drizzle,
        /// a rejection FRACTION from every other strategy, null when nothing was rejected.
        /// <see cref="RejectionMapIsCoverage"/> is what tells the two apart, and a writer must stamp it.
        /// </summary>
        /// <remarks>Kept because a retained master without it forces every downstream crop onto the
        /// edge-noise ESTIMATE. The walk measures where noise settles and a partial-coverage band is a
        /// LEVEL, about 0.1 percent deep, so it can refuse an edge and leave a ramp that a background
        /// model then fits.</remarks>
        public Image? RejectionMap { get; init; }

        /// <summary>Whether <see cref="RejectionMap"/> is a coverage/weight plane (high is well
        /// covered) rather than a rejection fraction (high is heavily rejected).</summary>
        public bool RejectionMapIsCoverage { get; init; }

        /// <summary>
        /// The same night integrated once per FIELD ORIENTATION when it crossed the meridian, empty
        /// otherwise. Each is a session in its own right (its own master, stats rect, subs and, where
        /// it has the frames for one, its own half-master pair) and carries
        /// <see cref="ImagingSession.FlipSide"/>, so its id is the night's plus the side.
        ///
        /// <para><b>These share sky with this session and with each other.</b> Anything that splits
        /// the dataset must keep them together, which is what <see cref="DatasetSplitWriter.GroupIdOf"/>
        /// is for; treating them as independent sessions would put the same sky in train and test.</para>
        /// </summary>
        public ImmutableArray<RegisteredSession> FlipSides { get; init; } = [];

        /// <summary>This session and every flip side of it, which is what a consumer that writes a
        /// master, tiles it or measures it should iterate: all of them are outputs of the bake.</summary>
        public IEnumerable<RegisteredSession> SelfAndFlipSides()
        {
            yield return this;
            foreach (var side in FlipSides)
            {
                yield return side;
            }
        }
    }

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
    /// <param name="warpInterpolation">The resampling kernel that places each sub on the canvas, the
    /// same choice <c>StackingOptions.WarpInterpolation</c> gives the stacker, so a retained master and
    /// a user's master of the same night are built the same way. Every master baked before 7.1 was
    /// bilinear, which carries about a pixel of FWHM in quadrature at 2 px seeing
    /// (<c>docs/plans/deconvolver-training.md</c>, R1); the bake's <c>bake-provenance.json</c> records
    /// the argument a run was given, which is how two stores are told apart.</param>
    /// <param name="hotPixelSigma">One knob, two producers, and the shipped mask is their UNION:
    /// it is the per-frame outlier sigma of the session-derived <see cref="BadPixelAccumulator"/>
    /// map (built whenever the registered transforms prove the session dithered/drifted enough to
    /// separate stars from defects; the two producers flag nearly disjoint real populations, see
    /// <see cref="BadPixelAccumulator.UnionInto"/>) AND the STARTING CEILING for the sigma above
    /// the dark's own background at which a pixel is masked out of drizzle deposition, matching
    /// <c>StackingOptions.HotPixelSigma</c>. Zero disables both. It is a ceiling rather than the
    /// threshold because sigma is not portable between darks: it multiplies a quantized MAD, so the
    /// same number recovered 32.95% of a sensor's consensus defect set on one master dark and
    /// 74.77% on another from the SAME sensor at a different gain.
    /// <see cref="BadPixelDetection.BuildMaskFromDark"/> therefore walks it down to a defect budget,
    /// which brought both to 86-89%. <b>This is not redundant with dark subtraction</b>, which is what
    /// <see cref="TryDrizzle"/> originally assumed. <see cref="Calibrator"/> now rescales the dark's
    /// thermal component to the light's EXPOSURE, but temperature is still uncompensated, and many
    /// hot pixels are non-linear or telegraph-noise unstable so no scaling of any kind removes them.
    /// On this archive the exposure half is close to inert regardless: every master dark's median
    /// equals its master bias's median to the ADU, so the rescale only ever moves defective pixels,
    /// which is precisely the population the mask exists for. That is why PixInsight's
    /// CosmeticCorrection runs in addition to dark subtraction rather than instead of it.</param>
    /// <param name="skipStorePath">Optional <see cref="DatasetSkipStore"/> path. Every way this
    /// method drops a session without throwing appends a record there, so a bake-to-bake comparison
    /// of WHICH sessions failed is a diff rather than a log grep. Null disables it.</param>
    /// <param name="timings">Optional per-stage accounting (<see cref="StageTimings"/>). Populated
    /// with <see cref="StageNames.Measure"/>, <see cref="StageNames.Register"/>,
    /// <see cref="StageNames.Warp"/>, <see cref="StageNames.Integrate"/> and
    /// <see cref="StageNames.Halves"/>, each carrying the item and pixel counts it processed so the
    /// caller never has to infer a denominator. Null (the default) records nothing and costs nothing.
    /// <b>Not shared across concurrent sessions</b>; see the type's remarks.</param>
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
        WarpInterpolation warpInterpolation = WarpInterpolation.Lanczos3Clamped,
        float hotPixelSigma = 8f,
        string? skipStorePath = null,
        StageTimings? timings = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Measure every light: calibrate -> debayer -> detect stars -> PSF metrics.
        //    The star list is retained on each AnalyzedFrame so nothing below re-detects.
        //    The session bad-pixel accumulator (task #22) rides along: the measure pass is the one
        //    place the calibrated pixels are already in hand, and every measured light votes --
        //    gate-rejected frames included, since a sensor defect does not care about clouds.
        var measureStart = StageTimings.Start();
        var badPixelAccumulator = hotPixelSigma > 0f ? new BadPixelAccumulator(hotPixelSigma) : null;
        var analyzed = new List<SessionFrameAnalyzer.AnalyzedFrame>(session.Lights.Length);
        var measuredPixels = 0L;
        foreach (var light in session.Lights)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await SessionFrameAnalyzer.MeasureAsync(
                light, calibrator, debayerAlgorithm, badPixels: badPixelAccumulator, cancellationToken: cancellationToken);
            analyzed.Add(frame);
            measuredPixels += (long)frame.Frame.Width * frame.Frame.Height;
        }
        timings?.Record(StageNames.Measure, measureStart, analyzed.Count, measuredPixels);

        // 2. Session-relative quality gate (star-count-led; see SessionFrameAnalyzer doc).
        var gate = SessionFrameAnalyzer.ApplyGate(analyzed, qualityRejectSigma, qualityMaxRejectFraction);

        // Itemised as they are dropped, because nothing downstream can reconstruct them: the gate's
        // rejects carry their own typed reason here and are unreachable once this scope ends.
        var dropped = ImmutableArray.CreateBuilder<DroppedSub>();
        foreach (var (rejected, reason) in gate.Rejected)
        {
            dropped.Add(new DroppedSub(rejected.Frame, $"{DropStageGate}:{reason}", rejected.Metrics));
        }
        logger?.LogInformation(
            "  [{Session}] gate: kept {Kept}/{Total} ({Rejected} rejected{Floor})",
            session.Id, gate.Kept.Length, analyzed.Count, gate.Rejected.Length,
            gate.KeepFloorTriggered ? ", floor" : "");
        if (gate.Kept.Length < minSubs)
        {
            // Censused over everything MEASURED, not over the survivors, because at this point the
            // question is what the gate threw out and why. No quads exist yet, so that half is empty.
            var gateCensus = RegistrationCensus.Measure(
                [.. analyzed.Select(static a => a.Metrics.StarCount)],
                [],
                [.. analyzed.Select(static a => a.Metrics.MedianHfd)],
                [.. analyzed.Select(static a => a.Metrics.MedianEllipticity)]);
            logger?.LogWarning("  [{Session}] {Kept} subs survived the gate (< {Min}) -- skipped. census {Census}",
                session.Id, gate.Kept.Length, minSubs, RegistrationCensus.Describe(gateCensus));
            await DatasetSkipStore.RecordAsync(skipStorePath, new DatasetSkipStore.SkippedSession(
                SessionId: session.Id,
                Reason: "gate-kept-below-min-subs",
                Survivors: gate.Kept.Length,
                Registered: 0,
                SkippedTooFewStars: 0,
                SkippedNoQuadFit: 0,
                ReferenceFile: null,
                ReferenceStars: 0,
                ReferenceQuads: 0,
                Census: gateCensus), logger, cancellationToken);
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
            var score = FrameRegistration.ReferenceScore(f.Metrics);
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

        // 4. Register each survivor against the reference from the RETAINED star lists, through the
        //    shared RegisterLoop -- the SAME loop body StackingPipeline runs (min-stars floor ->
        //    quad-form -> tolerance ladder -> rigid refine, census + skip counters included), so
        //    the two paths cannot drift here. What stays local is exactly what differs: Debug-level
        //    per-frame logs keyed by session id, and the AnalyzedFrame result tuples.
        var registerStart = StageTimings.Start();
        using var registerLoop = await RegisterLoop.CreateAsync(reference.Stars, QuadStars, cancellationToken);
        logger?.LogInformation("  [{Session}] reference quads={Quads} from {Stars} retained stars (top {Cap})",
            session.Id, registerLoop.ReferenceQuadCount, registerLoop.ReferenceStarCount, QuadStars);
        var matched = new List<(SessionFrameAnalyzer.AnalyzedFrame Frame, Matrix3x2 Transform)>(survivors.Length);
        // The refiner's unmoved-pair count over the session. A pair that sits where the light's
        // detection already was, while the bulk transform says it should have moved, is a detection
        // fixed to the SENSOR (a residual warm pixel pairing with its own copy in the reference), and
        // the count is the one tell a bake log has for a warm-pixel night: the stacker prints it per
        // frame, and until now no bake log carried it at all (docs/plans/deconvolver-training.md,
        // "the third finding placed").
        var unmovedDropped = 0;
        foreach (var f in survivors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(f, reference))
            {
                // The reference still contributes to the census: it is one of the frames whose
                // focus the census is describing, and excluding it would silently drop the
                // sharpest sample from the spread.
                registerLoop.AddReference(f.Metrics);
                matched.Add((f, Matrix3x2.Identity));
                continue;
            }
            var attempt = await registerLoop.RegisterAsync(f.Stars, f.Metrics, cancellationToken);
            if (attempt.Transform is not { } transform)
            {
                dropped.Add(new DroppedSub(
                    f.Frame,
                    attempt.Skip is RegisterLoop.SkipCause.TooFewStars ? DropStageTooFewStars : DropStageNoQuadFit,
                    f.Metrics));
                if (attempt.Skip is RegisterLoop.SkipCause.TooFewStars)
                {
                    logger?.LogDebug("  [{Session}] {File} stars={Stars} (< {Min}) -> skip (too few)",
                        session.Id, Path.GetFileName(f.Frame.Path), f.Stars.Count, FrameRegistration.MinStarsForMatch);
                }
                else
                {
                    // Both counts are already in hand, so state them: "no fit" on its own cannot
                    // separate a DETECTION problem (few quads on this sub) from a GEOMETRY one
                    // (plenty of quads on both sides that still do not correspond), and those two
                    // have opposite fixes.
                    // HFD and ellipticity are stated alongside the counts because they are what says
                    // WHY the counts look as they do, and they were already measured. Without them a
                    // reconstructed histogram can show that a session was not star-poor but still
                    // cannot show that its focus was drifting, which is the next question every time.
                    logger?.LogDebug(
                        "  [{Session}] {File} stars={Stars} quads={Quads} hfd={Hfd:F2} ecc={Ecc:F3} vs reference quads={RefQuads} -> skip (no quad fit up to tolerance {MaxTol})",
                        session.Id, Path.GetFileName(f.Frame.Path), f.Stars.Count, attempt.LightQuads,
                        f.Metrics.MedianHfd, f.Metrics.MedianEllipticity,
                        registerLoop.ReferenceQuadCount, FrameRegistration.QuadTolerances[^1]);
                }
                continue;
            }
            unmovedDropped += attempt.RefineUnmovedDropped;
            logger?.LogDebug(
                "  [{Session}] {File} stars={Stars} quads={Quads} hfd={Hfd:F2} ecc={Ecc:F3} -> matched at tolerance {Tol} (rms {Rms:F2} px; refine rms {RefineRms:F2} px from {Pairs} pairs, {Unmoved} unmoved dropped)",
                session.Id, Path.GetFileName(f.Frame.Path), f.Stars.Count, attempt.LightQuads,
                f.Metrics.MedianHfd, f.Metrics.MedianEllipticity,
                attempt.QuadTolerance, attempt.MatchRmsPx, attempt.RefineRmsPx, attempt.RefineMatchedPairs, attempt.RefineUnmovedDropped);
            matched.Add((f, transform));
        }
        // Items are the SURVIVORS, not the matches: the ones that failed still cost their quad-form
        // and their trip up the tolerance ladder, so charging the stage only for its successes would
        // make a session that fails everything look arbitrarily fast. No pixels: registration reads
        // star centroids only, never a pixel, which is the whole reason it is a rounding error next
        // to every other stage.
        timings?.Record(StageNames.Register, registerStart, survivors.Length);
        var spread = registerLoop.MeasureCensus();
        var census = RegistrationCensus.Describe(spread);
        logger?.LogInformation(
            "  [{Session}] registered {Matched}/{Survivors} (skipped {Skipped}: {TooFew} too-few-stars, {NoFit} no-quad-fit); refine dropped {Unmoved} unmoved pairs; census {Census}",
            session.Id, matched.Count, survivors.Length, registerLoop.SkippedTooFewStars + registerLoop.SkippedNoQuadFit,
            registerLoop.SkippedTooFewStars, registerLoop.SkippedNoQuadFit, unmovedDropped, census);
        if (matched.Count < 2)
        {
            // WARNING level, which is what a bake log actually shows, so it has to be
            // self-diagnosing. The bare form of this message cost a whole-session drop that could
            // not be explained without re-running the session at Debug: it named neither the star
            // counts nor the quad counts, both of which were already computed right here.
            //
            // Read the CENSUS, because it separates the only two causes and those have opposite
            // fixes. FEW quads everywhere is a detection problem. PLENTY of quads on both
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
            //
            // The census carries the per-frame spread, so the min/max quad range this message used
            // to print separately is gone rather than duplicated: two renderings of the same numbers
            // is how one of them ends up stale.
            logger?.LogWarning(
                "  [{Session}] fewer than 2 registered subs -- skipped. reference {RefFile} stars={RefStars} quads={RefQuads}, skipped {TooFew} too-few-stars + {NoFit} no-quad-fit. census {Census}",
                session.Id, Path.GetFileName(reference.Frame.Path),
                registerLoop.ReferenceStarCount, registerLoop.ReferenceQuadCount,
                registerLoop.SkippedTooFewStars, registerLoop.SkippedNoQuadFit, census);
            await DatasetSkipStore.RecordAsync(skipStorePath, new DatasetSkipStore.SkippedSession(
                SessionId: session.Id,
                Reason: "fewer-than-2-registered",
                Survivors: survivors.Length,
                Registered: matched.Count,
                SkippedTooFewStars: registerLoop.SkippedTooFewStars,
                SkippedNoQuadFit: registerLoop.SkippedNoQuadFit,
                ReferenceFile: Path.GetFileName(reference.Frame.Path),
                ReferenceStars: registerLoop.ReferenceStarCount,
                ReferenceQuads: registerLoop.ReferenceQuadCount,
                Census: spread), logger, cancellationToken);
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

        // Built before the pass that decides whether anything is written, because it is what closes
        // the two routes to a warped sub: read the scratch FITS, or warp the source again with the
        // very arguments this pass is about to use.
        var warpedSubs = new WarpedSubSource(calibrator, debayerAlgorithm, warpInterpolation, canvasW, canvasH);

        var warpStart = StageTimings.Start();
        var subs = ImmutableArray.CreateBuilder<RegisteredSub>(matched.Count);
        // Captured off the first raw load: the drizzle gate keys off it and there is no cheaper
        // authoritative source here. Invariant within a session by construction (frames with a
        // different sensor land in a different group).
        var sensorType = SensorType.Unknown;
        // Every sub's per-CFA-colour sky, taken off the calibrated raw frame this loop already holds,
        // for the drizzle sky reference below. Cheap beside the warp, and the only pass that sees
        // every frame before integration.
        var cfaSkies = new List<Normalizer.CfaNormalizationStats>(matched.Count);
        // The STRATEGY is decided here, one raw load in, rather than between this pass and the
        // integration where it used to sit -- because it decides whether this pass has to write
        // anything at all. DrizzleStrategy consumes IntegrationJob.RawBayerFrames only (it
        // forward-projects the raw CFA, which is the whole point of it) and both half-masters take
        // the master's strategy, so on a drizzled session the warped scratch has exactly ONE reader:
        // the tiler's per-sub pass, which runs after the master exists and can re-warp a sub from its
        // source instead (see WarpedSubSource). Not writing it takes such a session's disk cost from
        // subs x canvas bytes to nothing, and that is not a marginal saving: the 861-sub ASI294MC eta
        // Car session needs ~120 GB of scratch and died mid-warp on a full disk in the 2026-09-17 bake.
        var useDrizzle = false;
        for (var i = 0; i < matched.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (f, transform) = matched[i];
            var raw = await f.Frame.LoadFullAsync(cancellationToken);
            if (i == 0)
            {
                sensorType = raw.ImageMeta.SensorType;
                var probe = IntegrationProbe.Snapshot(
                    frameCount: matched.Count,
                    frameWidth: refW,
                    frameHeight: refH,
                    channelCount: 3,
                    canvasWidth: canvasW,
                    canvasHeight: canvasH,
                    stagingDir: sessionScratch,
                    sensorType: sensorType);
                useDrizzle = TryDrizzle(probe, calibrator, logger, session.Id);
                // Only the staged path writes, so only it has to fit. Asked BEFORE the first warp
                // rather than discovered at the frame that fills the disk: the 861-sub session above
                // spent forty minutes to fail at warped_0572, and the requirement was knowable from
                // the sub count and the canvas before any of it.
                if (!useDrizzle && ScratchShortfall(
                        sessionScratch, matched.Count, canvasW, canvasH, WarpedChannelCount(sensorType)) is { } shortfall)
                {
                    logger?.LogWarning(
                        "  [{Session}] needs {Need:F1} GB of warped scratch for {Subs} subs on a {W}x{H} canvas and " +
                        "{Free:F1} GB is free on {Drive} -- skipped. Point --scratch-root at a bigger disk.",
                        session.Id, shortfall.NeedBytes / (double)(1L << 30), matched.Count, canvasW, canvasH,
                        shortfall.FreeBytes / (double)(1L << 30), shortfall.Drive);
                    await DatasetSkipStore.RecordAsync(skipStorePath, new DatasetSkipStore.SkippedSession(
                        SessionId: session.Id,
                        Reason: "scratch-space-insufficient",
                        Survivors: survivors.Length,
                        Registered: matched.Count,
                        SkippedTooFewStars: registerLoop.SkippedTooFewStars,
                        SkippedNoQuadFit: registerLoop.SkippedNoQuadFit,
                        ReferenceFile: Path.GetFileName(reference.Frame.Path),
                        ReferenceStars: registerLoop.ReferenceStarCount,
                        ReferenceQuads: registerLoop.ReferenceQuadCount,
                        Census: spread), logger, cancellationToken);
                    return null;
                }
            }
            var calibrated = calibrator?.Apply(raw) ?? raw;
            if (calibrated.IsCfaMosaic)
            {
                cfaSkies.Add(Normalizer.ComputeCfaStats(calibrated));
            }
            if (useDrizzle)
            {
                // The composition WarpToCanvasAsync would have returned, which is the only part of it
                // a drizzled session needs; kept in lockstep with that method's own `shifted`.
                subs.Add(new RegisteredSub(f.Frame, null, transform * canvasShift, f.Metrics));
                continue;
            }
            // The shared debayer + warp step (FrameRegistration.WarpToCanvasAsync) -- the same
            // three lines StackingPipeline's producer runs, so the two paths cannot drift here.
            var (warped, shifted) = await FrameRegistration.WarpToCanvasAsync(
                calibrated, transform, canvasShift, debayerAlgorithm, canvasW, canvasH, warpInterpolation, cancellationToken);
            var warpedPath = Path.Combine(sessionScratch, $"warped_{i:D4}.fits");
            warped.WriteToFitsFile(warpedPath);
            subs.Add(new RegisteredSub(f.Frame, warpedPath, shifted, f.Metrics));
        }
        var subsList = subs.MoveToImmutable();
        // Charged in CANVAS pixels, not source pixels: the warp writes a canvas-sized scratch FITS per
        // sub, and the canvas is larger than the frame by the session's whole dither excursion, so
        // source pixels would flatter it by whatever the dithering happened to be. Kept as its own
        // stage rather than folded into integrate, which is how the log reads it, because it is a
        // second full pass over the raw lights on the ARCHIVE disk while the integration that follows
        // reads scratch on a different one.
        timings?.Record(StageNames.Warp, warpStart, subsList.Length, (long)subsList.Length * canvasW * canvasH);

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
        // useDrizzle was settled in the warp pass above, off the first raw load's SensorType and the
        // same probe this line used to build; it has to be known before a sub is warped because it
        // decides whether a warped sub is written at all.

        // Hot-pixel mask, the thing this path was missing. StackingPipeline has built one since it
        // hit this exact failure ("visible hot-pixel clusters survived into the master", its own
        // comment at the equivalent site); the dataset path relied instead on TryDrizzle's
        // matched-dark requirement and so wrote them into 45 of 64 drizzled masters. Measured on
        // 2025-12-28 HIP 34710: nineteen clusters across R/G/B, every substantial one a ~36x25 px
        // footprint whose shape is congruent with every other to 1.2-3.2 px after centring, which
        // is the session's whole dither excursion. That congruence is the proof they are single
        // fixed sensor defects: nothing position-dependent can paint the same shape everywhere,
        // and the union canvas grew by exactly (+37, +27) for the same reason.
        //
        // Built unconditionally, like StackingPipeline's, rather than gated on useDrizzle: only
        // the drizzle strategies read IntegrationJob.BadPixelMask (the staged path's sigma-clip
        // handles outliers across N frames), so an unused mask costs tens of ms, while a mask
        // gated on a strategy decision is precisely the bug the stacking side already had.
        BitMatrix[]? badPixelMask = null;
        if (calibrator?.Dark is { } darkMaster && hotPixelSigma > 0f)
        {
            badPixelMask = BadPixelDetection.BuildMaskFromDark(darkMaster, hotPixelSigma, logger);
            // The sigma passed in is a STARTING CEILING, not the threshold that ends up being used
            // (BadPixelDetection walks it down to the defect budget), so this line reports the
            // resulting count and the fraction it represents. The per-channel line from the
            // detector itself carries the sigma and threshold actually chosen.
            var flagged = BadPixelDetection.CountMaskedPixels(badPixelMask, darkMaster.Width, darkMaster.Height);
            logger?.LogInformation("  [{Session}] hot-pixel mask: {Count} px ({Pct:F3}% of frame), from sigma<={Sigma:F1}",
                session.Id,
                flagged,
                flagged * 100.0 / (darkMaster.Width * (double)darkMaster.Height * darkMaster.ChannelCount),
                hotPixelSigma);
        }
        // The session-derived map (task #22) joins the dark-derived one as a UNION when it can be
        // built (enough measured frames, the registered transforms prove real dither/drift,
        // flagged fraction sane): the two flag nearly DISJOINT real populations (see
        // BadPixelAccumulator.UnionInto) -- the dark map the stable dark-current defects, the
        // session map the pixels actively misbehaving at the lights' own exposure, gain and
        // temperature, which is the population the dark A/B proved is missed (35 of 52 residual
        // clusters stood at dark-derived sigma 8, six byte-identical to the unmasked run).
        // BuildMask refuses with a logged reason otherwise, and the dark mask above stands alone.
        if (badPixelAccumulator?.BuildMask(transforms, logger, session.Id) is { } registrationMask)
        {
            if (badPixelMask is { Length: > 0 } darkMask
                && calibrator?.Dark is { } dm && dm.Width == refW && dm.Height == refH)
            {
                var (shared, regOnly, darkOnly) = BadPixelAccumulator.Overlap(
                    registrationMask[0], darkMask[0], refW, refH);
                logger?.LogInformation(
                    "  [{Session}] bad-pixel masks: {Shared} px shared, {RegOnly} registration-only, {DarkOnly} dark-only; masking their union",
                    session.Id, shared, regOnly, darkOnly);
                BadPixelAccumulator.UnionInto(registrationMask[0], darkMask[0], refW, refH);
            }
            badPixelMask = registrationMask;
        }

        // One sky for the master AND both halves: the session's MEDIAN per CFA colour, over the same
        // calibrated raw frames the drizzle producer will hand over. Unnormalised drizzle weights the
        // four Bayer phases unevenly, so a sky that drifts through the session left a fixed 2x2 level
        // pattern in every drizzled master (median 0.28 sigma over the 2026-09-16 bake's 57, 8.9 at
        // worst). Shifting each frame onto this sky removes it while the master stays on the subs'
        // linear scale, which is why normalisation stays off. One reference for all three integrations
        // keeps the half-master pair on the same level.
        //
        // The median and not the registration reference's own sky: the reference is chosen for its
        // STARS, and on Statue of Liberty its sky was 2.1x the session's, which put the whole master on
        // that pedestal and halved every structure's contrast relative to the sky, the quantity the
        // tile stretch works in. The median keeps the master where the unshifted mean put it.
        Normalizer.CfaNormalizationStats? skyReference = null;
        if (useDrizzle && cfaSkies.Count == subsList.Length)
        {
            skyReference = MedianSky(cfaSkies);
            logger?.LogInformation(
                "  [{Session}] drizzle sky reference, median over {Frames} subs: R {R:F1} G {G:F1} B {B:F1}",
                session.Id, cfaSkies.Count,
                skyReference.Red.PerChannelMedian[0], skyReference.Green.PerChannelMedian[0], skyReference.Blue.PerChannelMedian[0]);
        }

        var all = Enumerable.Range(0, subsList.Length).ToImmutableArray();
        var integrateStart = StageTimings.Start();
        var integration = await IntegrateSubsetAsync(all, "_integrate");
        var master = integration.Master;
        timings?.Record(StageNames.Integrate, integrateStart, subsList.Length, (long)subsList.Length * canvasW * canvasH);
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
            // Both halves record into ONE stage, and between them they cover every sub exactly once,
            // so the per-item cost is directly comparable with Integrate above rather than being half
            // of it twice.
            var halvesStart = StageTimings.Start();
            halfA = (await IntegrateSubsetAsync(
                all.Where(i => i % 2 == 0).ToImmutableArray(), "_half_a")).Master;
            halfB = (await IntegrateSubsetAsync(
                all.Where(i => i % 2 == 1).ToImmutableArray(), "_half_b")).Master;
            timings?.Record(StageNames.Halves, halvesStart, subsList.Length, (long)subsList.Length * canvasW * canvasH);
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

        // 7c. Pier-side masters, when the session crossed the meridian. The flip is read off the
        //     REGISTRATION, not off a PIERSIDE card: the card is absent from a fifth of this archive
        //     (older SharpCap wrote none) while the transform onto the reference always says which
        //     way the field was lying. Registration already handled the rotation, so this costs one
        //     extra integration per side and no second measure or register pass.
        //
        //     Both are emitted because they answer different questions. The COMBINED master is
        //     deeper and is what carries the half-master pair (a drizzled half needs
        //     MinSubsForHalfMasters subs, which most single sides cannot reach: 23 of the 40 flipped
        //     sessions in the 2026-09-17 bake would lose their pair if only the sides were kept). The
        //     SIDES carry a coherent sky gradient, since a gradient is fixed to the horizon and
        //     reverses in sensor coordinates across the flip, and each covers its own canvas more
        //     fully, so its stats rect is larger and the tiler gets more usable cells out of it.
        var sides = ImmutableArray<RegisteredSession>.Empty;
        var (sideOne, sideTwo) = SplitByFieldRotation(subsList);
        if (sideOne.Length >= minSubs && sideTwo.Length >= minSubs)
        {
            logger?.LogInformation(
                "  [{Session}] field rotation splits it {First}/{Second} subs (meridian flip); integrating each side as its own master",
                session.Id, sideOne.Length, sideTwo.Length);
            var built = ImmutableArray.CreateBuilder<RegisteredSession>(2);
            foreach (var (label, pick) in new[] { (FlipSideFirst, sideOne), (FlipSideSecond, sideTwo) })
            {
                var (_, sideStats) = CanvasGeometry.ComputeFootprintsAndStatsRect(
                    [.. pick.Select(i => transforms[i])], canvasShift, refW, refH, canvasW, canvasH);
                var sideIntegration = await IntegrateSubsetAsync(pick, $"_flip_{label}", sideStats);
                var sideMaster = sideIntegration.Master;
                Image? sideHalfA = null;
                Image? sideHalfB = null;
                if (pick.Length >= halfMasterFloor)
                {
                    sideHalfA = (await IntegrateSubsetAsync(
                        [.. pick.Where((_, k) => k % 2 == 0)], $"_flip_{label}_half_a", sideStats)).Master;
                    sideHalfB = (await IntegrateSubsetAsync(
                        [.. pick.Where((_, k) => k % 2 == 1)], $"_flip_{label}_half_b", sideStats)).Master;
                }
                built.Add(new RegisteredSession(
                    session with { FlipSide = label }, sideMaster, [.. pick.Select(i => subsList[i])],
                    canvasW, canvasH, sideStats, reference.Frame, pick.Length, pick.Length, 0,
                    useDrizzle ? IntegrationStrategyKind.BayerDrizzle : IntegrationStrategyKind.Float16Staged,
                    sideHalfA, sideHalfB)
                {
                    WarpedSubs = warpedSubs,
                    RejectionMap = sideIntegration.TotalRejections > 0 ? sideIntegration.RejectionMap : null,
                    RejectionMapIsCoverage = sideIntegration.RejectionMapIsCoverage,
                });
            }
            sides = built.MoveToImmutable();
        }

        return new RegisteredSession(
            session, master, subsList, canvasW, canvasH, statsRect,
            reference.Frame, survivors.Length, matched.Count, registerLoop.SkippedTooFewStars + registerLoop.SkippedNoQuadFit,
            useDrizzle ? IntegrationStrategyKind.BayerDrizzle : IntegrationStrategyKind.Float16Staged,
            halfA, halfB)
        {
            WarpedSubs = warpedSubs,
            FlipSides = sides,
            RejectionMap = integration.TotalRejections > 0 ? integration.RejectionMap : null,
            RejectionMapIsCoverage = integration.RejectionMapIsCoverage,
            // On the COMBINED master only. A flip side is a subset of the same night's registered
            // subs, so attaching the session's drops to each side would count every one of them
            // twice and invite a reader to sum the sides.
            Dropped = dropped.ToImmutable(),
        };

        async Task<IntegrationResult> IntegrateSubsetAsync(ImmutableArray<int> pick, string scratchName, PixelRect? subsetStatsRect = null)
        {
            var scratch = Path.Combine(sessionScratch, scratchName);
            var job = new IntegrationJob(
                WarpedFrames: token => WarpedProducer(pick, token),
                ExpectedFrameCount: pick.Length,
                Options: new IntegrationOptions(Rejector: StackingPipeline.BuildRejector(pick.Length), ApplyNormalization: false)
                {
                    DrizzleSkyReference = skyReference,
                },
                StagingDir: scratch,
                // A pier-side subset covers less canvas than the session but covers it MORE FULLY, so
                // it gets its own all-frames intersection; passing the session's would take statistics
                // over canvas this subset never reached.
                StatsRect: subsetStatsRect ?? statsRect,
                // Footprints are indexed in registration order, the same order subsList is built
                // in, so the subset has to be taken in lockstep or every frame's coverage is
                // attributed to the wrong frame.
                FrameFootprints: [.. pick.Select(i => footprints[i])],
                CanvasWidth: canvasW,
                CanvasHeight: canvasH,
                RawBayerFrames: useDrizzle ? token => RawBayerProducer(pick, token) : null,
                DrizzleOptions: useDrizzle ? new DrizzleOptions() : null,
                BadPixelMask: badPixelMask);
            return useDrizzle
                ? await new DrizzleStrategy().RunAsync(job, cancellationToken)
                : await new Float16StagedStrategy().RunAsync(job, cancellationToken);
        }

        async IAsyncEnumerable<Image> WarpedProducer(
            ImmutableArray<int> pick, [EnumeratorCancellation] CancellationToken token)
        {
            foreach (var i in pick)
            {
                token.ThrowIfCancellationRequested();
                // Only Float16StagedStrategy consumes this producer, and only a staged session
                // materialises its subs, so this reads the scratch FITS every time; it goes through
                // the shared source anyway rather than opening the path itself, so there is one
                // definition of what a warped sub is.
                yield return await warpedSubs.LoadAsync(subsList[i], token);
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

    /// <summary>The per-colour median of the subs' skies (and of their floors), each colour taken
    /// independently. See the drizzle sky reference in <see cref="RegisterAsync"/> for why the median
    /// and not the registration reference's own sky.</summary>
    internal static Normalizer.CfaNormalizationStats MedianSky(IReadOnlyList<Normalizer.CfaNormalizationStats> skies)
    {
        if (skies.Count == 0)
        {
            throw new ArgumentException("No sub skies to take a median of.", nameof(skies));
        }

        return new Normalizer.CfaNormalizationStats(
            Colour(skies.Select(s => s.Red)), Colour(skies.Select(s => s.Green)), Colour(skies.Select(s => s.Blue)));

        static NormalizationStats Colour(IEnumerable<NormalizationStats> perSub)
        {
            var subs = perSub.ToArray();
            return new NormalizationStats(
                [Median(subs.Select(s => s.PerChannelFloor[0]))], [Median(subs.Select(s => s.PerChannelMedian[0]))]);
        }

        static float Median(IEnumerable<float> values)
        {
            var sorted = values.Order().ToArray();
            var mid = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
        }
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
