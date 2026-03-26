using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Self-contained guide loop that captures frames, computes centroid errors,
/// and sends pulse guide corrections to the mount. Supports both classical
/// P-controller and neural model controllers with optional online learning.
/// </summary>
internal sealed class GuideLoop
{
    private readonly IPulseGuideTarget _pulseTarget;
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

    /// <summary>Last captured guide frame (mono float[,]). Updated each iteration.</summary>
    internal float[,]? LastFrame { get; private set; }

    /// <summary>Last centroid result from the tracker. Null if star was lost.</summary>
    internal GuiderCentroidResult? LastCentroidResult { get; private set; }

    // Online learning state
    private ExperienceReplayBuffer? _experienceBuffer;
    private NeuralGuideTrainer? _onlineTrainer;
    private NeuralGuidePerformanceMonitor? _performanceMonitor;
    private DirectoryInfo? _profileFolder;
    private int _consecutiveFallbacks;
    private int _framesSinceTraining;
    private double _lastSaveTimestamp;
    private double _lastRaError;
    private double _lastDecError;
    private bool _hasPreviousError;

    // Thread-safe scratch buffers for neural inference during online learning
    private readonly float[] _hidden1Scratch = new float[NeuralGuideModel.Hidden1Size];
    private readonly float[] _hidden2Scratch = new float[NeuralGuideModel.Hidden2Size];
    private readonly float[] _outputScratch = new float[NeuralGuideModel.OutputSize];

    /// <summary>
    /// Maximum pulse duration for corrections in milliseconds.
    /// </summary>
    public double MaxPulseMs { get; set; } = 2000;

    /// <summary>
    /// Blend factor for neural model corrections. Controls how much the neural output
    /// modifies the P-controller baseline. 0 = P-controller only, 1 = full neural replacement.
    /// Default: 0.15 (neural provides 15% refinement on top of P-controller).
    /// Kept conservative to prevent runaway corrections from an under-trained model;
    /// online learning gradually improves the model in-place.
    /// </summary>
    public double NeuralBlendFactor { get; set; } = 0.15;

    /// <summary>
    /// Number of frames between online training updates.
    /// </summary>
    public int OnlineTrainingInterval { get; set; } = 20;

    /// <summary>
    /// Minimum experiences before online training starts.
    /// </summary>
    public int MinExperiencesBeforeTraining { get; set; } = 64;

    /// <summary>
    /// Batch size for online training.
    /// </summary>
    public int OnlineBatchSize { get; set; } = 16;

    /// <summary>
    /// Minimum seconds between saves of model weights.
    /// </summary>
    public double SaveIntervalSeconds { get; set; } = 60.0;

    /// <summary>
    /// Consecutive neural fallbacks before hard-disabling the neural model.
    /// </summary>
    public int MaxConsecutiveFallbacks { get; set; } = 5;

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

    /// <summary>
    /// Whether online learning is active.
    /// </summary>
    public bool IsOnlineLearningEnabled => _experienceBuffer is not null;

    /// <summary>
    /// The performance monitor, if online learning is enabled.
    /// </summary>
    public NeuralGuidePerformanceMonitor? PerformanceMonitor => _performanceMonitor;

    public GuideLoop(
        IPulseGuideTarget pulseTarget,
        GuiderCentroidTracker tracker,
        ProportionalGuideController pController,
        IExternal external)
    {
        _pulseTarget = pulseTarget;
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
        _neuralFeatures = new NeuralGuideFeatures(siteLatitude: 0);
        _useNeuralModel = true;
        _consecutiveFallbacks = 0;
    }

    /// <summary>
    /// Disables the neural model, reverting to P-controller only.
    /// </summary>
    public void DisableNeuralModel()
    {
        _useNeuralModel = false;
    }

    /// <summary>
    /// Enables online learning: experience recording, periodic training, and model persistence.
    /// Must be called after <see cref="EnableNeuralModel"/> and <see cref="SetCalibration"/>.
    /// </summary>
    /// <param name="onlineLearningRate">Learning rate for online updates (typically 10x lower than offline).</param>
    /// <param name="profileFolder">Directory for saving model weights.</param>
    public void EnableOnlineLearning(float onlineLearningRate = 0.0001f, DirectoryInfo? profileFolder = null)
    {
        if (_neuralModel is null)
        {
            throw new InvalidOperationException("Neural model must be enabled before online learning.");
        }

        _experienceBuffer = new ExperienceReplayBuffer();
        _onlineTrainer = new NeuralGuideTrainer(_neuralModel, onlineLearningRate, OnlineBatchSize);
        _performanceMonitor = new NeuralGuidePerformanceMonitor();
        _profileFolder = profileFolder;
        _framesSinceTraining = 0;
        _lastSaveTimestamp = 0;
    }

    /// <summary>
    /// Runs the guide loop: capture frame → compute error → send correction.
    /// Continues until cancelled. When online learning is enabled, periodically
    /// trains the neural model from accumulated experience.
    /// </summary>
    /// <param name="captureFrame">Function that captures a guide frame.</param>
    /// <param name="exposureInterval">Time between guide exposures.</param>
    /// <param name="hourAngle">Current hour angle in hours (for neural model features).</param>
    /// <param name="declination">Target declination in degrees (for neural model features).</param>
    /// <param name="siteLatitude">Observer latitude in degrees (for altitude computation).</param>
    /// <param name="cancellationToken">Cancellation token to stop guiding.</param>
    public async ValueTask RunAsync(
        Func<CancellationToken, ValueTask<float[,]>> captureFrame,
        TimeSpan exposureInterval,
        double hourAngle,
        double declination,
        double siteLatitude,
        CancellationToken cancellationToken)
    {
        if (_calibration is not { } calibration)
        {
            throw new InvalidOperationException("Cannot start guiding without calibration.");
        }

        _isGuiding = true;
        _errorTracker.Reset();
        if (_neuralFeatures is not null)
        {
            _neuralFeatures = new NeuralGuideFeatures(siteLatitude);
        }
        _performanceMonitor?.Reset();
        _experienceBuffer?.Reset();
        _guideStartTimestamp = GetTimestamp();
        _hasPreviousError = false;
        _consecutiveFallbacks = 0;
        _framesSinceTraining = 0;

        var trainingRng = _experienceBuffer is not null ? new Random(42) : null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frameStart = GetTimestamp();

                // Capture and process frame
                var frame = await captureFrame(cancellationToken);
                LastFrame = frame;
                var result = _tracker.ProcessFrame(frame);
                LastCentroidResult = result;

                if (result is null)
                {
                    // Star lost — wait and try again
                    _hasPreviousError = false;
                    await _external.SleepAsync(exposureInterval, cancellationToken);
                    continue;
                }

                // Transform pixel error to mount axes
                var (raErrorPx, decErrorPx) = calibration.TransformToMountAxes(
                    result.Value.DeltaX, result.Value.DeltaY);

                var timestamp = GetTimestamp();
                _errorTracker.Add(timestamp, raErrorPx, decErrorPx);

                // Update outcome of previous experience
                if (_hasPreviousError && _experienceBuffer is not null)
                {
                    _experienceBuffer.UpdateOutcome(raErrorPx, decErrorPx, _lastRaError, _lastDecError);
                }

                // Compute P-controller correction (always, for shadow comparison and experience recording)
                var pCorrection = _pController.Compute(calibration, result.Value.DeltaX, result.Value.DeltaY);

                // Compute actual correction (neural or P-controller)
                var (correction, usedNeural) = ComputeCorrection(
                    calibration, result.Value.DeltaX, result.Value.DeltaY,
                    timestamp, hourAngle, declination, pCorrection);

                // Record experience for online learning
                if (_experienceBuffer is not null && _neuralFeatures is not null)
                {
                    var features = new float[NeuralGuideModel.InputSize];
                    var (raErr, decErr) = calibration.TransformToMountAxes(result.Value.DeltaX, result.Value.DeltaY);
                    _neuralFeatures.Build(raErr, decErr, timestamp,
                        _errorTracker.RaRmsShort, _errorTracker.DecRmsShort, hourAngle, declination, features);

                    var experience = new OnlineGuideExperience
                    {
                        Features = features,
                        TargetRa = (float)Math.Clamp(pCorrection.RaPulseMs / MaxPulseMs, -1.0, 1.0),
                        TargetDec = (float)Math.Clamp(pCorrection.DecPulseMs / MaxPulseMs, -1.0, 1.0),
                        PriorityWeight = 1.0f,
                        OutcomeKnown = false
                    };
                    _experienceBuffer.Add(experience);
                }

                // Update performance monitor
                if (usedNeural && _performanceMonitor is not null)
                {
                    var actualErrorMag = Math.Sqrt(raErrorPx * raErrorPx + decErrorPx * decErrorPx);
                    // Estimate P-controller residual: error magnitude after theoretical P correction
                    var pRaResidual = raErrorPx - pCorrection.RaPulseMs / 1000.0 * calibration.RaRatePixPerSec;
                    var pDecResidual = decErrorPx - pCorrection.DecPulseMs / 1000.0 * calibration.DecRatePixPerSec;
                    var pResidualMag = Math.Sqrt(pRaResidual * pRaResidual + pDecResidual * pDecResidual);
                    _performanceMonitor.Record(timestamp, actualErrorMag, pResidualMag);
                }

                _lastRaError = raErrorPx;
                _lastDecError = decErrorPx;
                _hasPreviousError = true;

                // Apply RA correction
                if (correction.HasRaCorrection)
                {
                    var direction = correction.RaPulseMs > 0
                        ? GuideDirection.West
                        : GuideDirection.East;
                    await _pulseTarget.PulseGuideAsync(direction, correction.RaPulseDuration, cancellationToken);
                }

                // Apply Dec correction
                if (correction.HasDecCorrection)
                {
                    var direction = correction.DecPulseMs > 0
                        ? GuideDirection.North
                        : GuideDirection.South;
                    await _pulseTarget.PulseGuideAsync(direction, correction.DecPulseDuration, cancellationToken);
                }

                if (correction.HasRaCorrection || correction.HasDecCorrection)
                {
                    _neuralFeatures?.RecordCorrection(timestamp);
                }

                // Online training step (synchronous, fast: ~16 samples * 418 weights < 1ms)
                if (_onlineTrainer is not null && _experienceBuffer is not null && trainingRng is not null)
                {
                    _framesSinceTraining++;
                    if (_framesSinceTraining >= OnlineTrainingInterval
                        && _experienceBuffer.Count >= MinExperiencesBeforeTraining)
                    {
                        _onlineTrainer.TrainOnBatch(_experienceBuffer, OnlineBatchSize, trainingRng);
                        _framesSinceTraining = 0;

                        // Throttled save
                        if (_profileFolder is not null && _calibration is not null
                            && timestamp - _lastSaveTimestamp >= SaveIntervalSeconds)
                        {
                            await NeuralGuideModelPersistence.SaveAsync(
                                _neuralModel!, calibration, _profileFolder, cancellationToken);
                            _lastSaveTimestamp = timestamp;
                        }
                    }
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
            // Final save on shutdown
            if (_profileFolder is not null && _neuralModel is not null && _calibration is not null)
            {
                using var saveCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await NeuralGuideModelPersistence.SaveAsync(
                        _neuralModel, calibration, _profileFolder, saveCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Best-effort save on shutdown
                }
            }

            _isGuiding = false;
        }
    }

    private (GuideCorrection Correction, bool UsedNeural) ComputeCorrection(
        GuiderCalibrationResult calibration,
        double deltaX, double deltaY,
        double timestamp, double hourAngle, double declination,
        GuideCorrection pCorrection)
    {
        if (_useNeuralModel && _neuralModel is not null && _neuralFeatures is not null)
        {
            var (raErrorPx, decErrorPx) = calibration.TransformToMountAxes(deltaX, deltaY);

            Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
            _neuralFeatures.Build(
                raErrorPx, decErrorPx, timestamp,
                _errorTracker.RaRmsShort, _errorTracker.DecRmsShort,
                hourAngle, declination, input);

            var output = _neuralModel.ForwardWithScratch(input, _hidden1Scratch, _hidden2Scratch, _outputScratch);
            var raPulseMs = output[0] * MaxPulseMs;
            var decPulseMs = output[1] * MaxPulseMs;

            // Gate 1: bounds check
            if (Math.Abs(raPulseMs) > MaxPulseMs || Math.Abs(decPulseMs) > MaxPulseMs)
            {
                _consecutiveFallbacks++;
                if (_consecutiveFallbacks >= MaxConsecutiveFallbacks)
                {
                    DisableNeuralModel();
                }
                return (pCorrection, false);
            }

            // Gate 2: performance monitor
            if (_performanceMonitor is not null && !_performanceMonitor.IsNeuralModelHelping
                && _errorTracker.ShortWindowCount >= _performanceMonitor.MinSamples)
            {
                _consecutiveFallbacks++;
                return (pCorrection, false);
            }

            _consecutiveFallbacks = 0;

            // Blend neural output with P-controller baseline. The P-controller provides
            // a proven-reliable correction; the neural model refines it. This prevents
            // the neural model from catastrophically over-correcting when inputs are noisy
            // (gear noise, seeing jitter).
            var blendedRa = (1.0 - NeuralBlendFactor) * pCorrection.RaPulseMs + NeuralBlendFactor * raPulseMs;
            var blendedDec = (1.0 - NeuralBlendFactor) * pCorrection.DecPulseMs + NeuralBlendFactor * decPulseMs;
            return (new GuideCorrection(blendedRa, blendedDec), true);
        }

        // P-controller only
        return (pCorrection, false);
    }

    private double GetTimestamp()
    {
        return _external.TimeProvider.GetTimestamp() / (double)_external.TimeProvider.TimestampFrequency;
    }
}
