using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Result of validating a saved calibration against current conditions.
/// </summary>
internal enum CalibrationValidationResult
{
    /// <summary>Saved calibration matches current conditions. Use it.</summary>
    Valid,

    /// <summary>Guide rates drifted but camera angle is OK. Recalibrate, keep model weights.</summary>
    RateDrifted,

    /// <summary>Camera angle changed significantly. Recalibrate and discard saved model weights.</summary>
    AngleChanged
}

/// <summary>
/// Performs guider calibration by sending test pulses to the mount and measuring
/// the resulting star displacement. Determines camera angle and effective guide rates.
/// </summary>
internal sealed class GuiderCalibration
{
    private const int DefaultCalibrationPulseMs = 2000;
    private const int DefaultCalibrationSteps = 5;

    /// <summary>
    /// Maximum angle deviation (degrees) for a saved calibration to be considered valid.
    /// </summary>
    public double AngleToleranceDeg { get; set; } = 5.0;

    /// <summary>
    /// Maximum fractional rate deviation for a saved calibration to be considered valid.
    /// 0.20 = 20%.
    /// </summary>
    public double RateToleranceFraction { get; set; } = 0.20;

    /// <summary>
    /// Duration of each calibration pulse.
    /// </summary>
    public TimeSpan CalibrationPulseDuration { get; set; } = TimeSpan.FromMilliseconds(DefaultCalibrationPulseMs);

    /// <summary>
    /// Number of calibration steps per direction.
    /// </summary>
    public int CalibrationSteps { get; set; } = DefaultCalibrationSteps;

    /// <summary>
    /// Whether adaptive backlash clearing is enabled. When true, backlash clearing
    /// pulses are sent until star movement is detected or <see cref="MaxBacklashClearingSteps"/> is reached.
    /// </summary>
    public bool BacklashClearingEnabled { get; set; }

    /// <summary>
    /// Maximum number of backlash clearing pulses before giving up.
    /// </summary>
    public int MaxBacklashClearingSteps { get; set; } = 10;

    /// <summary>
    /// Cumulative star displacement (pixels) that indicates backlash has been cleared.
    /// </summary>
    public double BacklashMovementThresholdPx { get; set; } = 1.5;

    /// <summary>
    /// Runs calibration by pulsing in each direction and measuring displacement.
    /// </summary>
    /// <param name="pulseTarget">Pulse guide target (camera ST-4, mount, or router).</param>
    /// <param name="tracker">Centroid tracker with an acquired star.</param>
    /// <param name="captureFrame">Function that captures a guide frame and returns the image data.</param>
    /// <param name="external">External services for time management.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Calibration result, or null if calibration failed.</returns>
    public async ValueTask<GuiderCalibrationResult?> CalibrateAsync(
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        Func<CancellationToken, ValueTask<float[,]>> captureFrame,
        IExternal external,
        CancellationToken cancellationToken)
    {
        if (!tracker.IsAcquired)
        {
            return null;
        }

        // Record starting lock position
        tracker.SetLockPosition();

        // --- RA calibration (West then East to return) ---
        var raBacklashResult = BacklashClearingResult.None;
        if (BacklashClearingEnabled)
        {
            // Clear backlash by pulsing West before measuring
            raBacklashResult = await ClearBacklashAsync(pulseTarget, tracker, captureFrame, external, GuideDirection.West, cancellationToken);

            // Re-acquire after backlash clearing and reset lock position
            tracker.Reset();
            var blFrame = await captureFrame(cancellationToken);
            if (tracker.ProcessFrame(blFrame) is null)
            {
                return null;
            }
            tracker.SetLockPosition();
        }

        var westResult = await MeasureDisplacementAsync(pulseTarget, tracker, captureFrame, external,
            GuideDirection.West, CalibrationSteps, CalibrationPulseDuration, cancellationToken);

        if (westResult is null)
        {
            return null;
        }

        // Return to start by pulsing East
        for (var i = 0; i < CalibrationSteps; i++)
        {
            await pulseTarget.PulseGuideAsync(GuideDirection.East, CalibrationPulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(pulseTarget, external, CalibrationPulseDuration, cancellationToken);
        }

        // Re-acquire after return — star position jumped back, may exceed search radius
        tracker.Reset();
        var frame = await captureFrame(cancellationToken);
        if (tracker.ProcessFrame(frame) is null)
        {
            return null;
        }
        tracker.SetLockPosition();

        // --- Dec calibration (North then South to return) ---
        var decBacklashResult = BacklashClearingResult.None;
        if (BacklashClearingEnabled)
        {
            // Clear backlash by pulsing North before measuring
            decBacklashResult = await ClearBacklashAsync(pulseTarget, tracker, captureFrame, external, GuideDirection.North, cancellationToken);

            // Re-acquire after backlash clearing and reset lock position
            tracker.Reset();
            var blFrame2 = await captureFrame(cancellationToken);
            if (tracker.ProcessFrame(blFrame2) is null)
            {
                return null;
            }
            tracker.SetLockPosition();
        }

        var northResult = await MeasureDisplacementAsync(pulseTarget, tracker, captureFrame, external,
            GuideDirection.North, CalibrationSteps, CalibrationPulseDuration, cancellationToken);

        if (northResult is null)
        {
            return null;
        }

        // Return to start by pulsing South
        for (var i = 0; i < CalibrationSteps; i++)
        {
            await pulseTarget.PulseGuideAsync(GuideDirection.South, CalibrationPulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(pulseTarget, external, CalibrationPulseDuration, cancellationToken);
        }

        // Compute calibration from West displacement (RA axis) and North displacement (Dec axis)
        var totalRaTimeSec = CalibrationSteps * CalibrationPulseDuration.TotalSeconds;
        var totalDecTimeSec = totalRaTimeSec;

        var raDx = westResult.Value.DeltaX;
        var raDy = westResult.Value.DeltaY;
        var decDx = northResult.Value.DeltaX;
        var decDy = northResult.Value.DeltaY;

        var raDisplacementPx = Math.Sqrt(raDx * raDx + raDy * raDy);
        var decDisplacementPx = Math.Sqrt(decDx * decDx + decDy * decDy);

        if (raDisplacementPx < 1.0 || decDisplacementPx < 1.0)
        {
            // Not enough displacement for reliable calibration
            return null;
        }

        // Camera angle: angle of the RA axis on the sensor
        // West pulse should move stars along the RA direction on the sensor
        var cameraAngleRad = Math.Atan2(raDy, raDx);

        // Guide rates in pixels per second
        var raRatePixPerSec = raDisplacementPx / totalRaTimeSec;
        var decRatePixPerSec = decDisplacementPx / totalDecTimeSec;

        return new GuiderCalibrationResult(
            CameraAngleRad: cameraAngleRad,
            RaRatePixPerSec: raRatePixPerSec,
            DecRatePixPerSec: decRatePixPerSec,
            RaDisplacementPx: raDisplacementPx,
            DecDisplacementPx: decDisplacementPx,
            TotalCalibrationTimeSec: totalRaTimeSec + totalDecTimeSec,
            BacklashClearingStepsRa: raBacklashResult.StepsUsed,
            BacklashClearingStepsDec: decBacklashResult.StepsUsed);
    }

    /// <summary>
    /// Validates a saved calibration by sending a single West pulse and comparing
    /// the measured displacement against the saved rates and camera angle.
    /// Much faster than full calibration (~2s vs ~12s).
    /// </summary>
    /// <param name="savedCalibration">Previously saved calibration to validate.</param>
    /// <param name="pulseTarget">Pulse guide target (camera ST-4, mount, or router).</param>
    /// <param name="tracker">Centroid tracker with an acquired star.</param>
    /// <param name="captureFrame">Function that captures a guide frame.</param>
    /// <param name="external">External services for time management.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result indicating whether the saved calibration can be reused.</returns>
    public async ValueTask<CalibrationValidationResult> ValidateAsync(
        GuiderCalibrationResult savedCalibration,
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        Func<CancellationToken, ValueTask<float[,]>> captureFrame,
        IExternal external,
        CancellationToken cancellationToken)
    {
        if (!tracker.IsAcquired)
        {
            return CalibrationValidationResult.AngleChanged;
        }

        tracker.SetLockPosition();

        // Send a single West pulse using the same pulse duration as the saved calibration
        var pulseDuration = CalibrationPulseDuration;
        await pulseTarget.PulseGuideAsync(GuideDirection.West, pulseDuration, cancellationToken);
        await WaitForPulseCompleteAsync(pulseTarget, external, pulseDuration, cancellationToken);

        var frame = await captureFrame(cancellationToken);
        var result = tracker.ProcessFrame(frame);

        // Return the star to its original position
        await pulseTarget.PulseGuideAsync(GuideDirection.East, pulseDuration, cancellationToken);
        await WaitForPulseCompleteAsync(pulseTarget, external, pulseDuration, cancellationToken);

        // Reset tracker for next use
        tracker.Reset();
        var returnFrame = await captureFrame(cancellationToken);
        tracker.ProcessFrame(returnFrame);

        if (result is null)
        {
            return CalibrationValidationResult.AngleChanged;
        }

        var dx = result.Value.DeltaX;
        var dy = result.Value.DeltaY;
        var displacement = Math.Sqrt(dx * dx + dy * dy);

        if (displacement < 1.0)
        {
            // Not enough displacement to validate
            return CalibrationValidationResult.RateDrifted;
        }

        // Compare camera angle
        var measuredAngleRad = Math.Atan2(dy, dx);
        var angleDiffDeg = Math.Abs(measuredAngleRad - savedCalibration.CameraAngleRad) * 180.0 / Math.PI;
        // Normalize to [0, 180]
        if (angleDiffDeg > 180.0)
        {
            angleDiffDeg = 360.0 - angleDiffDeg;
        }

        if (angleDiffDeg > AngleToleranceDeg)
        {
            return CalibrationValidationResult.AngleChanged;
        }

        // Compare RA guide rate
        var measuredRate = displacement / pulseDuration.TotalSeconds;
        var rateDeviation = Math.Abs(measuredRate - savedCalibration.RaRatePixPerSec) / savedCalibration.RaRatePixPerSec;

        if (rateDeviation > RateToleranceFraction)
        {
            return CalibrationValidationResult.RateDrifted;
        }

        return CalibrationValidationResult.Valid;
    }

    internal async ValueTask<BacklashClearingResult> ClearBacklashAsync(
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        Func<CancellationToken, ValueTask<float[,]>> captureFrame,
        IExternal external,
        GuideDirection direction,
        CancellationToken cancellationToken)
    {
        // Record pre-clearing star position — displacement accumulates from here
        tracker.SetLockPosition();

        for (var i = 0; i < MaxBacklashClearingSteps; i++)
        {
            await pulseTarget.PulseGuideAsync(direction, CalibrationPulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(pulseTarget, external, CalibrationPulseDuration, cancellationToken);

            var frame = await captureFrame(cancellationToken);
            var result = tracker.ProcessFrame(frame);

            if (result is null)
            {
                // Star lost during backlash clearing
                return new BacklashClearingResult(i + 1, MovementDetected: false);
            }

            var disp = Math.Sqrt(result.Value.DeltaX * result.Value.DeltaX + result.Value.DeltaY * result.Value.DeltaY);
            if (disp >= BacklashMovementThresholdPx)
            {
                return new BacklashClearingResult(i + 1, MovementDetected: true);
            }
        }

        return new BacklashClearingResult(MaxBacklashClearingSteps, MovementDetected: false);
    }

    private static async ValueTask<GuiderCentroidResult?> MeasureDisplacementAsync(
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        Func<CancellationToken, ValueTask<float[,]>> captureFrame,
        IExternal external,
        GuideDirection direction,
        int steps,
        TimeSpan pulseDuration,
        CancellationToken cancellationToken)
    {
        GuiderCentroidResult? lastResult = null;

        for (var i = 0; i < steps; i++)
        {
            await pulseTarget.PulseGuideAsync(direction, pulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(pulseTarget, external, pulseDuration, cancellationToken);

            var frame = await captureFrame(cancellationToken);
            lastResult = tracker.ProcessFrame(frame);

            if (lastResult is null)
            {
                return null; // Lost star during calibration
            }
        }

        return lastResult;
    }

    private static async ValueTask WaitForPulseCompleteAsync(
        IPulseGuideTarget pulseTarget, IExternal external, TimeSpan pulseDuration, CancellationToken cancellationToken)
    {
        // Wait most of the pulse duration upfront, then poll at small intervals
        var bulkWait = pulseDuration * 0.9;
        if (bulkWait > TimeSpan.Zero)
        {
            await external.SleepAsync(bulkWait, cancellationToken);
        }

        var pollInterval = TimeSpan.FromMilliseconds(50);
        var maxPolls = (int)(pulseDuration.TotalMilliseconds / pollInterval.TotalMilliseconds) + 20;
        while (await pulseTarget.IsPulseGuidingAsync(cancellationToken) && --maxPolls > 0)
        {
            await external.SleepAsync(pollInterval, cancellationToken);
        }
    }
}

/// <summary>
/// Result of adaptive backlash clearing.
/// </summary>
/// <param name="StepsUsed">Number of pulses sent.</param>
/// <param name="MovementDetected">Whether star displacement exceeded the threshold.</param>
internal readonly record struct BacklashClearingResult(int StepsUsed, bool MovementDetected)
{
    /// <summary>
    /// Default result when backlash clearing is not enabled.
    /// </summary>
    public static BacklashClearingResult None => new BacklashClearingResult(0, MovementDetected: false);
}

/// <summary>
/// Result of guider calibration.
/// </summary>
/// <param name="CameraAngleRad">Angle of the RA axis on the sensor in radians.</param>
/// <param name="RaRatePixPerSec">RA guide rate in pixels per second.</param>
/// <param name="DecRatePixPerSec">Dec guide rate in pixels per second.</param>
/// <param name="RaDisplacementPx">Total RA displacement measured during calibration (pixels).</param>
/// <param name="DecDisplacementPx">Total Dec displacement measured during calibration (pixels).</param>
/// <param name="TotalCalibrationTimeSec">Total calibration time in seconds.</param>
/// <param name="BacklashClearingStepsRa">Number of backlash clearing steps used for RA axis.</param>
/// <param name="BacklashClearingStepsDec">Number of backlash clearing steps used for Dec axis.</param>
internal readonly record struct GuiderCalibrationResult(
    double CameraAngleRad,
    double RaRatePixPerSec,
    double DecRatePixPerSec,
    double RaDisplacementPx,
    double DecDisplacementPx,
    double TotalCalibrationTimeSec,
    int BacklashClearingStepsRa = 0,
    int BacklashClearingStepsDec = 0)
{
    /// <summary>
    /// Camera angle in degrees.
    /// </summary>
    public readonly double CameraAngleDeg => CameraAngleRad * 180.0 / Math.PI;

    /// <summary>
    /// Transforms a pixel-space error (dX, dY) into RA/Dec corrections
    /// using the calibrated camera angle.
    /// </summary>
    /// <param name="deltaX">Error in X pixels.</param>
    /// <param name="deltaY">Error in Y pixels.</param>
    /// <returns>Error decomposed into (raPixels, decPixels) along the mount axes.</returns>
    public readonly (double RaPixels, double DecPixels) TransformToMountAxes(double deltaX, double deltaY)
    {
        var cos = Math.Cos(CameraAngleRad);
        var sin = Math.Sin(CameraAngleRad);

        // Rotate from camera frame to mount frame
        var raPixels = deltaX * cos + deltaY * sin;
        var decPixels = -deltaX * sin + deltaY * cos;

        return (raPixels, decPixels);
    }
}
