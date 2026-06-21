using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;

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
/// <summary>
/// Current calibration step, exposed for UI progress display.
/// </summary>
internal readonly record struct CalibrationProgress(
    string Phase,
    GuideDirection Direction,
    int Step,
    int TotalSteps,
    double DisplacementPx);

internal sealed class GuiderCalibration
{
    /// <summary>
    /// Optional logger. Every rejection path in <see cref="CalibrateAsync"/> logs WHY it
    /// returned null (with the measured numbers) — a silent null forces whoever reads the
    /// session log to guess which of five quality gates fired.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Current calibration progress, updated during <see cref="CalibrateAsync"/>.
    /// </summary>
    public CalibrationProgress? Progress { get; set; }

    /// <summary>In-progress RA calibration steps (star positions after each pulse). Empty before RA calibration starts.</summary>
    public ImmutableArray<CalibrationStep> ActiveRaSteps { get; private set; } = [];

    /// <summary>In-progress Dec calibration steps. Empty before Dec calibration starts.</summary>
    public ImmutableArray<CalibrationStep> ActiveDecSteps { get; private set; } = [];

    /// <summary>RA origin (star position before first RA pulse).</summary>
    public CalibrationStep? ActiveRaOrigin { get; private set; }

    /// <summary>Dec origin (star position before first Dec pulse).</summary>
    public CalibrationStep? ActiveDecOrigin { get; private set; }
    private const int DefaultCalibrationPulseMs = 750;
    private const int DefaultCalibrationSteps = 12;

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
    /// Maximum deviation from perpendicular (degrees) allowed between the measured RA (West) and
    /// Dec (North) axes of a FRESH calibration before it is rejected as degenerate. RA and Dec are
    /// mechanically perpendicular, so on the sensor the two sweep directions must be ~90deg apart;
    /// a near-parallel result means a bad sweep (severe backlash, a star jump/swap, stiction, or a
    /// non-responding axis) that would produce garbage corrections. Generous (30deg) so real cone
    /// error / camera tilt passes while clearly-degenerate calibrations are rejected.
    /// </summary>
    public double FreshOrthogonalityToleranceDeg { get; set; } = 30.0;

    /// <summary>
    /// Minimum sweep linearity (net displacement / total path length) for a FRESH calibration. A
    /// clean monotonic sweep has a ratio near 1; a star that wandered back and forth during the
    /// sweep (seeing blowups, intermittent stiction, a hopping centroid) inflates the path length,
    /// signalling unstable rates -- reject below this. 0.6 tolerates normal seeing jitter.
    /// </summary>
    public double MinSweepLinearity { get; set; } = 0.6;

    /// <summary>
    /// When true, force the Dec axis exactly perpendicular to RA (Dec angle = RA angle +/- 90deg),
    /// taking only the SIGN (which side is North) from the measured North sweep. When false (the
    /// default, matching PHD2's "Assume Dec orthogonal to RA" being off), use the independently
    /// MEASURED Dec sweep angle, which also tolerates a non-perpendicular Dec axis (cone error,
    /// camera/OTA tilt). Either way the Dec SENSE comes from the measurement -- the orthogonal
    /// assumption only snaps the angle, it never invents the sign.
    /// </summary>
    public bool AssumeDecOrthogonal { get; set; }

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
    /// Expected one-direction star excursion (pixels) during calibration: the backlash
    /// clearing pulses plus the measurement sweep, all in the same direction, before the
    /// star is returned to start. Callers use this to acquire a guide star with enough
    /// edge clearance to survive the throw instead of re-locking onto a different star.
    /// </summary>
    /// <param name="guideRatePixPerSec">Effective guide rate in pixels per second.</param>
    public double ExpectedSweepThrowPixels(double guideRatePixPerSec)
        => (CalibrationSteps + (BacklashClearingEnabled ? MaxBacklashClearingSteps : 0))
           * CalibrationPulseDuration.TotalSeconds * Math.Max(0.0, guideRatePixPerSec);

    /// <summary>
    /// Runs calibration by pulsing in each direction and measuring displacement.
    /// </summary>
    /// <param name="pulseTarget">Pulse guide target (camera ST-4, mount, or router).</param>
    /// <param name="tracker">Centroid tracker with an acquired star.</param>
    /// <param name="captureFrame">Function that captures a guide frame and returns the image data.</param>
    /// <param name="timeProvider">Time provider for delays.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Calibration result, or null if calibration failed.</returns>
    public async ValueTask<GuiderCalibrationResult?> CalibrateAsync(
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        Func<CancellationToken, ValueTask<Image>> captureFrame,
        ITimeProvider timeProvider,
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
            raBacklashResult = await ClearBacklashAsync(pulseTarget, tracker, captureFrame, timeProvider, GuideDirection.West, cancellationToken);

            // Re-acquire after backlash clearing and reset lock position
            tracker.Reset();
            var blFrame = await captureFrame(cancellationToken);
            if (tracker.ProcessFrame(blFrame.GetChannelArray(0)) is null)
            {
                Logger?.LogWarning("Calibration rejected: star lost re-acquiring after RA (West) backlash clearing ({Steps} steps, movement detected: {Moved}).",
                    raBacklashResult.StepsUsed, raBacklashResult.MovementDetected);
                return null;
            }
            tracker.SetLockPosition();
        }

        // Capture the RA origin (lock position = star position before first West pulse)
        var raOrigin = tracker.Stars.Count > 0
            ? new CalibrationStep(tracker.Stars[0].LockX, tracker.Stars[0].LockY)
            : new CalibrationStep(0, 0);
        ActiveRaOrigin = raOrigin;
        ActiveRaSteps = [];

        var westMeasurement = await MeasureDisplacementAsync(pulseTarget, tracker, captureFrame, timeProvider,
            GuideDirection.West, CalibrationSteps, CalibrationPulseDuration, cancellationToken,
            onStep: step => ActiveRaSteps = ActiveRaSteps.Add(step));

        if (westMeasurement is null)
        {
            Logger?.LogWarning("Calibration rejected: star lost during the RA (West) measurement sweep.");
            return null;
        }

        var (westResult, raSteps) = westMeasurement.Value;

        // Return to start by pulsing East
        for (var i = 0; i < CalibrationSteps; i++)
        {
            await pulseTarget.PulseGuideAsync(GuideDirection.East, CalibrationPulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(pulseTarget, timeProvider, CalibrationPulseDuration, cancellationToken);
        }

        // Re-acquire after return — star position jumped back, may exceed search radius
        tracker.Reset();
        var frame = await captureFrame(cancellationToken);
        if (tracker.ProcessFrame(frame.GetChannelArray(0)) is null)
        {
            Logger?.LogWarning("Calibration rejected: star lost re-acquiring after the RA (East) return sweep.");
            return null;
        }
        tracker.SetLockPosition();

        // --- Dec calibration (North then South to return) ---
        var decBacklashResult = BacklashClearingResult.None;
        if (BacklashClearingEnabled)
        {
            // Clear backlash by pulsing North before measuring
            decBacklashResult = await ClearBacklashAsync(pulseTarget, tracker, captureFrame, timeProvider, GuideDirection.North, cancellationToken);

            // Re-acquire after backlash clearing and reset lock position
            tracker.Reset();
            var blFrame2 = await captureFrame(cancellationToken);
            if (tracker.ProcessFrame(blFrame2.GetChannelArray(0)) is null)
            {
                Logger?.LogWarning("Calibration rejected: star lost re-acquiring after Dec (North) backlash clearing ({Steps} steps, movement detected: {Moved}).",
                    decBacklashResult.StepsUsed, decBacklashResult.MovementDetected);
                return null;
            }
            tracker.SetLockPosition();
        }

        // Capture the Dec origin (lock position = star position before first North pulse)
        var decOrigin = tracker.Stars.Count > 0
            ? new CalibrationStep(tracker.Stars[0].LockX, tracker.Stars[0].LockY)
            : raOrigin;
        ActiveDecOrigin = decOrigin;
        ActiveDecSteps = [];

        var northMeasurement = await MeasureDisplacementAsync(pulseTarget, tracker, captureFrame, timeProvider,
            GuideDirection.North, CalibrationSteps, CalibrationPulseDuration, cancellationToken,
            onStep: step => ActiveDecSteps = ActiveDecSteps.Add(step));

        if (northMeasurement is null)
        {
            Logger?.LogWarning("Calibration rejected: star lost during the Dec (North) measurement sweep.");
            return null;
        }

        var (northResult, decSteps) = northMeasurement.Value;

        // Return to start by pulsing South
        for (var i = 0; i < CalibrationSteps; i++)
        {
            await pulseTarget.PulseGuideAsync(GuideDirection.South, CalibrationPulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(pulseTarget, timeProvider, CalibrationPulseDuration, cancellationToken);
        }

        // Compute calibration from West displacement (RA axis) and North displacement (Dec axis)
        var totalRaTimeSec = CalibrationSteps * CalibrationPulseDuration.TotalSeconds;
        var totalDecTimeSec = totalRaTimeSec;

        var raDx = westResult.DeltaX;
        var raDy = westResult.DeltaY;
        var decDx = northResult.DeltaX;
        var decDy = northResult.DeltaY;

        var raDisplacementPx = Math.Sqrt(raDx * raDx + raDy * raDy);
        var decDisplacementPx = Math.Sqrt(decDx * decDx + decDy * decDy);

        if (raDisplacementPx < 1.0 || decDisplacementPx < 1.0)
        {
            // Not enough displacement for reliable calibration
            Logger?.LogWarning("Calibration rejected: insufficient displacement (RA West {RaPx:F2}px, Dec North {DecPx:F2}px; need >= 1px each). Pulses are not moving the mount or the guide rate is too low.",
                raDisplacementPx, decDisplacementPx);
            return null;
        }

        // Reject a DEGENERATE calibration: RA and Dec are mechanically perpendicular, so the
        // West (RA) and North (Dec) sweep directions on the sensor must be ~90deg apart. A
        // near-parallel result means a bad sweep (severe backlash, a star jump/swap, stiction or a
        // non-responding axis) and would yield garbage corrections -- reject rather than guide on it.
        var raAxisAngleDeg = Math.Atan2(raDy, raDx) * 180.0 / Math.PI;
        var decAxisAngleDeg = Math.Atan2(decDy, decDx) * 180.0 / Math.PI;
        var axisSeparationDeg = FoldAngleDiffDeg(Math.Abs(decAxisAngleDeg - raAxisAngleDeg));
        if (Math.Abs(axisSeparationDeg - 90.0) > FreshOrthogonalityToleranceDeg)
        {
            // RA/Dec axes not perpendicular -> degenerate calibration.
            Logger?.LogWarning("Calibration rejected: RA/Dec sweep axes are {Separation:F1}deg apart (RA at {RaAngle:F1}deg, Dec at {DecAngle:F1}deg; need 90 +/- {Tolerance:F0}deg).",
                axisSeparationDeg, raAxisAngleDeg, decAxisAngleDeg, FreshOrthogonalityToleranceDeg);
            return null;
        }

        // Reject an UNSTABLE sweep: a clean calibration moves the star monotonically along each
        // axis, so the net displacement is close to the total path the star travelled. A star that
        // wandered (seeing blowups, intermittent stiction, a hopping centroid) inflates the path
        // without the net -- the per-step rate is unreliable, so reject.
        var raLinearity = SweepLinearity(raOrigin, raSteps, raDisplacementPx);
        var decLinearity = SweepLinearity(decOrigin, decSteps, decDisplacementPx);
        if (raLinearity < MinSweepLinearity || decLinearity < MinSweepLinearity)
        {
            Logger?.LogWarning("Calibration rejected: unstable sweep (linearity RA {RaLin:F2}, Dec {DecLin:F2}; need >= {Min:F2}). The star wandered instead of moving monotonically.",
                raLinearity, decLinearity, MinSweepLinearity);
            return null;
        }

        // Camera angle: angle of the RA axis on the sensor
        // West pulse should move stars along the RA direction on the sensor
        var cameraAngleRad = Math.Atan2(raDy, raDx);

        // Dec axis angle: the MEASURED direction the North sweep moved the star on the sensor.
        // Carrying this (rather than assuming RA + 90deg) is what gets the Dec SENSE right -- on a
        // sensor where North is clockwise from West (southern hemisphere / certain pier sides) the
        // assumed +90deg inverts Dec corrections and guiding runs away. With AssumeDecOrthogonal we
        // still take the sense from the measurement (sign of the RA->Dec cross product) but snap the
        // angle to an exact perpendicular; otherwise we keep the measured angle (handles cone error
        // / camera tilt). This mirrors PHD2's "Assume Dec orthogonal to RA" option (default off).
        var measuredDecAngleRad = Math.Atan2(decDy, decDx);
        var northIsCcwFromWest = Math.Sin(measuredDecAngleRad - cameraAngleRad) >= 0;
        var decAngleRad = AssumeDecOrthogonal
            ? cameraAngleRad + (northIsCcwFromWest ? Math.PI / 2.0 : -Math.PI / 2.0)
            : measuredDecAngleRad;

        // Guide rates in pixels per second
        var raRatePixPerSec = raDisplacementPx / totalRaTimeSec;
        var decRatePixPerSec = decDisplacementPx / totalDecTimeSec;

        return new GuiderCalibrationResult(
            CameraAngleRad: cameraAngleRad,
            DecAngleRad: decAngleRad,
            RaRatePixPerSec: raRatePixPerSec,
            DecRatePixPerSec: decRatePixPerSec,
            RaDisplacementPx: raDisplacementPx,
            DecDisplacementPx: decDisplacementPx,
            TotalCalibrationTimeSec: totalRaTimeSec + totalDecTimeSec,
            BacklashClearingStepsRa: raBacklashResult.StepsUsed,
            BacklashClearingStepsDec: decBacklashResult.StepsUsed,
            Overlay: new CalibrationOverlayData(raOrigin, decOrigin, raSteps, decSteps,
                PixelScaleArcsec: 1.0, CameraAngleRad: cameraAngleRad,
                RaRateArcsecPerSec: 0, DecRateArcsecPerSec: 0,
                BacklashClearingStepsRa: raBacklashResult.StepsUsed,
                BacklashClearingStepsDec: decBacklashResult.StepsUsed));
    }

    /// <summary>
    /// Folds an absolute angle difference (degrees, up to 360) into [0, 180] so two directions
    /// can be compared by their shortest angular separation regardless of winding.
    /// </summary>
    internal static double FoldAngleDiffDeg(double absDiffDeg)
        => absDiffDeg > 180.0 ? 360.0 - absDiffDeg : absDiffDeg;

    /// <summary>
    /// Sweep linearity = net displacement / total path length travelled through the recorded steps.
    /// 1.0 = perfectly monotonic; lower means the star wandered during the sweep. Returns 1.0 when
    /// there are too few steps to judge (so a short sweep is never rejected on this basis alone).
    /// </summary>
    internal static double SweepLinearity(CalibrationStep origin, ImmutableArray<CalibrationStep> steps, double netDisplacementPx)
    {
        if (steps.Length < 2)
        {
            return 1.0;
        }

        var path = 0.0;
        var prev = origin;
        foreach (var step in steps)
        {
            var dx = step.X - prev.X;
            var dy = step.Y - prev.Y;
            path += Math.Sqrt(dx * dx + dy * dy);
            prev = step;
        }

        return path > 1e-6 ? Math.Min(1.0, netDisplacementPx / path) : 1.0;
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
    /// <param name="timeProvider">Time provider for delays.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result indicating whether the saved calibration can be reused.</returns>
    public async ValueTask<CalibrationValidationResult> ValidateAsync(
        GuiderCalibrationResult savedCalibration,
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        Func<CancellationToken, ValueTask<Image>> captureFrame,
        ITimeProvider timeProvider,
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
        await WaitForPulseCompleteAsync(pulseTarget, timeProvider, pulseDuration, cancellationToken);

        var frame = await captureFrame(cancellationToken);
        var result = tracker.ProcessFrame(frame.GetChannelArray(0));

        // Return the star to its original position
        await pulseTarget.PulseGuideAsync(GuideDirection.East, pulseDuration, cancellationToken);
        await WaitForPulseCompleteAsync(pulseTarget, timeProvider, pulseDuration, cancellationToken);

        // Reset tracker for next use
        tracker.Reset();
        var returnFrame = await captureFrame(cancellationToken);
        tracker.ProcessFrame(returnFrame.GetChannelArray(0));

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
        var angleDiffDeg = FoldAngleDiffDeg(Math.Abs(measuredAngleRad - savedCalibration.CameraAngleRad) * 180.0 / Math.PI);

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

        // --- Dec axis validation (North pulse) ---
        // The RA-only check above let a saved calibration with a stale/wrong Dec rate sail
        // through "validated", which then guides Dec badly (the live symptom: RA RMS fine, Dec
        // diverging). Validate the Dec axis too: pulse North, measure displacement, and reject
        // (recalibrate) if the Dec rate has drifted or the Dec axis is no longer ~orthogonal to
        // RA (e.g. wrong pier side / flip). The tracker was reset + reacquired after the RA
        // return above, so it holds a fresh star.
        if (!tracker.IsAcquired)
        {
            return CalibrationValidationResult.AngleChanged;
        }
        tracker.SetLockPosition();

        await pulseTarget.PulseGuideAsync(GuideDirection.North, pulseDuration, cancellationToken);
        await WaitForPulseCompleteAsync(pulseTarget, timeProvider, pulseDuration, cancellationToken);
        var decFrame = await captureFrame(cancellationToken);
        var decResult = tracker.ProcessFrame(decFrame.GetChannelArray(0));

        // Return the star (South) and re-acquire for the caller.
        await pulseTarget.PulseGuideAsync(GuideDirection.South, pulseDuration, cancellationToken);
        await WaitForPulseCompleteAsync(pulseTarget, timeProvider, pulseDuration, cancellationToken);
        tracker.Reset();
        var decReturnFrame = await captureFrame(cancellationToken);
        tracker.ProcessFrame(decReturnFrame.GetChannelArray(0));

        if (decResult is null)
        {
            return CalibrationValidationResult.AngleChanged;
        }

        var ddx = decResult.Value.DeltaX;
        var ddy = decResult.Value.DeltaY;
        var decDisplacement = Math.Sqrt(ddx * ddx + ddy * ddy);

        if (decDisplacement < 1.0)
        {
            // Dec axis not responding anything like the saved rate -> recalibrate.
            return CalibrationValidationResult.RateDrifted;
        }

        var savedDecRate = Math.Abs(savedCalibration.DecRatePixPerSec);
        if (savedDecRate > 1e-6)
        {
            var measuredDecRate = decDisplacement / pulseDuration.TotalSeconds;
            var decRateDeviation = Math.Abs(measuredDecRate - savedDecRate) / savedDecRate;
            if (decRateDeviation > RateToleranceFraction)
            {
                return CalibrationValidationResult.RateDrifted;
            }
        }

        // Orthogonality: the Dec (North) displacement direction must be ~90deg from the RA
        // (West) displacement direction. If the saved camera angle no longer keeps the axes
        // orthogonal (pier flip, large rotation), reject so we recalibrate cleanly. This measures
        // the same physical quantity as the fresh-calibration degeneracy gate, so it uses the same
        // generous tolerance: a rig with real cone error / camera tilt (5-30deg apparent
        // non-orthogonality) passes fresh calibration and must not have its saved calibration
        // invalidated every session. Session-to-session DRIFT is caught by the camera-angle check
        // above, which compares against the saved angle at the tight AngleToleranceDeg.
        var measuredDecAngleRad = Math.Atan2(ddy, ddx);
        var raToDecDeg = FoldAngleDiffDeg(Math.Abs(measuredDecAngleRad - measuredAngleRad) * 180.0 / Math.PI);
        if (Math.Abs(raToDecDeg - 90.0) > FreshOrthogonalityToleranceDeg)
        {
            return CalibrationValidationResult.AngleChanged;
        }

        return CalibrationValidationResult.Valid;
    }

    internal async ValueTask<BacklashClearingResult> ClearBacklashAsync(
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        Func<CancellationToken, ValueTask<Image>> captureFrame,
        ITimeProvider timeProvider,
        GuideDirection direction,
        CancellationToken cancellationToken)
    {
        // Record pre-clearing star position — displacement accumulates from here
        tracker.SetLockPosition();

        for (var i = 0; i < MaxBacklashClearingSteps; i++)
        {
            await pulseTarget.PulseGuideAsync(direction, CalibrationPulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(pulseTarget, timeProvider, CalibrationPulseDuration, cancellationToken);

            var frame = await captureFrame(cancellationToken);
            var result = tracker.ProcessFrame(frame.GetChannelArray(0));

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

    /// <summary>
    /// Measures displacement by sending calibration pulses and recording per-step star positions.
    /// Returns the final centroid result and the absolute (X, Y) position after each step.
    /// </summary>
    private static async ValueTask<(GuiderCentroidResult Result, ImmutableArray<CalibrationStep> Steps)?> MeasureDisplacementAsync(
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        Func<CancellationToken, ValueTask<Image>> captureFrame,
        ITimeProvider timeProvider,
        GuideDirection direction,
        int steps,
        TimeSpan pulseDuration,
        CancellationToken cancellationToken,
        Action<CalibrationStep>? onStep = null)
    {
        var stepList = new List<CalibrationStep>(steps);
        GuiderCentroidResult lastResult = default;

        for (var i = 0; i < steps; i++)
        {
            await pulseTarget.PulseGuideAsync(direction, pulseDuration, cancellationToken);
            await WaitForPulseCompleteAsync(pulseTarget, timeProvider, pulseDuration, cancellationToken);

            var frame = await captureFrame(cancellationToken);
            if (tracker.ProcessFrame(frame.GetChannelArray(0)) is not { } result)
            {
                return null; // Lost star during calibration
            }

            lastResult = result;
            var step = new CalibrationStep(result.X, result.Y);
            stepList.Add(step);
            onStep?.Invoke(step);
        }

        return stepList.Count > 0 ? (lastResult, [.. stepList]) : null;
    }

    private static async ValueTask WaitForPulseCompleteAsync(
        IPulseGuideTarget pulseTarget, ITimeProvider timeProvider, TimeSpan pulseDuration, CancellationToken cancellationToken)
    {
        // Wait most of the pulse duration upfront, then poll at small intervals
        var bulkWait = pulseDuration * 0.9;
        if (bulkWait > TimeSpan.Zero)
        {
            await timeProvider.SleepAsync(bulkWait, cancellationToken);
        }

        var pollInterval = TimeSpan.FromMilliseconds(50);
        var maxPolls = (int)(pulseDuration.TotalMilliseconds / pollInterval.TotalMilliseconds) + 20;
        while (await pulseTarget.IsPulseGuidingAsync(cancellationToken) && --maxPolls > 0)
        {
            await timeProvider.SleepAsync(pollInterval, cancellationToken);
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
/// <param name="CameraAngleRad">Angle of the RA (West) axis on the sensor in radians.</param>
/// <param name="DecAngleRad">Measured angle of the Dec (North) axis on the sensor in radians. Carries
/// the Dec SENSE and any non-orthogonality; see <see cref="TransformToMountAxes"/>.</param>
/// <param name="RaRatePixPerSec">RA guide rate in pixels per second.</param>
/// <param name="DecRatePixPerSec">Dec guide rate in pixels per second.</param>
/// <param name="RaDisplacementPx">Total RA displacement measured during calibration (pixels).</param>
/// <param name="DecDisplacementPx">Total Dec displacement measured during calibration (pixels).</param>
/// <param name="TotalCalibrationTimeSec">Total calibration time in seconds.</param>
/// <param name="BacklashClearingStepsRa">Number of backlash clearing steps used for RA axis.</param>
/// <param name="BacklashClearingStepsDec">Number of backlash clearing steps used for Dec axis.</param>
internal readonly record struct GuiderCalibrationResult(
    double CameraAngleRad,
    double DecAngleRad,
    double RaRatePixPerSec,
    double DecRatePixPerSec,
    double RaDisplacementPx,
    double DecDisplacementPx,
    double TotalCalibrationTimeSec,
    int BacklashClearingStepsRa = 0,
    int BacklashClearingStepsDec = 0,
    CalibrationOverlayData? Overlay = null)
{
    /// <summary>
    /// Camera (RA-axis) angle in degrees.
    /// </summary>
    public readonly double CameraAngleDeg => CameraAngleRad * 180.0 / Math.PI;

    /// <summary>
    /// Dec-axis angle in degrees.
    /// </summary>
    public readonly double DecAngleDeg => DecAngleRad * 180.0 / Math.PI;

    /// <summary>
    /// Returns the calibration with the DEC sense reversed for a German-mount meridian flip
    /// (PHD2's "reverse Dec output after meridian flip"). Across a GEM flip the Dec axis mechanically
    /// reverses relative to the sky while RA tracks the same way, so the Dec guide RESPONSE on the
    /// sensor inverts but the RA response does not. Only the Dec rate/displacement sign flips here —
    /// the measured axis ANGLES still describe where the motor axes point on the sensor and are left
    /// unchanged. Negating <see cref="DecRatePixPerSec"/> inverts the Dec correction direction the
    /// <see cref="ProportionalGuideController"/> derives, which is exactly what keeps the loop
    /// converging on the post-flip pier side. Single source of truth for both flip sites in
    /// <c>BuiltInGuiderDriver</c>; pinned by the post-flip convergence test in GuiderCalibrationTests.
    /// </summary>
    public readonly GuiderCalibrationResult WithMeridianFlip()
        => this with
        {
            DecRatePixPerSec = -DecRatePixPerSec,
            DecDisplacementPx = -DecDisplacementPx,
        };

    /// <summary>
    /// Decomposes a pixel-space error (dX, dY) onto the two MEASURED mount-axis directions
    /// (RA-West at <see cref="CameraAngleRad"/>, Dec-North at <see cref="DecAngleRad"/>) by solving
    /// the 2x2 basis. This honours the measured Dec SENSE (so a sensor whose North is clockwise from
    /// West yields correctly-signed decPixels instead of the inverted value a fixed +90deg rotation
    /// gives) and any non-orthogonality (cone error / camera tilt). For an exactly-orthogonal +90deg
    /// Dec axis it reduces to the classic rotation.
    /// </summary>
    /// <param name="deltaX">Error in X pixels.</param>
    /// <param name="deltaY">Error in Y pixels.</param>
    /// <returns>Error decomposed into (raPixels, decPixels) along the mount axes.</returns>
    public readonly (double RaPixels, double DecPixels) TransformToMountAxes(double deltaX, double deltaY)
    {
        var cosRa = Math.Cos(CameraAngleRad);
        var sinRa = Math.Sin(CameraAngleRad);
        var cosDec = Math.Cos(DecAngleRad);
        var sinDec = Math.Sin(DecAngleRad);
        // Solve [deltaX; deltaY] = ra*u_ra + dec*u_dec for (ra, dec). det = sin(DecAngle - CameraAngle),
        // i.e. +/-1 when the axes are orthogonal, ~0 when degenerate (parallel).
        var det = cosRa * sinDec - cosDec * sinRa;
        if (Math.Abs(det) < 1e-6)
        {
            // Degenerate axes (should be rejected upstream): fall back to the orthogonal projection.
            return (deltaX * cosRa + deltaY * sinRa, -deltaX * sinRa + deltaY * cosRa);
        }
        var raPixels = (deltaX * sinDec - deltaY * cosDec) / det;
        var decPixels = (-deltaX * sinRa + deltaY * cosRa) / det;
        return (raPixels, decPixels);
    }
}

/// <summary>
/// A single position (absolute image pixels) recorded during calibration.
/// </summary>
/// <param name="X">Star X position in image pixels.</param>
/// <param name="Y">Star Y position in image pixels.</param>
public readonly record struct CalibrationStep(double X, double Y);

/// <summary>
/// Per-step calibration data for rendering the L-shaped overlay on the guide camera image.
/// All coordinates are absolute image pixel positions.
/// </summary>
/// <param name="RaOrigin">Star position at the start of RA (West) measurement.</param>
/// <param name="DecOrigin">Star position at the start of Dec (North) measurement.</param>
/// <param name="RaSteps">Absolute star positions after each West calibration pulse.</param>
/// <param name="DecSteps">Absolute star positions after each North calibration pulse.</param>
/// <param name="PixelScaleArcsec">Guider pixel scale in arcsec/px for converting to display units.</param>
/// <param name="CameraAngleRad">Camera angle from calibration, for rotating displacements to RA/Dec axes.</param>
/// <param name="RaRateArcsecPerSec">RA guide rate in arcsec/sec.</param>
/// <param name="DecRateArcsecPerSec">Dec guide rate in arcsec/sec.</param>
/// <param name="BacklashClearingStepsRa">RA backlash clearing pulses used.</param>
/// <param name="BacklashClearingStepsDec">Dec backlash clearing pulses used.</param>
public sealed record CalibrationOverlayData(
    CalibrationStep RaOrigin,
    CalibrationStep DecOrigin,
    ImmutableArray<CalibrationStep> RaSteps,
    ImmutableArray<CalibrationStep> DecSteps,
    double PixelScaleArcsec,
    double CameraAngleRad,
    double RaRateArcsecPerSec = 0,
    double DecRateArcsecPerSec = 0,
    int BacklashClearingStepsRa = 0,
    int BacklashClearingStepsDec = 0)
{
    /// <summary>Camera angle in degrees.</summary>
    public double CameraAngleDeg => CameraAngleRad * 180.0 / Math.PI;

    /// <summary>Orthogonality error: deviation from 90° between RA and Dec axes.</summary>
    public double OrthoErrorDeg
    {
        get
        {
            if (RaSteps.IsDefaultOrEmpty || DecSteps.IsDefaultOrEmpty)
            {
                return 0;
            }
            var raLast = RaSteps[^1];
            var decLast = DecSteps[^1];
            var raAngle = Math.Atan2(raLast.Y - RaOrigin.Y, raLast.X - RaOrigin.X);
            var decAngle = Math.Atan2(decLast.Y - DecOrigin.Y, decLast.X - DecOrigin.X);
            var diff = GuiderCalibration.FoldAngleDiffDeg(Math.Abs(decAngle - raAngle) * 180.0 / Math.PI);
            return Math.Abs(diff - 90);
        }
    }
}
