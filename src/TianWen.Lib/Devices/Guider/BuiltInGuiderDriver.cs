using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Guider;

/// <summary>
/// Built-in guider driver that uses <see cref="GuideLoop"/> and <see cref="GuiderCalibration"/>
/// to perform autoguiding via an <see cref="ICameraDriver"/> and pulse guide corrections
/// routed through <see cref="PulseGuideRouter"/>.
/// </summary>
internal sealed class BuiltInGuiderDriver : IDeviceDependentGuider
{
    private readonly BuiltInGuiderDevice _device;
    private bool _connected;

    private IMountDriver? _mount;
    private ICameraDriver? _camera;
    private IPulseGuideTarget? _pulseTarget;

    private GuideLoop? _guideLoop;
    private CancellationTokenSource? _guideCts;
    private GuiderCalibrationResult? _lastCalibration;
    private PointingState? _calibrationPierSide;
    private volatile Image? _lastFrame;
    private volatile GuiderCentroidTracker? _calibrationTracker;

    /// <summary>
    /// When true (the default), the DEC guide direction is automatically reversed when
    /// a meridian flip is detected (hour angle sign change since calibration).
    /// Configured via the <c>reverseDecAfterFlip</c> query parameter on <see cref="BuiltInGuiderDevice"/>.
    /// </summary>
    internal bool ReverseDecOnFlip { get; set; }

    private int _state = (int)GuiderState.Idle;

    private double _settlePixels;
    private double _settleTime;
    private double _settleTimeout;
    private long _settleStartedTicks;
    // Overall settle-phase start, set once when entering Settling and NOT reset by excursions
    // (unlike _settleStartedTicks). Used by the neural settle fail-safe so a model that keeps
    // guiding just-above the settle threshold -- never settling, never diverging past the
    // divergence kill-switch -- still gets caught instead of resetting the timer forever.
    private long _settlingPhaseStartedTicks;
    // Number of large-excursion settle-clock resets within the current settle phase. The neural
    // fail-safe requires repeated resets (the perpetually-resetting signature of a misbehaving
    // model) before blaming the model -- a single reset is just as likely an external perturbation
    // (wind gust, scope bump) that the model should not be punished for.
    private int _settleExcursionResets;

    /// <summary>
    /// Minimum number of large-excursion settle resets within one settle phase before the neural
    /// fail-safe may fire. One excursion is indistinguishable from an external perturbation; two
    /// or more while the model is engaged is the model fighting the settle.
    /// </summary>
    private const int NeuralSettleFailSafeMinExcursions = 2;

    // Configuration — read from device URI query parameters. The advanced knobs (calibration
    // attempts/delay, settle fail-safe fraction) carry their defaults + rationale on the
    // corresponding BuiltInGuiderDevice properties.
    private readonly bool _reuseCalibration;
    private readonly bool _useNeuralGuider;
    private readonly double _neuralBlendFactor;
    private readonly int _maxCalibrationAttempts;
    private readonly int _maxRecalibrationAttempts;
    private readonly TimeSpan _calibrationRetryDelay;
    private readonly double _neuralSettleFailSafeFraction;

    private enum GuiderState
    {
        Idle = 0,
        Looping = 1,
        Calibrating = 2,
        Guiding = 3,
        Settling = 4,
    }

    public BuiltInGuiderDriver(BuiltInGuiderDevice device, IServiceProvider serviceProvider)
    {
        _device = device;
        External = serviceProvider.GetRequiredService<IExternal>();
        Logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(BuiltInGuiderDriver));
        TimeProvider = serviceProvider.GetRequiredService<ITimeProvider>();
        ReverseDecOnFlip = device.ReverseDecAfterFlip;
        _reuseCalibration = device.ReuseCalibration;
        _useNeuralGuider = device.UseNeuralGuider;
        _neuralBlendFactor = device.NeuralBlendFactor;
        _maxCalibrationAttempts = device.MaxCalibrationAttempts;
        _maxRecalibrationAttempts = device.MaxRecalibrationAttempts;
        _calibrationRetryDelay = device.CalibrationRetryDelay;
        _neuralSettleFailSafeFraction = device.NeuralSettleFailSafeFraction;
    }

    public string Name => _device.DisplayName;

    public string? Description => "Built-in guider using GuideLoop";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => Volatile.Read(ref _connected);

    public DeviceType DriverType => DeviceType.Guider;

    public IExternal External { get; }

    public ILogger Logger { get; }

    public ITimeProvider TimeProvider { get; }


    /// <summary>
    /// Last guide frame — from the guide loop when guiding, or from calibration/looping captures.
    /// </summary>
    public Image? LastGuideFrame => _guideLoop?.LastFrame ?? _lastFrame;

    /// <summary>Guide star position in frame pixels.</summary>
    public (double X, double Y)? GuideStarPosition =>
        (_guideLoop?.LastCentroidResult ?? _calibrationTracker?.LastResult) is { } r ? (r.X, r.Y) : null;

    /// <summary>Guide star SNR.</summary>
    public double? GuideStarSNR =>
        (_guideLoop?.LastCentroidResult ?? _calibrationTracker?.LastResult)?.SNR;

    /// <summary>
    /// Number of frames on which the guide loop lost the lock star (all-run). Surfaced for
    /// tests and UI so a lock that keeps dropping is observable, not silent.
    /// </summary>
    internal int GuideStarLostEvents => _guideLoop?.StarLostEvents ?? 0;

    /// <summary>Number of times the guide loop re-acquired a star after a loss (all-run).</summary>
    internal int GuideReacquisitionEvents => _guideLoop?.ReacquisitionEvents ?? 0;

    /// <summary>
    /// Re-acquisitions where the new primary was a DIFFERENT star (the lock swapped, discarding
    /// the guide reference) -- the "selected guide star changes" / Dec-graph-jump symptom.
    /// </summary>
    internal int GuideDifferentStarReacquisitions => _guideLoop?.DifferentStarReacquisitions ?? 0;

    /// <summary>
    /// Whether the neural model is still contributing. False once a hard-disable safety net fired
    /// (bounds, sustained underperformance, or divergence). Surfaced for tests.
    /// </summary>
    internal bool GuideNeuralActive => _guideLoop?.IsNeuralActive ?? false;

    /// <summary>Star profile: horizontal and vertical intensity cross-sections.</summary>
    public (float[] H, float[] V)? GuideStarProfile =>
        (_guideLoop?.LastCentroidResult ?? _calibrationTracker?.LastResult) is { HProfile: { } h, VProfile: { } v } ? (h, v) : null;

    /// <summary>Calibration overlay data (L-shaped per-step positions) for guide camera overlay.</summary>
    public CalibrationOverlayData? CalibrationOverlay =>
        _lastCalibration is { } cal && cal.Overlay is { } overlay
            ? overlay with
            {
                PixelScaleArcsec = GuiderPixelScale,
                RaRateArcsecPerSec = cal.RaRatePixPerSec * GuiderPixelScale,
                DecRateArcsecPerSec = cal.DecRatePixPerSec * GuiderPixelScale,
            }
            : null;

    /// <summary>
    /// The mount driver wired by <see cref="LinkDevices"/>.
    /// </summary>
    internal IMountDriver? MountDriver => _mount;

    /// <summary>
    /// The camera driver wired by <see cref="LinkDevices"/>.
    /// </summary>
    internal ICameraDriver? CameraDriver => _camera;

    public void LinkDevices(IMountDriver mount, ICameraDriver? camera)
    {
        _mount = mount;
        _camera = camera ?? throw new InvalidOperationException("Built-in guider requires a dedicated guider camera.");

        var pulseGuideSource = _device.PulseGuideSource;
        _pulseTarget = new PulseGuideRouter(pulseGuideSource, _camera, mount);
    }

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

#pragma warning disable CS0067 // Events required by IGuider interface
    public event EventHandler<GuidingErrorEventArgs>? GuidingErrorEvent;
    public event EventHandler<GuiderStateChangedEventArgs>? GuiderStateChangedEvent;
#pragma warning restore CS0067

    private GuiderState CurrentState => (GuiderState)Interlocked.CompareExchange(ref _state, 0, 0);

    private bool TryTransition(GuiderState from, GuiderState to)
    {
        var previous = (GuiderState)Interlocked.CompareExchange(ref _state, (int)to, (int)from);
        return previous == from;
    }

    private void ForceState(GuiderState to)
    {
        Interlocked.Exchange(ref _state, (int)to);
    }

    public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _connected, true);
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(true));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelGuideLoop();
        ForceState(GuiderState.Idle);
        Volatile.Write(ref _connected, false);
        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(false));
        return ValueTask.CompletedTask;
    }

    public ValueTask ConnectEquipmentAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask DisconnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        CancelGuideLoop();
        ForceState(GuiderState.Idle);
        return ValueTask.CompletedTask;
    }

    public ValueTask<(int Width, int Height)?> CameraFrameSizeAsync(CancellationToken cancellationToken = default)
    {
        if (_camera is { CameraXSize: > 0 } cam)
        {
            return ValueTask.FromResult<(int, int)?>((cam.CameraXSize, cam.CameraYSize));
        }
        return ValueTask.FromResult<(int, int)?>(null);
    }

    public ValueTask<TimeSpan> ExposureTimeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TimeSpan.FromSeconds(2));

    public ValueTask<string?> GetActiveProfileNameAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);

    public ValueTask<IReadOnlyList<string>> GetEquipmentProfilesAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<string>>([]);

    public ValueTask<SettleProgress?> GetSettleProgressAsync(CancellationToken cancellationToken = default)
    {
        var state = CurrentState;

        if (state is GuiderState.Settling or GuiderState.Calibrating)
        {
            // Compute actual error distance from the guide loop's error tracker (in pixels)
            var tracker = _guideLoop?.ErrorTracker;
            var distance = tracker is { LastRaError: { } ra, LastDecError: { } dec }
                ? Math.Sqrt(ra * ra + dec * dec)
                : 0.0;

            var elapsed = TimeProvider.GetElapsedTime(Interlocked.Read(ref _settleStartedTicks));

            return ValueTask.FromResult<SettleProgress?>(new SettleProgress
            {
                Done = false,
                Distance = distance,
                SettlePx = _settlePixels,
                Time = elapsed.TotalSeconds,
                SettleTime = _settleTime,
                Status = 0,
                StarLocked = tracker?.TotalSamples > 0,
            });
        }

        if (state is GuiderState.Guiding)
        {
            var tracker = _guideLoop?.ErrorTracker;
            var distance = tracker is { LastRaError: { } ra, LastDecError: { } dec }
                ? Math.Sqrt(ra * ra + dec * dec)
                : 0.0;

            return ValueTask.FromResult<SettleProgress?>(new SettleProgress
            {
                Done = true,
                Distance = distance,
                SettlePx = _settlePixels,
                Time = _settleTime,
                SettleTime = _settleTime,
                Status = 0,
                StarLocked = true,
            });
        }

        return ValueTask.FromResult<SettleProgress?>(null);
    }

    public ValueTask<GuideStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentState is not GuiderState.Guiding and not GuiderState.Settling)
        {
            return ValueTask.FromResult<GuideStats?>(null);
        }

        var tracker = _guideLoop?.ErrorTracker;
        var scale = GuiderPixelScale; // px → arcsec
        return ValueTask.FromResult<GuideStats?>(new GuideStats
        {
            // Recent rolling-window stats (NOT all-time): the panel must reflect CURRENT guide
            // quality like PHD2. An all-time accumulator never decays, so one early transient
            // (a calibration/settle excursion) poisoned the displayed RMS forever -- showing a
            // catastrophic Dec RMS (e.g. 427") while live guiding had recovered to arcsec level.
            // The scatter plot already shows per-sample reality; these numbers now match it.
            TotalRMS = (tracker?.TotalRmsShort ?? 0) * scale,
            RaRMS = (tracker?.RaRmsShort ?? 0) * scale,
            DecRMS = (tracker?.DecRmsShort ?? 0) * scale,
            PeakRa = (tracker?.PeakRaShort ?? 0) * scale,
            PeakDec = (tracker?.PeakDecShort ?? 0) * scale,
            LastRaErr = tracker?.LastRaError * scale,
            LastDecErr = tracker?.LastDecError * scale,
            LastRaPulseMs = _guideLoop?.LastCorrection?.RaPulseMs,
            LastDecPulseMs = _guideLoop?.LastCorrection?.DecPulseMs,
        });
    }

    public ValueTask<(string? AppState, double AvgDist)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = CurrentState;
        // PHD2 vocabulary: while the loop has no star lock, report "LostLock" instead of a
        // healthy-looking Guiding/Settling. Session logic already treats LostLock as
        // guiding-in-recovery (it must not restart the guider over a passing cloud), and the
        // UI uses it to show a "guide star lost" banner instead of a silent flatline.
        var appState = state switch
        {
            GuiderState.Idle => "Stopped",
            GuiderState.Looping => "Looping",
            GuiderState.Calibrating => "Calibrating",
            GuiderState.Guiding or GuiderState.Settling when _guideLoop?.IsStarLost == true => "LostLock",
            GuiderState.Guiding => "Guiding",
            GuiderState.Settling => "Settling",
            _ => "Unknown",
        };

        var avgDist = state is GuiderState.Guiding or GuiderState.Settling
            ? (_guideLoop?.ErrorTracker.TotalRmsAll ?? 0)
            : 0.0;
        return ValueTask.FromResult<(string?, double)>((appState, avgDist));
    }

    public ValueTask ClearCalibrationAsync(CancellationToken cancellationToken = default)
    {
        _lastCalibration = null;
        _calibrationPierSide = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask FlipCalibrationAsync(CancellationToken cancellationToken = default)
    {
        // Reverse DEC direction in existing calibration data
        if (_lastCalibration is { } cal)
        {
            _lastCalibration = cal with
            {
                DecRatePixPerSec = -cal.DecRatePixPerSec,
                DecDisplacementPx = -cal.DecDisplacementPx
            };
            // Toggle pier side so a subsequent auto-detect doesn't double-flip
            _calibrationPierSide = _calibrationPierSide?.Flipped;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask GuideAsync(double settlePixels, double settleTime, double settleTimeout, CancellationToken cancellationToken)
    {
        if (_pulseTarget is null || _camera is null || _mount is null)
        {
            throw new GuiderException("Built-in guider has not been linked to mount and camera. Call LinkDevices first.");
        }

        var current = CurrentState;
        if (current is not GuiderState.Idle and not GuiderState.Looping)
        {
            throw new GuiderException($"Cannot start guiding in state {current}");
        }

        _settlePixels = settlePixels;
        _settleTime = settleTime;
        _settleTimeout = settleTimeout;

        // Cancel any previous guide loop
        CancelGuideLoop();

        var cts = new CancellationTokenSource();
        _guideCts = cts;

        ForceState(GuiderState.Calibrating);

        // Start calibration + guide loop in the background
        _ = RunCalibrateAndGuideAsync(_pulseTarget, _camera, _mount, cts.Token);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Runs the calibration sequence: acquire guide star, pulse in RA/Dec to measure plate scale and angle.
    /// </summary>
    private async ValueTask<GuiderCalibrationResult?> CalibrateInternalAsync(IPulseGuideTarget pulseTarget, ICameraDriver camera, CancellationToken ct)
    {
        var exposureTime = await ExposureTimeAsync(ct);

        var tracker = new GuiderCentroidTracker(maxStars: 1) { AcquisitionEdgeMargin = GuideStarAcquisitionMargin() };
        _calibrationTracker = tracker;
        var frame = await CaptureGuideFrameAsync(camera, exposureTime, TimeProvider, External.ImageReadyPollInterval, ct);
        _lastFrame = frame;
        tracker.ProcessFrame(frame.GetChannelArray(0));

        if (!tracker.IsAcquired)
        {
            frame.Release();
            _lastFrame = null;
            // Return null without raising a guiding error -- the caller retries (a cloud / poor seeing
            // is often transient) and raises the terminal error only once all attempts are exhausted.
            Logger.LogWarning("Built-in guider: calibration could not acquire a guide star (cloud / poor seeing?).");
            return null;
        }

        var timeProvider = TimeProvider;
        var pollInterval = External.ImageReadyPollInterval;
        async ValueTask<Image> CaptureFrame(CancellationToken token)
        {
            var f = await CaptureGuideFrameAsync(camera, exposureTime, timeProvider, pollInterval, token);
            _lastFrame = f;
            return f;
        }

        var calibration = new GuiderCalibration
        {
            BacklashClearingEnabled = true,
            Logger = Logger,
        };

        return await calibration.CalibrateAsync(pulseTarget, tracker, CaptureFrame, TimeProvider, ct);
    }

    /// <summary>
    /// Calibrates with bounded retries. A failed attempt (no usable guide star, or a calibration the
    /// quality gates reject) is often transient -- a passing cloud, poor seeing -- so retry after a
    /// short wait that gives conditions time to clear, rather than abandoning the session on the
    /// first cloud. Returns null only after all attempts fail.
    /// </summary>
    private async ValueTask<GuiderCalibrationResult?> CalibrateWithRetryAsync(IPulseGuideTarget pulseTarget, ICameraDriver camera, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _maxCalibrationAttempts && !ct.IsCancellationRequested; attempt++)
        {
            var result = await CalibrateInternalAsync(pulseTarget, camera, ct);
            if (result is not null)
            {
                if (attempt > 1)
                {
                    Logger.LogInformation("Built-in guider: calibration succeeded on attempt {Attempt}/{Max}.", attempt, _maxCalibrationAttempts);
                }
                return result;
            }

            if (attempt < _maxCalibrationAttempts)
            {
                Logger.LogWarning(
                    "Built-in guider: calibration attempt {Attempt}/{Max} failed -- retrying in {Delay:F0}s (waiting for transient conditions like cloud to clear).",
                    attempt, _maxCalibrationAttempts, _calibrationRetryDelay.TotalSeconds);
                await TimeProvider.SleepAsync(_calibrationRetryDelay, ct);
            }
        }

        return null;
    }

    /// <summary>
    /// Attempts to load a saved calibration from disk and validate it with a quick pulse test.
    /// Returns the loaded calibration if valid, or null if no saved calibration exists or validation failed.
    /// </summary>
    private async ValueTask<GuiderCalibrationResult?> TryLoadAndValidateCalibrationAsync(
        IPulseGuideTarget pulseTarget, ICameraDriver camera, CancellationToken ct)
    {
        // Load the most recent neural model file — it contains the calibration result
        var model = new NeuralGuideModel();
        var savedCalibration = await NeuralGuideModelPersistence.TryLoadAsync(model, External.ProfileFolder, ct);
        if (savedCalibration is null)
        {
            return null;
        }

        Logger.LogInformation("Loaded saved calibration (angle={Angle:F1}°, RA rate={RaRate:F2} px/s). Validating...",
            savedCalibration.Value.CameraAngleDeg, savedCalibration.Value.RaRatePixPerSec);

        // Acquire a guide star for validation
        var exposureTime = await ExposureTimeAsync(ct);
        var tracker = new GuiderCentroidTracker(maxStars: 1) { AcquisitionEdgeMargin = GuideStarAcquisitionMargin() };
        _calibrationTracker = tracker;
        var frame = await CaptureGuideFrameAsync(camera, exposureTime, TimeProvider, External.ImageReadyPollInterval, ct);
        _lastFrame = frame;
        tracker.ProcessFrame(frame.GetChannelArray(0));

        if (!tracker.IsAcquired)
        {
            Logger.LogWarning("Cannot validate saved calibration — no guide star acquired.");
            return null;
        }

        var timeProvider = TimeProvider;
        var pollInterval = External.ImageReadyPollInterval;
        async ValueTask<Image> CaptureFrame(CancellationToken token)
        {
            var f = await CaptureGuideFrameAsync(camera, exposureTime, timeProvider, pollInterval, token);
            _lastFrame = f;
            return f;
        }

        var calibration = new GuiderCalibration();
        var result = await calibration.ValidateAsync(savedCalibration.Value, pulseTarget, tracker, CaptureFrame, TimeProvider, ct);

        switch (result)
        {
            case CalibrationValidationResult.Valid:
                Logger.LogInformation("Saved calibration validated — reusing.");
                _lastCalibration = savedCalibration;
                _calibrationPierSide = await _mount!.GetSideOfPierAsync(ct);
                return savedCalibration;

            case CalibrationValidationResult.RateDrifted:
                Logger.LogInformation("Saved calibration rate drifted — recalibrating (keeping neural weights).");
                return null;

            case CalibrationValidationResult.AngleChanged:
                Logger.LogInformation("Saved calibration angle changed — recalibrating (discarding neural weights).");
                return null;

            default:
                return null;
        }
    }

    private async Task RunCalibrateAndGuideAsync(IPulseGuideTarget pulseTarget, ICameraDriver camera, IMountDriver mount, CancellationToken ct)
    {
        try
        {
            var exposureTime = await ExposureTimeAsync(ct);

            // Reuse previous calibration if available, otherwise run a fresh calibration
            var calResult = _lastCalibration;
            if (calResult is null && _reuseCalibration)
            {
                // Try to load saved calibration from disk and validate with a quick pulse test
                calResult = await TryLoadAndValidateCalibrationAsync(pulseTarget, camera, ct);
                if (calResult is not null)
                {
                    // Record pier side at the time we load the calibration — this is our reference
                    // for detecting meridian flips later when guiding restarts after a slew.
                    _calibrationPierSide = await mount.GetSideOfPierAsync(ct);
                }
            }

            if (calResult is null)
            {
                calResult = await CalibrateWithRetryAsync(pulseTarget, camera, ct);
                if (calResult is null)
                {
                    GuidingErrorEvent?.Invoke(this, new GuidingErrorEventArgs(_device, "Calibration failed — no usable guide star after retries (cloud / poor seeing?) or insufficient star displacement"));
                    ForceState(GuiderState.Idle);
                    return;
                }

                _lastCalibration = calResult;
                _calibrationPierSide = await mount.GetSideOfPierAsync(ct);
            }
            else if (ReverseDecOnFlip)
            {
                // Detect meridian flip by checking if the mount's pier side changed since calibration.
                // HA sign alone is unreliable — slewing to a target on the other side of the meridian
                // changes HA sign without an actual GEM flip.
                var currentPierSide = await mount.GetSideOfPierAsync(ct);
                if (_calibrationPierSide is { } calPier && calPier != currentPierSide)
                {
                    var flipped = calResult.Value with
                    {
                        DecRatePixPerSec = -calResult.Value.DecRatePixPerSec,
                        DecDisplacementPx = -calResult.Value.DecDisplacementPx
                    };
                    calResult = flipped;
                    _lastCalibration = flipped;
                    _calibrationPierSide = currentPierSide;

                    Logger.LogInformation("Built-in guider: detected meridian flip (pier side {CalPier} → {CurPier}), reversed DEC direction.",
                        calPier, currentPierSide);
                }
            }

            var timeProvider = TimeProvider;
            var pollInterval = External.ImageReadyPollInterval;
            async ValueTask<Image> CaptureFrame(CancellationToken token)
                => await CaptureGuideFrameAsync(camera, exposureTime, timeProvider, pollInterval, token);

            // Guide -> (on divergence) recalibrate -> guide, bounded. Each pass re-acquires a fresh
            // guide star and rebuilds the loop. A neural model that drove the divergence is NOT
            // re-enabled on a recovery pass (it's the usual culprit), so recovery runs on pure P.
            var neuralAllowed = _useNeuralGuider;
            var recalibrationAttempts = 0;

            while (!ct.IsCancellationRequested)
            {
                // Acquire guide star and set lock position. A starless frame here (cloud at slew
                // completion, pointing way off after a GOTO) previously slipped through unchecked --
                // the loop then "guided" on nothing and the UI showed a healthy-looking flatline.
                // Bounded retries with the same backoff the calibration path uses; give up loudly
                // rather than guide without a lock.
                var tracker = new GuiderCentroidTracker(maxStars: 1) { AcquisitionEdgeMargin = GuideStarAcquisitionMargin() };
                var acquired = false;
                for (var attempt = 1; attempt <= _maxCalibrationAttempts && !ct.IsCancellationRequested; attempt++)
                {
                    var frame = await CaptureGuideFrameAsync(camera, exposureTime, TimeProvider, External.ImageReadyPollInterval, ct);
                    tracker.ProcessFrame(frame.GetChannelArray(0));
                    if (tracker.IsAcquired)
                    {
                        acquired = true;
                        _lastFrame = frame;
                        break;
                    }

                    frame.Release();
                    Logger.LogWarning(
                        "Built-in guider: no guide star acquired for guiding (attempt {Attempt}/{Max}; cloud / poor seeing?), retrying in {Delay:F0}s.",
                        attempt, _maxCalibrationAttempts, _calibrationRetryDelay.TotalSeconds);
                    await TimeProvider.SleepAsync(_calibrationRetryDelay, ct);
                }

                if (!acquired)
                {
                    GuidingErrorEvent?.Invoke(this, new GuidingErrorEventArgs(_device,
                        $"Cannot start guiding — no usable guide star after {_maxCalibrationAttempts} attempts (cloud / poor seeing?)"));
                    return; // finally resets to Idle
                }

                tracker.SetLockPosition();

                // Build guide loop
                var pController = new ProportionalGuideController
                {
                    AggressivenessRa = 0.7,
                    AggressivenessDec = 0.7,
                    MinPulseMs = 5
                };

                var guideLoop = new GuideLoop(pulseTarget, tracker, pController, TimeProvider, Logger);
                guideLoop.SetCalibration(calResult.Value);

                // Enable neural guide model with online learning if configured (and not disabled
                // after a divergence-triggered recalibration -- a suspect model must not be reloaded).
                if (neuralAllowed)
                {
                    var model = new NeuralGuideModel();
                    var loaded = await NeuralGuideModelPersistence.TryLoadAsync(model, External.ProfileFolder, ct);
                    if (loaded is null)
                    {
                        model.InitializeRandom(42);
                    }
                    guideLoop.NeuralBlendFactor = _neuralBlendFactor;
                    guideLoop.EnableNeuralModel(model);
                    guideLoop.EnableOnlineLearning(profileFolder: External.ProfileFolder);
                    Logger.LogInformation(
                        "Neural guide enabled (blend={Blend}, {Status})",
                        _neuralBlendFactor, loaded is not null ? "loaded from disk" : "fresh model");
                }

                _guideLoop = guideLoop;
                _calibrationTracker = null; // guide loop owns tracking now

                // Query mount for neural model features
                var declination = await mount.GetDeclinationAsync(ct);
                var ra = await mount.GetRightAscensionAsync(ct);
                var siteLatitude = await mount.GetSiteLatitudeAsync(ct);
                var siderealTime = await mount.GetSiderealTimeAsync(ct);
                var hourAngle = siderealTime - ra;

                // Probe mount for encoder position support (for predictive PEC)
                Func<TelescopeAxis, CancellationToken, ValueTask<long?>>? getAxisPosition = null;
                uint wormStepsRa = 0, wormStepsDec = 0;
                var testPos = await mount.GetAxisPositionAsync(TelescopeAxis.Primary, ct);
                if (testPos is not null)
                {
                    getAxisPosition = mount.GetAxisPositionAsync;
                    wormStepsRa = await mount.GetWormPeriodStepsAsync(TelescopeAxis.Primary, ct);
                    wormStepsDec = await mount.GetWormPeriodStepsAsync(TelescopeAxis.Seconary, ct);
                }

                // Transition: Calibrating → Settling → Guiding. The phase timestamp must be
                // recorded BEFORE the state becomes visible: a concurrent IsGuidingAsync /
                // IsSettlingAsync poll that observes Settling with a zero/stale phase start would
                // compute a huge settling duration and trip the neural fail-safe instantly.
                RecordSettlePhaseStart();
                ForceState(GuiderState.Settling);

                // Run the guide loop (returns on cancellation or a recalibration request)
                await guideLoop.RunAsync(CaptureFrame, exposureTime, hourAngle, declination, siteLatitude,
                    getAxisPosition, wormStepsRa, wormStepsDec, ct);

                if (ct.IsCancellationRequested || !guideLoop.RecalibrationRequested)
                {
                    break; // normal stop
                }

                // Guiding diverged beyond recovery -- re-acquire + recalibrate to re-establish a clean
                // reference rather than limp on a broken lock. Bounded attempts.
                if (++recalibrationAttempts > _maxRecalibrationAttempts)
                {
                    GuidingErrorEvent?.Invoke(this, new GuidingErrorEventArgs(_device,
                        $"Guiding diverged repeatedly; {_maxRecalibrationAttempts} recalibration attempts did not recover"));
                    return; // finally resets to Idle
                }

                neuralAllowed = false;
                Logger.LogWarning(
                    "Built-in guider: guiding diverged -- recalibrating to re-establish a clean lock (attempt {Attempt}/{Max}; neural off for the rest of the session).",
                    recalibrationAttempts, _maxRecalibrationAttempts);
                ForceState(GuiderState.Calibrating);
                _lastCalibration = null;
                var fresh = await CalibrateWithRetryAsync(pulseTarget, camera, ct);
                if (fresh is null)
                {
                    GuidingErrorEvent?.Invoke(this, new GuidingErrorEventArgs(_device,
                        "Recalibration failed — no usable guide star after retries"));
                    return;
                }
                calResult = fresh;
                _lastCalibration = fresh;
                _calibrationPierSide = await mount.GetSideOfPierAsync(ct);
                // loop back -> re-acquire + rebuild loop with the fresh calibration
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on StopCaptureAsync
        }
        catch (Exception ex)
        {
            GuidingErrorEvent?.Invoke(this, new GuidingErrorEventArgs(_device,ex.Message));
        }
        finally
        {
            _guideLoop = null;
            ForceState(GuiderState.Idle);
        }
    }

    /// <summary>
    /// Captures a single guide frame: start exposure, poll until ready, return pixel data.
    /// </summary>
    /// <summary>
    /// Captures a single guide frame via <see cref="ICameraDriver.GetImageAsync"/>.
    /// The returned <see cref="Image"/> holds a <see cref="ChannelBuffer"/> ref —
    /// caller must call <see cref="Image.Release"/> when done to allow buffer reuse.
    /// </summary>
    internal static async ValueTask<Image> CaptureGuideFrameAsync(ICameraDriver camera, TimeSpan exposure, ITimeProvider timeProvider, TimeSpan imageReadyPollInterval, CancellationToken ct)
    {
        await camera.StartExposureAsync(exposure, FrameType.Light, ct);

        // Poll until image is ready
        while (!await camera.GetImageReadyAsync(ct))
        {
            await timeProvider.SleepAsync(imageReadyPollInterval, ct);
        }

        return await camera.GetImageAsync(ct) ?? throw new GuiderException("Failed to capture guide frame — no image data");
    }

    private static readonly Random _ditherRng = new Random();

    public ValueTask DitherAsync(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false, CancellationToken cancellationToken = default)
    {
        var current = CurrentState;
        if (current is not GuiderState.Guiding)
        {
            throw new GuiderException($"Cannot dither in state {current}");
        }

        // Offset the guide star lock position by a random amount up to ditherPixels.
        // The guide loop naturally corrects the star back to the new lock position,
        // creating the dither offset on the imaging camera.
        if (_guideLoop is { } loop)
        {
            var angle = _ditherRng.NextDouble() * 2 * Math.PI;
            var distance = ditherPixels * (0.5 + 0.5 * _ditherRng.NextDouble()); // 50-100% of requested
            var dx = distance * Math.Cos(angle);
            var dy = raOnly ? 0 : distance * Math.Sin(angle);
            loop.Tracker.OffsetLockPosition(dx, dy);
        }

        _settlePixels = settlePixels;
        _settleTime = settleTime;
        _settleTimeout = settleTimeout;

        ForceState(GuiderState.Settling);
        RecordSettlePhaseStart();

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsGuidingAsync(CancellationToken cancellationToken = default)
    {
        TryCompleteSettle();
        return ValueTask.FromResult(CurrentState is GuiderState.Guiding);
    }

    public ValueTask<bool> IsLoopingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Looping or GuiderState.Guiding or GuiderState.Calibrating or GuiderState.Settling);

    public ValueTask<bool> IsSettlingAsync(CancellationToken cancellationToken = default)
    {
        TryCompleteSettle();
        return ValueTask.FromResult(CurrentState is GuiderState.Settling or GuiderState.Calibrating);
    }

    public ValueTask<bool> LoopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (CurrentState is GuiderState.Idle)
        {
            ForceState(GuiderState.Looping);
        }
        return ValueTask.FromResult(true);
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask<double> PixelScaleAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(GuiderPixelScale);
    }

    /// <summary>
    /// Guider pixel scale in arcsec/px, computed from camera pixel size and focal length.
    /// Returns 1.0 as fallback if either is unavailable (so pixel values pass through as-is).
    /// </summary>
    private double GuiderPixelScale =>
        _camera is { PixelSizeX: > 0 and var px, FocalLength: > 0 and var fl }
            ? Astrometry.CoordinateUtils.PixelScaleArcsec(px, fl)
            : 1.0;

    public ValueTask<string?> SaveImageAsync(string outputFolder, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);

    public ValueTask StopCaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        CancelGuideLoop();
        ForceState(GuiderState.Idle);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnpauseAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    private void RecordSettleStart()
    {
        Interlocked.Exchange(ref _settleStartedTicks, TimeProvider.GetTimestamp());
    }

    /// <summary>
    /// Marks the start of a settle phase (entering Settling). Resets both the "stable for settleTime"
    /// timer and the overall settle-phase timer used by the neural fail-safe. Excursion resets call
    /// <see cref="RecordSettleStart"/> instead, which only resets the former.
    /// </summary>
    private void RecordSettlePhaseStart()
    {
        var now = TimeProvider.GetTimestamp();
        Interlocked.Exchange(ref _settleStartedTicks, now);
        Interlocked.Exchange(ref _settlingPhaseStartedTicks, now);
        Interlocked.Exchange(ref _settleExcursionResets, 0);
    }

    /// <summary>
    /// Checks whether the guide error has been below the settle threshold for the required settle time.
    /// If so, transitions from Settling to Guiding.
    /// </summary>
    private bool TryCompleteSettle()
    {
        if (CurrentState is not GuiderState.Settling)
        {
            return false;
        }

        // Neural settle fail-safe: if settling drags past a fraction of the settle timeout with the
        // neural model still engaged, the model is the likely reason it will not settle (it keeps
        // guiding just above the settle threshold -- never settling, never diverging far enough to
        // trip the in-loop divergence kill-switch). Disable it and restart the settle window so the
        // proven P-controller gets a clean attempt within the remaining budget. Uses the phase timer
        // (not reset by excursions) so a perpetually-resetting settle still gets caught -- but only
        // after repeated excursion resets, so a single external perturbation (wind gust, scope bump)
        // that stretches the phase past the threshold is not blamed on the model.
        if (_settleTimeout > 0 && _guideLoop is { IsNeuralActive: true } neuralLoop)
        {
            var settlingFor = TimeProvider.GetElapsedTime(Interlocked.Read(ref _settlingPhaseStartedTicks));
            if (settlingFor.TotalSeconds >= _settleTimeout * _neuralSettleFailSafeFraction &&
                Volatile.Read(ref _settleExcursionResets) >= NeuralSettleFailSafeMinExcursions)
            {
                neuralLoop.DisableNeuralModel();
                Logger.LogWarning(
                    "Built-in guider: still settling after {Elapsed:F0}s ({Fraction:P0} of the {Timeout:F0}s settle timeout) with the neural model engaged -- disabling neural and restarting the settle window on the P-controller.",
                    settlingFor.TotalSeconds, _neuralSettleFailSafeFraction, _settleTimeout);
                RecordSettleStart(); // fresh settle attempt for the P-controller within the remaining budget
            }
        }

        var elapsed = TimeProvider.GetElapsedTime(Interlocked.Read(ref _settleStartedTicks));

        // Check actual error distance (in pixels) against the settle threshold
        var tracker = _guideLoop?.ErrorTracker;
        if (tracker is { LastRaError: { } ra, LastDecError: { } dec })
        {
            var distance = Math.Sqrt(ra * ra + dec * dec);
            if (distance > _settlePixels * 3)
            {
                // Large excursion — reset the settle timer completely
                Interlocked.Increment(ref _settleExcursionResets);
                RecordSettleStart();
                return false;
            }
        }

        if (elapsed.TotalSeconds >= _settleTime)
        {
            if (TryTransition(GuiderState.Settling, GuiderState.Guiding))
            {
                // Start guide-quality stats fresh as guiding begins, so the displayed
                // RMS / Peak reflect guiding -- not the calibration + settle transient
                // (matches PHD2, which only shows the guide graph once guiding starts).
                _guideLoop?.RequestErrorStatsReset();
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Edge margin (px) to keep an acquired guide star clear of the frame border so it
    /// survives the calibration throw (backlash clearing + measurement sweep, one
    /// direction) instead of leaving the sensor and forcing a re-lock onto a different
    /// star. Derived from the standard 0.5x sidereal guide rate against the guide pixel
    /// scale; capped to a third of the (binned) frame so small sub-frames still acquire.
    /// </summary>
    private int GuideStarAcquisitionMargin()
    {
        const double halfSiderealArcsecPerSec = 15.041 * 0.5;
        var scale = GuiderPixelScale; // arcsec/px (1.0 fallback)
        var ratePxPerSec = scale > 0 ? halfSiderealArcsecPerSec / scale : 2.0;
        // Same calibration geometry the driver runs (defaults + backlash clearing on).
        var throwPx = new GuiderCalibration { BacklashClearingEnabled = true }
            .ExpectedSweepThrowPixels(ratePxPerSec) + 16.0; // + search-radius tracking headroom
        var frameMin = _camera is { Connected: true } cam ? Math.Min(cam.NumX, cam.NumY) : 0;
        var cap = frameMin > 0 ? frameMin / 3 : int.MaxValue;
        return Math.Clamp((int)Math.Ceiling(throwPx), 0, cap);
    }

    private void CancelGuideLoop()
    {
        var cts = Interlocked.Exchange(ref _guideCts, null);
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    public void Dispose()
    {
        CancelGuideLoop();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
