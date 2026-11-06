using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>Which gate refused a guider calibration.</summary>
internal enum GuiderCalibrationFailureCause
{
    /// <summary>A star was chosen to calibrate on and none is at the chosen position.</summary>
    NoStarAtChosenPosition,

    /// <summary>No star was chosen and none could be found to start from (cloud, poor seeing or a field with no star the quality gates accept).</summary>
    NoStarFound,

    /// <summary>The star was lost while a sweep ran, or when it was looked for after one.</summary>
    StarLost,

    /// <summary>The star moved less than 1 px along the RA or the Dec sweep: the pulses did not move the mount, or the rate is too low.</summary>
    TooLittleDisplacement,

    /// <summary>The RA and Dec sweeps are not roughly perpendicular on the sensor.</summary>
    AxesNotOrthogonal,

    /// <summary>The star wandered during a sweep instead of moving steadily.</summary>
    UnstableSweep,
}

/// <summary>
/// The cumulative star displacement from the sweep's start (pixels) after each pulse of one sweep.
/// </summary>
/// <param name="Phase">Which sweep, for example "RA backlash clearing" or "Dec measurement".</param>
/// <param name="Direction">The pulse direction.</param>
/// <param name="DisplacementsPx">Displacement after each pulse, in order; a sweep that lost its star ends at the last frame that had one.</param>
internal readonly record struct CalibrationSweepTrace(string Phase, GuideDirection Direction, ImmutableArray<double> DisplacementsPx);

/// <summary>
/// Why a guider calibration failed (#2050): the star it started on, the gate that refused it and what every sweep
/// measured, so a flaky run names its cause instead of ending as a bare null.
/// </summary>
/// <param name="Cause">The gate that refused.</param>
/// <param name="Phase">Where it refused, for example "the RA (West) measurement sweep".</param>
/// <param name="Reason">The refusal in words, with the numbers the gate judged.</param>
/// <param name="StarX">The start star's X; NaN when there was none.</param>
/// <param name="StarY">The start star's Y; NaN when there was none.</param>
/// <param name="StarSnr">The start star's SNR; NaN when there was none.</param>
/// <param name="StarFlux">The start star's flux; NaN when there was none.</param>
/// <param name="Sweeps">Every sweep run before the refusal, backlash clearing included.</param>
internal sealed record GuiderCalibrationFailure(
    GuiderCalibrationFailureCause Cause,
    string Phase,
    string Reason,
    double StarX,
    double StarY,
    double StarSnr,
    double StarFlux,
    ImmutableArray<CalibrationSweepTrace> Sweeps)
{
    /// <summary>The start star, in words.</summary>
    public string StarDescription => double.IsNaN(StarX) || double.IsNaN(StarY)
        ? "no start star"
        : string.Create(CultureInfo.InvariantCulture, $"start star at ({StarX:F1}, {StarY:F1}), SNR {StarSnr:F1}, flux {StarFlux:F0}");

    /// <summary>A short plain-language form for an error a person reads: the reason and the star.</summary>
    public string Summary => $"{Reason} ({StarDescription})";

    /// <summary>The whole failure for the log: the summary and the displacement after every pulse of every sweep.</summary>
    public string Describe()
    {
        var text = new StringBuilder(Summary);
        text.Append(CultureInfo.InvariantCulture, $"; cause {Cause}");
        foreach (var sweep in Sweeps.IsDefault ? ImmutableArray<CalibrationSweepTrace>.Empty : Sweeps)
        {
            text.Append(CultureInfo.InvariantCulture, $"; {sweep.Phase} ({sweep.Direction}) px after each pulse:");
            if (sweep.DisplacementsPx.IsDefaultOrEmpty)
            {
                text.Append(" none");
                continue;
            }
            foreach (var px in sweep.DisplacementsPx)
            {
                text.Append(CultureInfo.InvariantCulture, $" {px:F2}");
            }
        }
        return text.ToString();
    }
}

/// <summary>
/// What <see cref="GuiderCalibration.CalibrateAsync"/> answers: the calibration, or the reason there is none.
/// </summary>
internal readonly record struct GuiderCalibrationOutcome(GuiderCalibrationResult? Result, GuiderCalibrationFailure? Failure)
{
    /// <summary>A calibration that passed every gate.</summary>
    public static GuiderCalibrationOutcome Success(GuiderCalibrationResult result) => new(result, null);

    /// <summary>A calibration a gate refused.</summary>
    public static GuiderCalibrationOutcome Failed(GuiderCalibrationFailure failure) => new(null, failure);
}
