using System;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Classical proportional (P) guide controller. Computes guide pulse durations
/// from centroid errors using the calibrated camera angle and guide rates.
/// Serves as the baseline controller and training target for the neural guider.
/// </summary>
internal sealed class ProportionalGuideController
{
    private const double DefaultAggressivenessRa = 0.7;
    private const double DefaultAggressivenessDec = 0.7;
    private const double DefaultMinPulseMs = 50;
    private const double DefaultMaxPulseMs = 2000;

    /// <summary>
    /// RA aggressiveness (0.0 to 1.0). Higher values apply larger corrections.
    /// </summary>
    public double AggressivenessRa { get; set; } = DefaultAggressivenessRa;

    /// <summary>
    /// Dec aggressiveness (0.0 to 1.0).
    /// </summary>
    public double AggressivenessDec { get; set; } = DefaultAggressivenessDec;

    /// <summary>
    /// Minimum pulse duration in milliseconds. Pulses below this are suppressed (dead zone).
    /// </summary>
    public double MinPulseMs { get; set; } = DefaultMinPulseMs;

    /// <summary>
    /// Maximum pulse duration in milliseconds. Pulses are clamped to this value.
    /// </summary>
    public double MaxPulseMs { get; set; } = DefaultMaxPulseMs;

    /// <summary>
    /// If true, Dec corrections are suppressed (RA-only guiding).
    /// </summary>
    public bool DecGuideEnabled { get; set; } = true;

    /// <summary>
    /// Computes guide corrections from a centroid error measurement.
    /// </summary>
    /// <param name="calibration">Calibration result with camera angle and guide rates.</param>
    /// <param name="deltaX">Centroid error in X pixels.</param>
    /// <param name="deltaY">Centroid error in Y pixels.</param>
    /// <returns>Guide correction with RA and Dec pulse durations and directions.</returns>
    public GuideCorrection Compute(GuiderCalibrationResult calibration, double deltaX, double deltaY)
    {
        // Transform pixel-space error to mount axes
        var (raErrorPx, decErrorPx) = calibration.TransformToMountAxes(deltaX, deltaY);

        // Convert pixel error to time-domain correction via guide rates.
        // Negate: positive RA error means star moved in the West-pulse direction,
        // so we need an opposite (East) correction to bring it back.
        var raCorrectionSec = -raErrorPx / calibration.RaRatePixPerSec * AggressivenessRa;
        var decCorrectionSec = -decErrorPx / calibration.DecRatePixPerSec * AggressivenessDec;

        // Apply dead zone and clamp
        var raPulseMs = ApplyDeadZoneAndClamp(raCorrectionSec * 1000.0);
        var decPulseMs = DecGuideEnabled ? ApplyDeadZoneAndClamp(decCorrectionSec * 1000.0) : 0;

        return new GuideCorrection(raPulseMs, decPulseMs);
    }

    private double ApplyDeadZoneAndClamp(double pulseMs)
    {
        if (Math.Abs(pulseMs) < MinPulseMs)
        {
            return 0; // Dead zone — suppress tiny corrections
        }

        // Clamp magnitude
        if (pulseMs > MaxPulseMs)
        {
            return MaxPulseMs;
        }

        if (pulseMs < -MaxPulseMs)
        {
            return -MaxPulseMs;
        }

        return pulseMs;
    }
}

/// <summary>
/// Guide correction output: signed pulse durations in milliseconds.
/// Positive RA = West correction, negative RA = East correction.
/// Positive Dec = North correction, negative Dec = South correction.
/// </summary>
/// <param name="RaPulseMs">RA correction pulse duration in ms (positive = West, negative = East).</param>
/// <param name="DecPulseMs">Dec correction pulse duration in ms (positive = North, negative = South).</param>
internal readonly record struct GuideCorrection(double RaPulseMs, double DecPulseMs)
{
    /// <summary>
    /// Duration of the RA pulse.
    /// </summary>
    public readonly TimeSpan RaPulseDuration => TimeSpan.FromMilliseconds(Math.Abs(RaPulseMs));

    /// <summary>
    /// Duration of the Dec pulse.
    /// </summary>
    public readonly TimeSpan DecPulseDuration => TimeSpan.FromMilliseconds(Math.Abs(DecPulseMs));

    /// <summary>
    /// Whether an RA correction is needed.
    /// </summary>
    public readonly bool HasRaCorrection => RaPulseMs != 0;

    /// <summary>
    /// Whether a Dec correction is needed.
    /// </summary>
    public readonly bool HasDecCorrection => DecPulseMs != 0;
}
