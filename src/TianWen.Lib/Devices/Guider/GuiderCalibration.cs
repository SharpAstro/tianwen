using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Performs guider calibration by sending test pulses to the mount and measuring
/// the resulting star displacement. Determines camera angle and effective guide rates.
/// </summary>
internal sealed class GuiderCalibration
{
    private const int DefaultCalibrationPulseMs = 2000;
    private const int DefaultCalibrationSteps = 5;

    /// <summary>
    /// Duration of each calibration pulse.
    /// </summary>
    public TimeSpan CalibrationPulseDuration { get; set; } = TimeSpan.FromMilliseconds(DefaultCalibrationPulseMs);

    /// <summary>
    /// Number of calibration steps per direction.
    /// </summary>
    public int CalibrationSteps { get; set; } = DefaultCalibrationSteps;

    /// <summary>
    /// Runs calibration by pulsing in each direction and measuring displacement.
    /// </summary>
    /// <param name="mount">Mount to pulse guide.</param>
    /// <param name="tracker">Centroid tracker with an acquired star.</param>
    /// <param name="captureFrame">Function that captures a guide frame and returns the image data.</param>
    /// <param name="external">External services for time management.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Calibration result, or null if calibration failed.</returns>
    public async ValueTask<GuiderCalibrationResult?> CalibrateAsync(
        IMountDriver mount,
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
        var westResult = await MeasureDisplacementAsync(mount, tracker, captureFrame, external,
            GuideDirection.West, CalibrationSteps, CalibrationPulseDuration, cancellationToken);

        if (westResult is null)
        {
            return null;
        }

        // Return to start by pulsing East
        for (var i = 0; i < CalibrationSteps; i++)
        {
            await mount.PulseGuideAsync(GuideDirection.East, CalibrationPulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(mount, external, CalibrationPulseDuration, cancellationToken);
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
        var northResult = await MeasureDisplacementAsync(mount, tracker, captureFrame, external,
            GuideDirection.North, CalibrationSteps, CalibrationPulseDuration, cancellationToken);

        if (northResult is null)
        {
            return null;
        }

        // Return to start by pulsing South
        for (var i = 0; i < CalibrationSteps; i++)
        {
            await mount.PulseGuideAsync(GuideDirection.South, CalibrationPulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(mount, external, CalibrationPulseDuration, cancellationToken);
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
            TotalCalibrationTimeSec: totalRaTimeSec + totalDecTimeSec);
    }

    private static async ValueTask<GuiderCentroidResult?> MeasureDisplacementAsync(
        IMountDriver mount,
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
            await mount.PulseGuideAsync(direction, pulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(mount, external, pulseDuration, cancellationToken);

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
        IMountDriver mount, IExternal external, TimeSpan pulseDuration, CancellationToken cancellationToken)
    {
        // Wait most of the pulse duration upfront, then poll at small intervals
        var bulkWait = pulseDuration * 0.9;
        if (bulkWait > TimeSpan.Zero)
        {
            await external.SleepAsync(bulkWait, cancellationToken);
        }

        var pollInterval = TimeSpan.FromMilliseconds(50);
        var maxPolls = (int)(pulseDuration.TotalMilliseconds / pollInterval.TotalMilliseconds) + 20;
        while (await mount.IsPulseGuidingAsync(cancellationToken) && --maxPolls > 0)
        {
            await external.SleepAsync(pollInterval, cancellationToken);
        }
    }
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
internal readonly record struct GuiderCalibrationResult(
    double CameraAngleRad,
    double RaRatePixPerSec,
    double DecRatePixPerSec,
    double RaDisplacementPx,
    double DecDisplacementPx,
    double TotalCalibrationTimeSec)
{
    /// <summary>
    /// Camera angle in degrees.
    /// </summary>
    public double CameraAngleDeg => CameraAngleRad * 180.0 / Math.PI;

    /// <summary>
    /// Transforms a pixel-space error (dX, dY) into RA/Dec corrections
    /// using the calibrated camera angle.
    /// </summary>
    /// <param name="deltaX">Error in X pixels.</param>
    /// <param name="deltaY">Error in Y pixels.</param>
    /// <returns>Error decomposed into (raPixels, decPixels) along the mount axes.</returns>
    public (double RaPixels, double DecPixels) TransformToMountAxes(double deltaX, double deltaY)
    {
        var cos = Math.Cos(CameraAngleRad);
        var sin = Math.Sin(CameraAngleRad);

        // Rotate from camera frame to mount frame
        var raPixels = deltaX * cos + deltaY * sin;
        var decPixels = -deltaX * sin + deltaY * cos;

        return (raPixels, decPixels);
    }
}
