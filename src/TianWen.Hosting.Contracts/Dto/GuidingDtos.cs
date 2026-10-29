using TianWen.Lib.Devices.Guider;

namespace TianWen.Hosting.Dto;

/// <summary>What a guiding run does (<c>POST /api/v1/guider/calibrate</c> and <c>/guide</c>, #1915). Numeric on the wire.</summary>
public enum GuideRunMode
{
    /// <summary>Calibrates and then ends, back to looping: the result stays on the node.</summary>
    Calibrate = 0,

    /// <summary>Calibrates when the guider holds no calibration, then guides until stopped.</summary>
    Guide = 1,
}

/// <summary>Where a guiding run is, in the guider's own vocabulary. Numeric on the wire.</summary>
public enum GuidingPhase
{
    /// <summary>No guiding run is going on: the loop, or nothing.</summary>
    Idle = 0,

    /// <summary>Started; the guider has not reported yet.</summary>
    Starting = 1,

    Calibrating = 2,

    /// <summary>Guiding, not yet settled.</summary>
    Settling = 3,

    Guiding = 4,

    /// <summary>Guiding with no star lock: a cloud, or a star lost off the frame.</summary>
    LostLock = 5,
}

/// <summary>
/// A guiding run asked for: <c>POST /api/v1/guider/calibrate</c> or <c>/guide</c> (#1915). The star and the exposure are the
/// ones the Guider tab's loop showed; a run started over the node's guide-camera loop takes whatever is left null from it.
/// </summary>
public sealed class GuidingRequestDto
{
    /// <summary>The chosen star's position in guide-frame pixels (0-based); both or neither. Null lets the guider choose.</summary>
    public double? StarX { get; init; }

    public double? StarY { get; init; }

    /// <summary>Exposure per guide frame, milliseconds; null takes the loop's, else the guider's own.</summary>
    public double? ExposureMs { get; init; }

    /// <summary>Gain, or null for the loop's, else the camera's.</summary>
    public short? Gain { get; init; }

    // set, not init: the JSON source generator gives an init-only property its TYPE'S default when the field is
    // absent, dropping the initializer below (CLAUDE.md, Hosting API: the wire traps).
    /// <summary>Guiding is settled once the error holds under this many pixels (guide only).</summary>
    public double SettlePixels { get; set; } = 1.5;

    /// <summary>...for this many seconds (guide only).</summary>
    public double SettleTimeSeconds { get; set; } = 10;

    /// <summary>...or the settle is given up on after this many (guide only).</summary>
    public double SettleTimeoutSeconds { get; set; } = 60;
}

/// <summary>A guider's calibration on the wire (<see cref="GuiderCalibrationSummary"/>), every figure null when not known.</summary>
public sealed class GuiderCalibrationDto
{
    /// <summary>The direction an RA pulse moves the star on the frame, degrees from +X towards +Y.</summary>
    public double? RaAngleDeg { get; init; }

    public double? DecAngleDeg { get; init; }

    public double? RaRateArcsecPerSec { get; init; }

    public double? DecRateArcsecPerSec { get; init; }

    public double? RaRatePixPerSec { get; init; }

    public double? DecRatePixPerSec { get; init; }

    /// <summary>How far the axes are from a right angle on the frame, degrees.</summary>
    public double? OrthogonalityErrorDeg { get; init; }

    public double? PixelScaleArcsec { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>The L the star walked, drawn over the guide frame; null when the guider recorded none.</summary>
    public CalibrationOverlayDto? Overlay { get; init; }

    public static GuiderCalibrationDto From(GuiderCalibrationSummary summary) => new()
    {
        RaAngleDeg = JsonNumber.OrNull(summary.RaAngleDeg),
        DecAngleDeg = JsonNumber.OrNull(summary.DecAngleDeg),
        RaRateArcsecPerSec = JsonNumber.OrNull(summary.RaRateArcsecPerSec),
        DecRateArcsecPerSec = JsonNumber.OrNull(summary.DecRateArcsecPerSec),
        RaRatePixPerSec = JsonNumber.OrNull(summary.RaRatePixPerSec),
        DecRatePixPerSec = JsonNumber.OrNull(summary.DecRatePixPerSec),
        OrthogonalityErrorDeg = JsonNumber.OrNull(summary.OrthogonalityErrorDeg),
        PixelScaleArcsec = JsonNumber.OrNull(summary.PixelScaleArcsec),
        DurationSeconds = JsonNumber.OrNull(summary.DurationSeconds),
        Overlay = summary.Overlay is { } overlay ? CalibrationOverlayDto.From(overlay) : null,
    };
}

/// <summary>
/// The node's guiding run, off the Guider tab's loop (<c>GET /api/v1/guider/run</c>, #1915): going on or not, where it is,
/// the calibration the node's guider holds, and, while a run goes on, the guider's own state as a session's is read.
/// </summary>
public sealed class GuidingStateDto
{
    /// <summary>Whether a guiding run is going on now.</summary>
    public bool Running { get; init; }

    public GuideRunMode Mode { get; init; }

    public GuidingPhase Phase { get; init; }

    /// <summary>The guide camera's name; empty before a run has been asked for.</summary>
    public string Camera { get; init; } = "";

    /// <summary>The star the run was started on, in guide-frame pixels; null when the guider chose.</summary>
    public double? StarX { get; init; }

    public double? StarY { get; init; }

    /// <summary>Why the last run failed, in words; null while it runs well, and when it ended on a stop or its calibration.</summary>
    public string? FailureReason { get; init; }

    /// <summary>The calibration the node's guider holds, from a calibration run or a guide run's own; null before one.</summary>
    public GuiderCalibrationDto? Calibration { get; init; }

    /// <summary>The guider as a session reports it (state, RMS, the recent steps, the star, its profile, the overlay); null while no run goes on.</summary>
    public GuiderStateDto? Guider { get; init; }
}
