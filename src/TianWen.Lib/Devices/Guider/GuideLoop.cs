using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.DAL;
using TianWen.Lib.Devices;
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
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger? _logger;

    /// <summary>
    /// Frames between throttled per-frame guide telemetry lines. At a 2s cadence,
    /// 15 frames ~= one summary every 30s. Star-lost / re-acquisition / neural-fallback
    /// transitions are logged unconditionally (they are the events that explain a jump in
    /// the guide graph), independent of this throttle.
    /// </summary>
    public int TelemetryLogEveryFrames { get; set; } = 15;

    /// <summary>
    /// A re-acquired primary star whose position jumped at least this many pixels from the
    /// pre-loss primary is reported as a DIFFERENT star (the "selected guide star changed"
    /// case the user sees as a discontinuity in the error graph), not the same star recovered.
    /// </summary>
    private const double DifferentStarJumpPx = 8.0;

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

    // Observability: star-lost / re-acquisition transition tracking. The tracker silently
    // drops a star (SNR below floor or moved out of its search ROI) and re-acquires the new
    // brightest on the next frame -- which resets the lock reference and shows up as a jump
    // in the guide graph. These make that visible (logged + surfaced as state).
    private bool _starLost;
    private double _lastPrimaryX;
    private double _lastPrimaryY;
    private bool _neuralHardDisabledLogged;

    /// <summary>Number of frames on which the tracker reported no star (lost lock), all-run.</summary>
    internal int StarLostEvents { get; private set; }

    /// <summary>
    /// Number of times the loop re-acquired a star after a loss, all-run. A high count during
    /// otherwise-quiet guiding means the lock is unstable (the "guide star keeps changing" symptom).
    /// </summary>
    internal int ReacquisitionEvents { get; private set; }

    /// <summary>
    /// Subset of <see cref="ReacquisitionEvents"/> where the re-acquired primary jumped at least
    /// <see cref="DifferentStarJumpPx"/> from the pre-loss primary -- i.e. the loop locked onto a
    /// DIFFERENT star, discarding the previous guide reference.
    /// </summary>
    internal int DifferentStarReacquisitions { get; private set; }

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
    /// Short-window total guide RMS (pixels) above which an engaged neural model is hard-disabled
    /// outright, independent of the performance monitor. The monitor compares neural error to the
    /// P-controller's single-frame predicted residual, which breaks down once errors are large
    /// (P's correction is pulse-capped, so its predicted residual looks as bad as the neural error,
    /// and the model is never flagged). A healthy lock sits at a couple of px; tens of px means the
    /// model is actively driving the star off, so revert to the pure P-controller immediately.
    /// Default 12px (~46" on a 3.8"/px guider) -- far above seeing/PE, far below a real divergence.
    /// </summary>
    public double NeuralDivergencePixels { get; set; } = 12.0;

    /// <summary>
    /// Short-window total guide RMS (pixels) that, sustained for <see cref="RecalibrationDivergenceFrames"/>
    /// consecutive frames, makes the loop give up and request a recalibration from the driver. This
    /// is catastrophic divergence the pure P-controller cannot recover from (a bad lock / stale
    /// calibration / a thrashing re-acquisition limit cycle) -- re-establishing a clean reference is
    /// the only fix. Set well above <see cref="NeuralDivergencePixels"/> so the neural kill-switch
    /// and P-controller recovery get their chance first.
    /// </summary>
    public double RecalibrationDivergencePixels { get; set; } = 25.0;

    /// <summary>
    /// Consecutive frames above <see cref="RecalibrationDivergencePixels"/> before the loop exits
    /// with <see cref="RecalibrationRequested"/>. A transient excursion the controller recovers from
    /// resets the counter; only a sustained divergence trips it.
    /// </summary>
    public int RecalibrationDivergenceFrames { get; set; } = 15;

    /// <summary>
    /// Set true (and the loop exits) when guiding has diverged beyond recovery and the driver should
    /// re-acquire + recalibrate. Distinct from cancellation: the loop returned on its own.
    /// </summary>
    internal bool RecalibrationRequested { get; private set; }

    private int _consecutiveDivergentFrames;

    /// <summary>
    /// Whether the guide loop is currently running.
    /// </summary>
    public bool IsGuiding => _isGuiding;

    /// <summary>
    /// Whether the neural model is currently contributing to corrections. Goes false once a model
    /// is hard-disabled (bounds breach, sustained underperformance, or a divergence kill-switch),
    /// so callers/tests can confirm the safety net engaged.
    /// </summary>
    internal bool IsNeuralActive => _useNeuralModel;

    /// <summary>
    /// The centroid tracker. Exposed for dither offset manipulation.
    /// </summary>
    internal GuiderCentroidTracker Tracker => _tracker;

    private int _statsResetRequested;

    /// <summary>
    /// Requests that the guide-error accumulators be reset at the start of the next loop
    /// iteration (thread-safe). The driver calls this when guiding actually begins (the
    /// Settling -&gt; Guiding transition) so the displayed RMS / Peak reflect guiding
    /// quality, not the calibration + settle transient. Performed on the loop thread to
    /// avoid racing the per-frame <c>Add</c>.
    /// </summary>
    internal void RequestErrorStatsReset() => Interlocked.Exchange(ref _statsResetRequested, 1);

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
        ITimeProvider timeProvider,
        ILogger? logger = null)
    {
        _pulseTarget = pulseTarget;
        _tracker = tracker;
        _pController = pController;
        _timeProvider = timeProvider;
        _logger = logger;
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
        _starLost = false;
        _neuralHardDisabledLogged = false;
        RecalibrationRequested = false;
        _consecutiveDivergentFrames = 0;
        StarLostEvents = 0;
        ReacquisitionEvents = 0;
        DifferentStarReacquisitions = 0;

        _logger?.LogInformation(
            "GuideLoop started: interval={IntervalSec:F1}s, neural={Neural} (blend={Blend:F2}), HA={HourAngle:F2}h, dec={Dec:F1}deg, RA rate={RaRate:F2}px/s, Dec rate={DecRate:F2}px/s.",
            exposureInterval.TotalSeconds, _useNeuralModel, NeuralBlendFactor, hourAngle, declination,
            calibration.RaRatePixPerSec, calibration.DecRatePixPerSec);

        var trainingRng = _experienceBuffer is not null ? new Random(42) : null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Honour a pending stats reset on the loop thread (set by the driver at
                // the Settling -> Guiding transition) so guide-quality stats start fresh.
                if (Interlocked.Exchange(ref _statsResetRequested, 0) == 1)
                {
                    _errorTracker.Reset();
                }

                var frameStart = GetTimestamp();

                // Capture and process frame — release previous frame's ChannelBuffer
                LastFrame?.Release();
                var frame = await captureFrame(cancellationToken);
                LastFrame = frame;
                var result = _tracker.ProcessFrame(frame.GetChannelArray(0));
                LastCentroidResult = result;

                if (result is null)
                {
                    // Star lost — wait and try again. Log only on the lost transition so a
                    // multi-frame outage produces one Warning, not a flood.
                    if (!_starLost)
                    {
                        _starLost = true;
                        StarLostEvents++;
                        _logger?.LogWarning(
                            "GuideLoop: guide star LOST at frame {Frame} (tracker SNR below floor or star left its search ROI). Last primary was ({X:F1},{Y:F1}); will re-acquire on the next usable frame.",
                            _guideFrameCount, _lastPrimaryX, _lastPrimaryY);
                    }
                    _hasPreviousError = false;
                    await _timeProvider.SleepAsync(exposureInterval, cancellationToken);
                    continue;
                }

                // Re-acquisition transition: the tracker reported a star again after a loss.
                // If the new primary jumped far from the pre-loss primary, the loop locked onto a
                // DIFFERENT star and discarded the old guide reference -- the discontinuity the user
                // sees as the "selected guide star changed" / Dec graph flip.
                if (_starLost)
                {
                    _starLost = false;
                    ReacquisitionEvents++;
                    var jump = Math.Sqrt(
                        (result.Value.X - _lastPrimaryX) * (result.Value.X - _lastPrimaryX) +
                        (result.Value.Y - _lastPrimaryY) * (result.Value.Y - _lastPrimaryY));
                    if (jump >= DifferentStarJumpPx)
                    {
                        DifferentStarReacquisitions++;
                        _logger?.LogWarning(
                            "GuideLoop: re-acquired a DIFFERENT guide star at frame {Frame} (primary jumped {Jump:F1}px: now ({X:F1},{Y:F1}), was ({X0:F1},{Y0:F1}), {Stars} star(s), SNR {Snr:F1}). The guide reference reset -- expect a one-frame error discontinuity.",
                            _guideFrameCount, jump, result.Value.X, result.Value.Y, _lastPrimaryX, _lastPrimaryY, result.Value.TrackedStarCount, result.Value.SNR);
                    }
                    else
                    {
                        _logger?.LogInformation(
                            "GuideLoop: recovered the guide star at frame {Frame} (primary back at ({X:F1},{Y:F1}), {Stars} star(s), SNR {Snr:F1}).",
                            _guideFrameCount, result.Value.X, result.Value.Y, result.Value.TrackedStarCount, result.Value.SNR);
                    }
                }
                _lastPrimaryX = result.Value.X;
                _lastPrimaryY = result.Value.Y;

                // Transform pixel error to mount axes
                var (raErrorPx, decErrorPx) = calibration.TransformToMountAxes(
                    result.Value.DeltaX, result.Value.DeltaY);

                var timestamp = GetTimestamp();
                _errorTracker.Add(timestamp, raErrorPx, decErrorPx);

                // Short-window total guide RMS, computed once for the safety nets below (only once
                // enough samples have accrued to be meaningful).
                var haveDivergenceSamples = _errorTracker.ShortWindowCount >= 10;
                var shortTotalRms = haveDivergenceSamples
                    ? Math.Sqrt(_errorTracker.RaRmsShort * _errorTracker.RaRmsShort +
                                _errorTracker.DecRmsShort * _errorTracker.DecRmsShort)
                    : 0.0;

                // Robust divergence kill-switch (independent of the perf monitor): if the short-window
                // guide RMS blows past a sane bound while the neural model is engaged, the model is
                // driving the star off -- hard-disable it now and finish on the pure P-controller.
                // Checked before computing this frame's correction so it already reverts to P.
                if (_useNeuralModel && haveDivergenceSamples && shortTotalRms > NeuralDivergencePixels)
                {
                    DisableNeuralModel();
                    if (!_neuralHardDisabledLogged)
                    {
                        _neuralHardDisabledLogged = true;
                        _logger?.LogWarning(
                            "GuideLoop: neural model HARD-DISABLED -- guiding diverged to {Rms:F1}px short-window RMS (> {Limit:F1}px) with the model engaged. Reverting to the pure P-controller for the rest of this run.",
                            shortTotalRms, NeuralDivergencePixels);
                    }
                }

                // Recalibration request: catastrophic divergence the controller cannot recover from
                // (a bad lock / stale calibration / a thrashing re-acquisition limit cycle), sustained
                // for several frames even after the neural net is gone. Give up and let the driver
                // re-acquire + recalibrate rather than limp on a broken reference. A transient that the
                // controller pulls back resets the counter.
                if (haveDivergenceSamples && shortTotalRms > RecalibrationDivergencePixels)
                {
                    if (++_consecutiveDivergentFrames >= RecalibrationDivergenceFrames)
                    {
                        RecalibrationRequested = true;
                        _logger?.LogWarning(
                            "GuideLoop: guiding diverged to {Rms:F0}px short-window RMS (> {Limit:F0}px) for {Frames} consecutive frames -- requesting recalibration to re-establish a clean lock.",
                            shortTotalRms, RecalibrationDivergencePixels, _consecutiveDivergentFrames);
                        break;
                    }
                }
                else
                {
                    _consecutiveDivergentFrames = 0;
                }

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

                // Throttled per-frame guide telemetry so the log shows what guiding is actually
                // doing (errors, corrections, controller mode, lock health) -- not just silence.
                if (_logger is not null && TelemetryLogEveryFrames > 0
                    && _guideFrameCount % TelemetryLogEveryFrames == 0)
                {
                    _logger.LogDebug(
                        "GuideLoop frame {Frame}: errRA={RaErr:F2}px errDec={DecErr:F2}px | corrRA={RaMs:F0}ms corrDec={DecMs:F0}ms ({Mode}) | SNR={Snr:F1} stars={Stars} | RMS(short) RA={RaRms:F2}px Dec={DecRms:F2}px | reacq={Reacq} diff-star={Diff} lost={Lost}",
                        _guideFrameCount, raErrorPx, decErrorPx,
                        correction.RaPulseMs, correction.DecPulseMs, usedNeural ? "neural" : "P",
                        result.Value.SNR, result.Value.TrackedStarCount,
                        _errorTracker.RaRmsShort, _errorTracker.DecRmsShort,
                        ReacquisitionEvents, DifferentStarReacquisitions, StarLostEvents);
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
                    await _timeProvider.SleepAsync(remaining, cancellationToken);
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

            _logger?.LogInformation(
                "GuideLoop stopped after {Frames} frames: RMS(all) RA={RaRms:F2}px Dec={DecRms:F2}px | re-acquisitions={Reacq} (different-star={Diff}), star-lost events={Lost}.",
                _guideFrameCount, _errorTracker.RaRmsAll, _errorTracker.DecRmsAll,
                ReacquisitionEvents, DifferentStarReacquisitions, StarLostEvents);

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
                _logger?.LogDebug(
                    "GuideLoop: neural output out of bounds (RA={RaMs:F0}ms Dec={DecMs:F0}ms exceed max {Max:F0}ms) -- fell back to P-controller ({Count}/{Limit}).",
                    raPulseMs, decPulseMs, MaxPulseMs, _consecutiveFallbacks, MaxConsecutiveFallbacks);
                if (_consecutiveFallbacks >= MaxConsecutiveFallbacks)
                {
                    DisableNeuralModel();
                    if (!_neuralHardDisabledLogged)
                    {
                        _neuralHardDisabledLogged = true;
                        _logger?.LogWarning(
                            "GuideLoop: neural model HARD-DISABLED after {Count} consecutive out-of-bounds outputs -- guiding on the P-controller only for the rest of this run.",
                            _consecutiveFallbacks);
                    }
                }
                return (pCorrection, false);
            }

            // Gate 2: performance monitor. A model that keeps underperforming the P-controller must
            // be DISABLED for the session, not retried every frame. Retrying makes it oscillate:
            // engage -> drift -> monitor flags it -> fall back to P -> P recovers -> the bad samples
            // age out of the monitor's window -> re-engage -> drift again. That oscillation is the
            // Dec "flip-flop" (each drift excursion pushes the star out of its ROI -> lost -> the
            // tracker re-acquires a different star). After MaxConsecutiveFallbacks consecutive
            // not-helping frames, hard-disable so guiding settles on the pure P-controller.
            if (_performanceMonitor is not null && !_performanceMonitor.IsNeuralModelHelping
                && _errorTracker.ShortWindowCount >= _performanceMonitor.MinSamples)
            {
                _consecutiveFallbacks++;
                if (_consecutiveFallbacks >= MaxConsecutiveFallbacks)
                {
                    DisableNeuralModel();
                    if (!_neuralHardDisabledLogged)
                    {
                        _neuralHardDisabledLogged = true;
                        _logger?.LogWarning(
                            "GuideLoop: neural model HARD-DISABLED after {Count} consecutive frames underperforming the P-controller (neural RMS {NeuralRms:F2}px vs P {PRms:F2}px) -- guiding on the P-controller only for the rest of this run.",
                            _consecutiveFallbacks, _performanceMonitor.NeuralRms, _performanceMonitor.PControllerRms);
                    }
                }
                else
                {
                    _logger?.LogDebug(
                        "GuideLoop: neural model not helping (neural RMS {NeuralRms:F2}px vs P {PRms:F2}px) -- fell back to P this frame ({Count}/{Limit}).",
                        _performanceMonitor.NeuralRms, _performanceMonitor.PControllerRms, _consecutiveFallbacks, MaxConsecutiveFallbacks);
                }
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
