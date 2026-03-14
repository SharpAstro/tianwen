using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Self-contained guide loop that captures frames, computes centroid errors,
/// and sends pulse guide corrections to the mount. Supports both classical
/// P-controller and neural model controllers.
/// </summary>
internal sealed class GuideLoop
{
    private readonly IMountDriver _mount;
    private readonly GuiderCentroidTracker _tracker;
    private readonly GuideErrorTracker _errorTracker;
    private readonly ProportionalGuideController _pController;
    private readonly IExternal _external;

    private GuiderCalibrationResult? _calibration;
    private NeuralGuideModel? _neuralModel;
    private NeuralGuideFeatures? _neuralFeatures;
    private bool _useNeuralModel;
    private bool _isGuiding;
    private double _guideStartTimestamp;

    /// <summary>
    /// Maximum pulse duration for corrections in milliseconds.
    /// </summary>
    public double MaxPulseMs { get; set; } = 2000;

    /// <summary>
    /// Whether the guide loop is currently running.
    /// </summary>
    public bool IsGuiding => _isGuiding;

    /// <summary>
    /// Guide error statistics.
    /// </summary>
    public GuideErrorTracker ErrorTracker => _errorTracker;

    /// <summary>
    /// The calibration result, if calibrated.
    /// </summary>
    public GuiderCalibrationResult? Calibration => _calibration;

    public GuideLoop(
        IMountDriver mount,
        GuiderCentroidTracker tracker,
        ProportionalGuideController pController,
        IExternal external)
    {
        _mount = mount;
        _tracker = tracker;
        _pController = pController;
        _external = external;
        _errorTracker = new GuideErrorTracker();
    }

    /// <summary>
    /// Sets the calibration result from a prior calibration run.
    /// </summary>
    public void SetCalibration(GuiderCalibrationResult calibration)
    {
        _calibration = calibration;
    }

    /// <summary>
    /// Enables the neural model for guide corrections. Falls back to P-controller
    /// if the model produces unreasonable outputs.
    /// </summary>
    public void EnableNeuralModel(NeuralGuideModel model)
    {
        _neuralModel = model;
        _neuralFeatures = new NeuralGuideFeatures();
        _useNeuralModel = true;
    }

    /// <summary>
    /// Disables the neural model, reverting to P-controller only.
    /// </summary>
    public void DisableNeuralModel()
    {
        _useNeuralModel = false;
    }

    /// <summary>
    /// Runs the guide loop: capture frame → compute error → send correction.
    /// Continues until cancelled.
    /// </summary>
    /// <param name="captureFrame">Function that captures a guide frame.</param>
    /// <param name="exposureInterval">Time between guide exposures.</param>
    /// <param name="hourAngle">Current hour angle in hours (for neural model features).</param>
    /// <param name="cancellationToken">Cancellation token to stop guiding.</param>
    public async ValueTask RunAsync(
        Func<CancellationToken, ValueTask<float[,]>> captureFrame,
        TimeSpan exposureInterval,
        double hourAngle,
        CancellationToken cancellationToken)
    {
        if (_calibration is null)
        {
            throw new InvalidOperationException("Cannot start guiding without calibration.");
        }

        _isGuiding = true;
        _errorTracker.Reset();
        _neuralFeatures?.Reset();
        _guideStartTimestamp = GetTimestamp();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frameStart = GetTimestamp();

                // Capture and process frame
                var frame = await captureFrame(cancellationToken);
                var result = _tracker.ProcessFrame(frame);

                if (result is null)
                {
                    // Star lost — wait and try again
                    await _external.SleepAsync(exposureInterval, cancellationToken);
                    continue;
                }

                // Transform pixel error to mount axes
                var (raErrorPx, decErrorPx) = _calibration.Value.TransformToMountAxes(
                    result.Value.DeltaX, result.Value.DeltaY);

                var timestamp = GetTimestamp();
                _errorTracker.Add(timestamp, raErrorPx, decErrorPx);

                // Compute correction
                var correction = ComputeCorrection(
                    _calibration.Value, result.Value.DeltaX, result.Value.DeltaY,
                    timestamp, hourAngle);

                // Apply RA correction
                if (correction.HasRaCorrection)
                {
                    var direction = correction.RaPulseMs > 0
                        ? GuideDirection.West
                        : GuideDirection.East;
                    await _mount.PulseGuideAsync(direction, correction.RaPulseDuration, cancellationToken);
                }

                // Apply Dec correction
                if (correction.HasDecCorrection)
                {
                    var direction = correction.DecPulseMs > 0
                        ? GuideDirection.North
                        : GuideDirection.South;
                    await _mount.PulseGuideAsync(direction, correction.DecPulseDuration, cancellationToken);
                }

                if (correction.HasRaCorrection || correction.HasDecCorrection)
                {
                    _neuralFeatures?.RecordCorrection(timestamp);
                }

                // Wait for the remainder of the exposure interval
                var elapsed = TimeSpan.FromSeconds(GetTimestamp() - frameStart);
                var remaining = exposureInterval - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await _external.SleepAsync(remaining, cancellationToken);
                }
            }
        }
        finally
        {
            _isGuiding = false;
        }
    }

    private GuideCorrection ComputeCorrection(
        GuiderCalibrationResult calibration,
        double deltaX, double deltaY,
        double timestamp, double hourAngle)
    {
        if (_useNeuralModel && _neuralModel is not null && _neuralFeatures is not null)
        {
            var (raErrorPx, decErrorPx) = calibration.TransformToMountAxes(deltaX, deltaY);

            Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
            _neuralFeatures.Build(
                raErrorPx, decErrorPx, timestamp,
                _errorTracker.RaRmsShort, _errorTracker.DecRmsShort,
                hourAngle, input);

            var output = _neuralModel.Forward(input);
            var raPulseMs = output[0] * MaxPulseMs;
            var decPulseMs = output[1] * MaxPulseMs;

            // Sanity check: if neural output is unreasonable, fall back to P-controller
            if (Math.Abs(raPulseMs) <= MaxPulseMs && Math.Abs(decPulseMs) <= MaxPulseMs)
            {
                return new GuideCorrection(raPulseMs, decPulseMs);
            }
        }

        // Fallback: P-controller
        return _pController.Compute(calibration, deltaX, deltaY);
    }

    private double GetTimestamp()
    {
        return _external.TimeProvider.GetTimestamp() / (double)_external.TimeProvider.TimestampFrequency;
    }
}
