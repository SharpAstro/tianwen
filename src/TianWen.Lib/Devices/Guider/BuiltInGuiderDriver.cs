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
    private long _settleStartedTicks;

    // Configuration — read from device URI query parameters
    private readonly bool _reuseCalibration;
    private readonly bool _useNeuralGuider;
    private readonly double _neuralBlendFactor;

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
        TimeProvider = serviceProvider.GetRequiredService<TimeProvider>();
        ReverseDecOnFlip = device.ReverseDecAfterFlip;
        _reuseCalibration = device.ReuseCalibration;
        _useNeuralGuider = device.UseNeuralGuider;
        _neuralBlendFactor = device.NeuralBlendFactor;
    }

    public string Name => _device.DisplayName;

    public string? Description => "Built-in guider using GuideLoop";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => Volatile.Read(ref _connected);

    public DeviceType DriverType => DeviceType.Guider;

    public IExternal External { get; }

    public ILogger Logger { get; }

    public TimeProvider TimeProvider { get; }


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

            var elapsed = TimeProvider.GetElapsedTime(_settleStartedTicks);

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
            TotalRMS = (tracker?.TotalRmsAll ?? 0) * scale,
            RaRMS = (tracker?.RaRmsAll ?? 0) * scale,
            DecRMS = (tracker?.DecRmsAll ?? 0) * scale,
            PeakRa = (tracker?.PeakRa ?? 0) * scale,
            PeakDec = (tracker?.PeakDec ?? 0) * scale,
            LastRaErr = tracker?.LastRaError * scale,
            LastDecErr = tracker?.LastDecError * scale,
            LastRaPulseMs = _guideLoop?.LastCorrection?.RaPulseMs,
            LastDecPulseMs = _guideLoop?.LastCorrection?.DecPulseMs,
        });
    }

    public ValueTask<(string? AppState, double AvgDist)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var state = CurrentState;
        var appState = state switch
        {
            GuiderState.Idle => "Stopped",
            GuiderState.Looping => "Looping",
            GuiderState.Calibrating => "Calibrating",
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

        var tracker = new GuiderCentroidTracker(maxStars: 1);
        _calibrationTracker = tracker;
        var frame = await CaptureGuideFrameAsync(camera, exposureTime, External, ct);
        _lastFrame = frame;
        tracker.ProcessFrame(frame.GetChannelArray(0));

        if (!tracker.IsAcquired)
        {
            frame.Release();
            _lastFrame = null;
            GuidingErrorEvent?.Invoke(this, new GuidingErrorEventArgs(_device, "Failed to acquire guide star"));
            return null;
        }

        var timeProvider = TimeProvider;
        async ValueTask<Image> CaptureFrame(CancellationToken token)
        {
            var f = await CaptureGuideFrameAsync(camera, exposureTime, External, token);
            _lastFrame = f;
            return f;
        }

        var calibration = new GuiderCalibration
        {
            BacklashClearingEnabled = true,
        };

        return await calibration.CalibrateAsync(pulseTarget, tracker, CaptureFrame, External, ct);
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
        var tracker = new GuiderCentroidTracker(maxStars: 1);
        _calibrationTracker = tracker;
        var frame = await CaptureGuideFrameAsync(camera, exposureTime, External, ct);
        _lastFrame = frame;
        tracker.ProcessFrame(frame.GetChannelArray(0));

        if (!tracker.IsAcquired)
        {
            Logger.LogWarning("Cannot validate saved calibration — no guide star acquired.");
            return null;
        }

        var timeProvider = TimeProvider;
        async ValueTask<Image> CaptureFrame(CancellationToken token)
        {
            var f = await CaptureGuideFrameAsync(camera, exposureTime, External, token);
            _lastFrame = f;
            return f;
        }

        var calibration = new GuiderCalibration();
        var result = await calibration.ValidateAsync(savedCalibration.Value, pulseTarget, tracker, CaptureFrame, External, ct);

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
                calResult = await CalibrateInternalAsync(pulseTarget, camera, ct);
                if (calResult is null)
                {
                    GuidingErrorEvent?.Invoke(this, new GuidingErrorEventArgs(_device, "Calibration failed — insufficient star displacement"));
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

            // Acquire guide star and set lock position
            var tracker = new GuiderCentroidTracker(maxStars: 1);
            var timeProvider = TimeProvider;
            async ValueTask<Image> CaptureFrame(CancellationToken token)
                => await CaptureGuideFrameAsync(camera, exposureTime, External, token);

            var frame = await CaptureGuideFrameAsync(camera, exposureTime, External, ct);
            tracker.ProcessFrame(frame.GetChannelArray(0));
            tracker.SetLockPosition();

            // Build guide loop
            var pController = new ProportionalGuideController
            {
                AggressivenessRa = 0.7,
                AggressivenessDec = 0.7,
                MinPulseMs = 5
            };

            var guideLoop = new GuideLoop(pulseTarget, tracker, pController, External, TimeProvider);
            guideLoop.SetCalibration(calResult.Value);

            // Enable neural guide model with online learning if configured
            if (_useNeuralGuider)
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

            // Transition: Calibrating → Settling → Guiding
            ForceState(GuiderState.Settling);
            RecordSettleStart();

            // Run the guide loop (blocks until cancelled)
            await guideLoop.RunAsync(CaptureFrame, exposureTime, hourAngle, declination, siteLatitude,
                getAxisPosition, wormStepsRa, wormStepsDec, ct);
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
    internal static async ValueTask<Image> CaptureGuideFrameAsync(ICameraDriver camera, TimeSpan exposure, IExternal external, CancellationToken ct)
    {
        await camera.StartExposureAsync(exposure, FrameType.Light, ct);

        // Poll until image is ready
        while (!await camera.GetImageReadyAsync(ct))
        {
            await external.SleepAsync(TimeSpan.FromMilliseconds(50), ct);
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

        ForceState(GuiderState.Settling);
        RecordSettleStart();

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
    /// Checks whether the guide error has been below the settle threshold for the required settle time.
    /// If so, transitions from Settling to Guiding.
    /// </summary>
    private bool TryCompleteSettle()
    {
        if (CurrentState is not GuiderState.Settling)
        {
            return false;
        }

        var elapsed = TimeProvider.GetElapsedTime(_settleStartedTicks);

        // Check actual error distance (in pixels) against the settle threshold
        var tracker = _guideLoop?.ErrorTracker;
        if (tracker is { LastRaError: { } ra, LastDecError: { } dec })
        {
            var distance = Math.Sqrt(ra * ra + dec * dec);
            if (distance > _settlePixels * 3)
            {
                // Large excursion — reset the settle timer completely
                RecordSettleStart();
                return false;
            }
        }

        if (elapsed.TotalSeconds >= _settleTime)
        {
            TryTransition(GuiderState.Settling, GuiderState.Guiding);
            return true;
        }

        return false;
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
