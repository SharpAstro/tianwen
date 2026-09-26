using System;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Sequencing.PolarAlignment;

namespace TianWen.Hosting.Dto;

/// <summary>
/// Polar alignment as the node's run: <c>POST /api/v1/polar</c> (P5 part 4 of docs/plans/hardware-in-the-server.md, #934),
/// the same routine the GUI runs, through the node's devices. It refines until <c>DELETE /api/v1/polar</c>, which is Done
/// and Cancel alike, and it stops by itself once no client has watched it for the node's detach grace.
/// </summary>
public sealed class PolarAlignmentRequestDto
{
    /// <summary>The OTA whose camera captures; an index outside the profile's OTAs means the first.</summary>
    public int OtaIndex { get; init; }

    /// <summary>Capture through the guider instead of an OTA's camera.</summary>
    public bool UseGuider { get; init; }

    /// <summary>How, every tunable; null runs the defaults.</summary>
    public PolarAlignmentConfigDto? Configuration { get; init; }
}

/// <summary>
/// <see cref="PolarAlignmentConfiguration"/> on the wire, EVERY field of it: the GUI's setup panel sets all of them, and a
/// field dropped here would silently run its default on the node. A field a caller leaves out (null) is the default,
/// applied as it arrives: the JSON source generator sets an absent init-only property to its type's default, so a
/// default written on the property would be zeroed rather than kept.
/// </summary>
public sealed class PolarAlignmentConfigDto
{
    /// <summary>The exposure ladder, in seconds, tried in order until a solve succeeds.</summary>
    public double[]? ExposureRampSeconds { get; init; }
    public int? MinStarsForSolve { get; init; }
    public double? RotationDeg { get; init; }
    public double? SettleSeconds { get; init; }
    public double? TargetAccuracyArcmin { get; init; }
    public PolarAlignmentOnDone? OnDone { get; init; }
    public bool? SaveFrames { get; init; }
    public int? MaxFrame2Retries { get; init; }
    public int? SmoothingWindow { get; init; }
    public double? SettleSigmaArcmin { get; init; }
    public int? RefineFullSolveInterval { get; init; }
    public bool? UseIncrementalSolver { get; init; }
    public int? ReferenceFrameAverages { get; init; }
    public int? RotationMinStars { get; init; }
    public int? RefineMinStars { get; init; }

    public static PolarAlignmentConfigDto From(in PolarAlignmentConfiguration configuration) => new PolarAlignmentConfigDto
    {
        ExposureRampSeconds = [.. configuration.ExposureRamp.Select(static exposure => exposure.TotalSeconds)],
        MinStarsForSolve = configuration.MinStarsForSolve,
        RotationDeg = configuration.RotationDeg,
        SettleSeconds = configuration.SettleSeconds,
        TargetAccuracyArcmin = configuration.TargetAccuracyArcmin,
        OnDone = configuration.OnDone,
        SaveFrames = configuration.SaveFrames,
        MaxFrame2Retries = configuration.MaxFrame2Retries,
        SmoothingWindow = configuration.SmoothingWindow,
        SettleSigmaArcmin = configuration.SettleSigmaArcmin,
        RefineFullSolveInterval = configuration.RefineFullSolveInterval,
        UseIncrementalSolver = configuration.UseIncrementalSolver,
        ReferenceFrameAverages = configuration.ReferenceFrameAverages,
        RotationMinStars = configuration.RotationMinStars,
        RefineMinStars = configuration.RefineMinStars,
    };

    public PolarAlignmentConfiguration ToConfiguration()
    {
        var defaults = PolarAlignmentConfiguration.Default;
        return new PolarAlignmentConfiguration(
            ExposureRamp: ExposureRampSeconds is { } ramp ? [.. ramp.Select(static seconds => TimeSpan.FromSeconds(seconds))] : defaults.ExposureRamp,
            MinStarsForSolve: MinStarsForSolve ?? defaults.MinStarsForSolve,
            RotationDeg: RotationDeg ?? defaults.RotationDeg,
            SettleSeconds: SettleSeconds ?? defaults.SettleSeconds,
            TargetAccuracyArcmin: TargetAccuracyArcmin ?? defaults.TargetAccuracyArcmin,
            OnDone: OnDone ?? defaults.OnDone,
            SaveFrames: SaveFrames ?? defaults.SaveFrames,
            MaxFrame2Retries: MaxFrame2Retries ?? defaults.MaxFrame2Retries,
            SmoothingWindow: SmoothingWindow ?? defaults.SmoothingWindow,
            SettleSigmaArcmin: SettleSigmaArcmin ?? defaults.SettleSigmaArcmin,
            RefineFullSolveInterval: RefineFullSolveInterval ?? defaults.RefineFullSolveInterval,
            UseIncrementalSolver: UseIncrementalSolver ?? defaults.UseIncrementalSolver,
            ReferenceFrameAverages: ReferenceFrameAverages ?? defaults.ReferenceFrameAverages,
            RotationMinStars: RotationMinStars ?? defaults.RotationMinStars,
            RefineMinStars: RefineMinStars ?? defaults.RefineMinStars);
    }
}

/// <summary>
/// The polar alignment going on, or the last one to end until the node's next run replaces it: <c>GET /api/v1/polar</c>.
/// Everything the GUI's polar panel draws from, losslessly: a value the routine does not have crosses as null, never 0.
/// The frame it captures is the OTA's (<c>/frames/ota{n}/latest</c>, announced by <c>FRAME-AVAILABLE</c>).
/// </summary>
public sealed class PolarStateDto
{
    /// <summary>The OTA whose camera it captures through; -1 for the guider.</summary>
    public int OtaIndex { get; init; }

    /// <summary>What it captures through, in words.</summary>
    public required string Source { get; init; }

    public PolarAlignmentPhase Phase { get; init; }

    public required string StatusMessage { get; init; }

    /// <summary>Whether it is going on: false once the mount has been restored and the devices given back.</summary>
    public bool Running { get; init; }

    /// <summary>Why it failed, in words, Phase A's failure included; null while it goes well, and after it ended well.</summary>
    public string? FailureReason { get; init; }

    public PolarPhaseADto? PhaseA { get; init; }

    /// <summary>The latest refinement tick.</summary>
    public PolarLiveSolveDto? LastSolve { get; init; }

    /// <summary>The latest plate solve, of the frame named by <see cref="FrameNumber"/>; null until one solved.</summary>
    public WcsDto? Wcs { get; init; }

    /// <summary>The token of the OTA's frame <see cref="Wcs"/> was solved from, as <c>/frames</c> numbers it.</summary>
    public int? FrameNumber { get; init; }
}

/// <summary><see cref="TwoFrameSolveResult"/> on the wire.</summary>
public sealed class PolarPhaseADto
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
    public double? AxisX { get; init; }
    public double? AxisY { get; init; }
    public double? AxisZ { get; init; }
    public double? AzErrorRad { get; init; }
    public double? AltErrorRad { get; init; }
    public double? ChordAngleObservedRad { get; init; }
    public double? ChordAnglePredictedRad { get; init; }
    public double LockedExposureSeconds { get; init; }
    public int StarsMatchedFrame1 { get; init; }
    public int StarsMatchedFrame2 { get; init; }
    public double? CommandedRotationRad { get; init; }
    public double? MeasuredRotationRad { get; init; }

    public static PolarPhaseADto From(in TwoFrameSolveResult result) => new PolarPhaseADto
    {
        Success = result.Success,
        FailureReason = result.FailureReason,
        AxisX = Wire.Finite(result.AxisJ2000.X),
        AxisY = Wire.Finite(result.AxisJ2000.Y),
        AxisZ = Wire.Finite(result.AxisJ2000.Z),
        AzErrorRad = Wire.Finite(result.AzErrorRad),
        AltErrorRad = Wire.Finite(result.AltErrorRad),
        ChordAngleObservedRad = Wire.Finite(result.ChordAngleObservedRad),
        ChordAnglePredictedRad = Wire.Finite(result.ChordAnglePredictedRad),
        LockedExposureSeconds = result.LockedExposure.TotalSeconds,
        StarsMatchedFrame1 = result.StarsMatchedFrame1,
        StarsMatchedFrame2 = result.StarsMatchedFrame2,
        CommandedRotationRad = Wire.Finite(result.CommandedRotationRad),
        MeasuredRotationRad = Wire.Finite(result.MeasuredRotationRad),
    };

    public TwoFrameSolveResult ToResult() => new TwoFrameSolveResult(
        Success,
        FailureReason,
        new Vec3(Wire.Value(AxisX), Wire.Value(AxisY), Wire.Value(AxisZ)),
        Wire.Value(AzErrorRad),
        Wire.Value(AltErrorRad),
        Wire.Value(ChordAngleObservedRad),
        Wire.Value(ChordAnglePredictedRad),
        TimeSpan.FromSeconds(LockedExposureSeconds),
        StarsMatchedFrame1,
        StarsMatchedFrame2,
        Wire.Value(CommandedRotationRad),
        Wire.Value(MeasuredRotationRad));
}

/// <summary><see cref="LiveSolveResult"/> on the wire, one refinement tick.</summary>
public sealed class PolarLiveSolveDto
{
    public int StarsMatched { get; init; }
    public double ExposureSeconds { get; init; }
    public string? FitsPath { get; init; }
    public double? AzErrorRad { get; init; }
    public double? AltErrorRad { get; init; }
    public double? SmoothedAzErrorRad { get; init; }
    public double? SmoothedAltErrorRad { get; init; }
    public bool IsSettled { get; init; }
    public bool IsAligned { get; init; }
    public int ConsecutiveFailedSolves { get; init; }
    public double? AxisX { get; init; }
    public double? AxisY { get; init; }
    public double? AxisZ { get; init; }
    public PolarOverlayDto? Overlay { get; init; }
    public WcsDto? Wcs { get; init; }

    public static PolarLiveSolveDto From(in LiveSolveResult result) => new PolarLiveSolveDto
    {
        StarsMatched = result.StarsMatched,
        ExposureSeconds = result.ExposureUsed.TotalSeconds,
        FitsPath = result.FitsPath,
        AzErrorRad = Wire.Finite(result.AzErrorRad),
        AltErrorRad = Wire.Finite(result.AltErrorRad),
        SmoothedAzErrorRad = Wire.Finite(result.SmoothedAzErrorRad),
        SmoothedAltErrorRad = Wire.Finite(result.SmoothedAltErrorRad),
        IsSettled = result.IsSettled,
        IsAligned = result.IsAligned,
        ConsecutiveFailedSolves = result.ConsecutiveFailedSolves,
        AxisX = Wire.Finite(result.AxisJ2000.X),
        AxisY = Wire.Finite(result.AxisJ2000.Y),
        AxisZ = Wire.Finite(result.AxisJ2000.Z),
        Overlay = result.Overlay is { } overlay ? PolarOverlayDto.From(overlay) : null,
        Wcs = result.Wcs is { } wcs ? WcsDto.From(wcs) : null,
    };

    public LiveSolveResult ToResult() => new LiveSolveResult(
        StarsMatched,
        TimeSpan.FromSeconds(ExposureSeconds),
        FitsPath,
        Wire.Value(AzErrorRad),
        Wire.Value(AltErrorRad),
        Wire.Value(SmoothedAzErrorRad),
        Wire.Value(SmoothedAltErrorRad),
        IsSettled,
        IsAligned,
        ConsecutiveFailedSolves,
        new Vec3(Wire.Value(AxisX), Wire.Value(AxisY), Wire.Value(AxisZ)),
        Overlay?.ToOverlay(),
        Wcs?.ToWcs());
}

/// <summary><see cref="PolarOverlay"/> on the wire: what the pole-centric reticle is drawn from, in J2000.</summary>
public sealed class PolarOverlayDto
{
    public double? TruePoleRaHours { get; init; }
    public double? TruePoleDecDeg { get; init; }
    public double? RefractedPoleRaHours { get; init; }
    public double? RefractedPoleDecDeg { get; init; }
    public double? AxisRaHours { get; init; }
    public double? AxisDecDeg { get; init; }
    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    public float[] RingRadiiArcmin { get; set; } = [];
    public double? AzErrorArcmin { get; init; }
    public double? AltErrorArcmin { get; init; }
    public Hemisphere Hemisphere { get; init; }

    /// <summary>The "follow this arrow" hint; null when the correction is too small to draw.</summary>
    public PolarArrowDto? CorrectionArrow { get; init; }

    /// <summary>Local sidereal time at the frame, in hours; null when the renderer is to skip the meridian.</summary>
    public double? LSTHours { get; init; }

    public static PolarOverlayDto From(in PolarOverlay overlay) => new PolarOverlayDto
    {
        TruePoleRaHours = Wire.Finite(overlay.TruePoleRaHours),
        TruePoleDecDeg = Wire.Finite(overlay.TruePoleDecDeg),
        RefractedPoleRaHours = Wire.Finite(overlay.RefractedPoleRaHours),
        RefractedPoleDecDeg = Wire.Finite(overlay.RefractedPoleDecDeg),
        AxisRaHours = Wire.Finite(overlay.AxisRaHours),
        AxisDecDeg = Wire.Finite(overlay.AxisDecDeg),
        RingRadiiArcmin = overlay.RingRadiiArcmin.IsDefault ? [] : [.. overlay.RingRadiiArcmin],
        AzErrorArcmin = Wire.Finite(overlay.AzErrorArcmin),
        AltErrorArcmin = Wire.Finite(overlay.AltErrorArcmin),
        Hemisphere = overlay.Hemisphere,
        CorrectionArrow = overlay.CorrectionArrow is { } arrow ? PolarArrowDto.From(arrow) : null,
        LSTHours = Wire.Finite(overlay.LSTHours),
    };

    public PolarOverlay ToOverlay() => new PolarOverlay(
        Wire.Value(TruePoleRaHours),
        Wire.Value(TruePoleDecDeg),
        Wire.Value(RefractedPoleRaHours),
        Wire.Value(RefractedPoleDecDeg),
        Wire.Value(AxisRaHours),
        Wire.Value(AxisDecDeg),
        [.. RingRadiiArcmin],
        Wire.Value(AzErrorArcmin),
        Wire.Value(AltErrorArcmin),
        Hemisphere,
        CorrectionArrow?.ToArrow(),
        Wire.Value(LSTHours));
}

/// <summary><see cref="PolarCorrectionArrow"/> on the wire.</summary>
public sealed class PolarArrowDto
{
    public double? StartRaHours { get; init; }
    public double? StartDecDeg { get; init; }
    public double? EndRaHours { get; init; }
    public double? EndDecDeg { get; init; }

    public static PolarArrowDto From(in PolarCorrectionArrow arrow) => new PolarArrowDto
    {
        StartRaHours = Wire.Finite(arrow.StartRaHours),
        StartDecDeg = Wire.Finite(arrow.StartDecDeg),
        EndRaHours = Wire.Finite(arrow.EndRaHours),
        EndDecDeg = Wire.Finite(arrow.EndDecDeg),
    };

    public PolarCorrectionArrow ToArrow() => new PolarCorrectionArrow(
        Wire.Value(StartRaHours), Wire.Value(StartDecDeg), Wire.Value(EndRaHours), Wire.Value(EndDecDeg));
}

/// <summary>A double on the wire: a value it does not have (NaN, an infinity) crosses as null, and comes back as NaN.</summary>
file static class Wire
{
    public static double? Finite(double value) => double.IsFinite(value) ? value : null;

    public static double Value(double? value) => value ?? double.NaN;
}
