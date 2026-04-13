using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Imaging;

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
    private readonly TimeProvider _timeProvider;

    private GuiderCalibrationResult? _calibration;
    private NeuralGuideModel? _neuralModel;
    private NeuralGuideFeatures? _neuralFeatures;
    private bool _useNeuralModel;
    private bool _isGuiding;
    private double _guideStartTimestamp;

    /// <summary>Last captured guide frame. Updated each iteration. Caller must Release() when replacing.</summary>
    private volatile Image? _lastFrame;
    internal Image? LastFrame { get => _lastFrame; private set => _lastFrame = value; }

    /// <summary>Last centroid result from the tracker. Null if star was lost.</summary>
    internal GuiderCentroidResult? LastCentroidResult { get; private set; }

    /// <summary>Last applied guide correction (pulse durations in ms).</summary>
    internal GuideCorrection? LastCorrection { get; private set; }

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
    private double _lastRaCorrectionPx;
    private double _lastDecCorrectionPx;
    private int _guideFrameCount;

    // Thread-safe scratch buffers for neural inference during online learning
    private readonly float[] _hidden1Scratch = new float[NeuralGuideModel.Hidden1Size];
    private readonly float[] _hidden2Scratch = new float[NeuralGuideModel.Hidden2Size];
    private readonly float[] _outputScratch = new float[NeuralGuideModel.OutputSize];

    /// <summary>
    /// Maximum pulse duration for corrections in milliseconds.
    /// </summary>
    public double MaxPulseMs { get; set; } = 2000;

    /// <summary>
    /// Target blend factor for neural model corrections after ramp-in completes.
    /// 0 = P-controller only, 1 = full neural replacement.
    /// Default: 0.5 (50% neural, 50% P-controller — matches PHD2's prediction_gain).
    /// During the ramp-in phase, the effective blend linearly increases from 0 to this value.
    /// </summary>
    public double NeuralBlendFactor { get; set; } = 0.5;

    /// <summary>
    /// Number of frames over which the neural blend ramps from 0 to <see cref="NeuralBlendFactor"/>.
    /// Set to approximately 2 PE cycles worth of frames for the model to learn before contributing.
    /// At 2s exposure with ~480s PE period, 2 cycles = 480 frames.
    /// Default: 480 frames (~16 min at 2s cadence).
    /// </summary>
    public int BlendRampInFrames { get; set; } = 480;

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
    /// The centroid tracker. Exposed for dither offset manipulation.
    /// </summary>
    internal GuiderCentroidTracker Tracker => _tracker;

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
        IExternal external,
        TimeProvider timeProvider)
    {
        _pulseTarget = pulseTarget;
        _tracker = tracker;
        _pController = pController;
        _external = external;
        _timeProvider = timeProvider;
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
    /// <param name="getAxisPosition">Optional callback to read raw encoder position per axis. Returns null if mount doesn't expose encoder data.</param>
    /// <param name="wormStepsPerCycleRa">RA worm gear steps per single worm rotation (CPR / wormTeeth). 0 = unknown/unavailable.</param>
    /// <param name="wormStepsPerCycleDec">Dec worm gear steps per single worm rotation. 0 = unknown/unavailable.</param>
    /// <param name="cancellationToken">Cancellation token to stop guiding.</param>
    public async ValueTask RunAsync(
        Func<CancellationToken, ValueTask<Image>> captureFrame,
        TimeSpan exposureInterval,
        double hourAngle,
        double declination,
        double siteLatitude,
        Func<TelescopeAxis, CancellationToken, ValueTask<long?>>? getAxisPosition = null,
        uint wormStepsPerCycleRa = 0,
        uint wormStepsPerCycleDec = 0,
        CancellationToken cancellationToken = default)
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
        _lastRaCorrectionPx = 0;
        _lastDecCorrectionPx = 0;
        _guideFrameCount = 0;
        _consecutiveFallbacks = 0;
        _framesSinceTraining = 0;

        var trainingRng = _experienceBuffer is not null ? new Random(42) : null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frameStart = GetTimestamp();

                // Capture and process frame — release previous frame's ChannelBuffer
                LastFrame?.Release();
                var frame = await captureFrame(cancellationToken);
                LastFrame = frame;
                var result = _tracker.ProcessFrame(frame.GetChannelArray(0));
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

                // Update outcome of previous experience with hindsight-optimal target
                if (_hasPreviousError && _experienceBuffer is not null)
                {
                    var raRateScale = 1000.0 / (calibration.RaRatePixPerSec * MaxPulseMs);
                    var decRateScale = 1000.0 / (Math.Abs(calibration.DecRatePixPerSec) * MaxPulseMs);
                    _experienceBuffer.UpdateOutcome(raErrorPx, decErrorPx, _lastRaError, _lastDecError,
                        raRateScale, decRateScale);
                }

                // Read encoder positions for PE phase features (non-blocking, best-effort)
                var raPhase = double.NaN;
                var decPhase = double.NaN;
                if (getAxisPosition is not null)
                {
                    var raPos = await getAxisPosition(TelescopeAxis.Primary, cancellationToken);
                    if (raPos is { } rp && wormStepsPerCycleRa > 0)
                    {
                        raPhase = ((rp % wormStepsPerCycleRa + wormStepsPerCycleRa) % wormStepsPerCycleRa)
                            / (double)wormStepsPerCycleRa * 2.0 * Math.PI;
                    }
                    var decPos = await getAxisPosition(TelescopeAxis.Seconary, cancellationToken);
                    if (decPos is { } dp && wormStepsPerCycleDec > 0)
                    {
                        decPhase = ((dp % wormStepsPerCycleDec + wormStepsPerCycleDec) % wormStepsPerCycleDec)
                            / (double)wormStepsPerCycleDec * 2.0 * Math.PI;
                    }
                }

                // Compute P-controller correction (always, for shadow comparison and experience recording)
                var pCorrection = _pController.Compute(calibration, result.Value.DeltaX, result.Value.DeltaY);

                // Compute actual correction (neural or P-controller)
                var (correction, usedNeural) = ComputeCorrection(
                    calibration, result.Value.DeltaX, result.Value.DeltaY,
                    timestamp, hourAngle, declination, raPhase, decPhase, pCorrection);

                // Record experience for online learning
                if (_experienceBuffer is not null && _neuralFeatures is not null)
                {
                    var features = new float[NeuralGuideModel.InputSize];
                    var (raErr, decErr) = calibration.TransformToMountAxes(result.Value.DeltaX, result.Value.DeltaY);
                    // Pass previous frame's correction in pixels so gear error can be accumulated
                    _neuralFeatures.Build(raErr, decErr,
                        _lastRaCorrectionPx, _lastDecCorrectionPx,
                        timestamp,
                        _errorTracker.RaRmsShort, _errorTracker.DecRmsShort, hourAngle, declination,
                        raPhase, decPhase, features);

                    var experience = new OnlineGuideExperience
                    {
                        Features = features,
                        // Initial target: P-controller (used if outcome is never observed, e.g. star lost next frame)
                        TargetRa = (float)Math.Clamp(pCorrection.RaPulseMs / MaxPulseMs, -1.0, 1.0),
                        TargetDec = (float)Math.Clamp(pCorrection.DecPulseMs / MaxPulseMs, -1.0, 1.0),
                        // Actual applied correction (for hindsight-optimal target computation)
                        AppliedRaNorm = (float)Math.Clamp(correction.RaPulseMs / MaxPulseMs, -1.0, 1.0),
                        AppliedDecNorm = (float)Math.Clamp(correction.DecPulseMs / MaxPulseMs, -1.0, 1.0),
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
                // Store applied correction in pixels for gear error accumulation next frame
                _lastRaCorrectionPx = correction.RaPulseMs / 1000.0 * calibration.RaRatePixPerSec;
                _lastDecCorrectionPx = correction.DecPulseMs / 1000.0 * Math.Abs(calibration.DecRatePixPerSec);
                _hasPreviousError = true;
                _guideFrameCount++;

                LastCorrection = correction;

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
        double raEncoderPhase, double decEncoderPhase,
        GuideCorrection pCorrection)
    {
        if (_useNeuralModel && _neuralModel is not null && _neuralFeatures is not null)
        {
            var (raErrorPx, decErrorPx) = calibration.TransformToMountAxes(deltaX, deltaY);

            Span<float> input = stackalloc float[NeuralGuideModel.InputSize];
            _neuralFeatures.Build(
                raErrorPx, decErrorPx,
                _lastRaCorrectionPx, _lastDecCorrectionPx,
                timestamp,
                _errorTracker.RaRmsShort, _errorTracker.DecRmsShort,
                hourAngle, declination, raEncoderPhase, decEncoderPhase, input);

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

            // Ramp-in: linearly increase blend from 0 to NeuralBlendFactor over BlendRampInFrames.
            // Like PHD2, the model needs ~2 PE cycles to learn before it should contribute.
            // During ramp-in, the P-controller does the work while the model trains on outcomes.
            var effectiveBlend = BlendRampInFrames > 0
                ? NeuralBlendFactor * Math.Min(1.0, (double)_guideFrameCount / BlendRampInFrames)
                : NeuralBlendFactor;

            var blendedRa = (1.0 - effectiveBlend) * pCorrection.RaPulseMs + effectiveBlend * raPulseMs;
            var blendedDec = (1.0 - effectiveBlend) * pCorrection.DecPulseMs + effectiveBlend * decPulseMs;
            return (new GuideCorrection(blendedRa, blendedDec), true);
        }

        // P-controller only
        return (pCorrection, false);
    }

    private double GetTimestamp()
    {
        return _timeProvider.GetTimestamp() / (double)_timeProvider.TimestampFrequency;
    }
}
