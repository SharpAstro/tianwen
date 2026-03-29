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
    private double _calibrationHourAngle = double.NaN;

    /// <summary>
    /// When true (the default), the DEC guide direction is automatically reversed when
    /// a meridian flip is detected (hour angle sign change since calibration).
    /// Configured via the <c>reverseDecAfterFlip</c> query parameter on <see cref="BuiltInGuiderDevice"/>.
    /// </summary>
    internal bool ReverseDecOnFlip { get; set; }

    private int _state = (int)GuiderState.Idle;

    private double _settlePixels;
    private double _settleTime;
    private ITimer? _settleTimer;

    private enum GuiderState
    {
        Idle = 0,
        Looping = 1,
        Calibrating = 2,
        Guiding = 3,
        Settling = 4,
    }

    public BuiltInGuiderDriver(BuiltInGuiderDevice device, IExternal external)
    {
        _device = device;
        External = external;
        ReverseDecOnFlip = device.ReverseDecAfterFlip;
    }

    public string Name => _device.DisplayName;

    public string? Description => "Built-in guider using GuideLoop";

    public string? DriverInfo => Description;

    public string? DriverVersion => typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "1.0";

    public bool Connected => Volatile.Read(ref _connected);

    public DeviceType DriverType => DeviceType.Guider;

    public IExternal External { get; }


    /// <summary>
    /// Last guide frame — from the guide loop when guiding.
    /// </summary>
    public Image? LastGuideFrame => _guideLoop?.LastFrame;

    /// <summary>Guide star position in frame pixels.</summary>
    public (double X, double Y)? GuideStarPosition =>
        _guideLoop?.LastCentroidResult is { } r ? (r.X, r.Y) : null;

    /// <summary>Guide star SNR.</summary>
    public double? GuideStarSNR =>
        _guideLoop?.LastCentroidResult?.SNR;

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
            return ValueTask.FromResult<SettleProgress?>(new SettleProgress
            {
                Done = false,
                Distance = 1.0,
                SettlePx = _settlePixels,
                Time = 0,
                SettleTime = _settleTime,
                Status = 0,
                StarLocked = true,
            });
        }

        if (state is GuiderState.Guiding)
        {
            return ValueTask.FromResult<SettleProgress?>(new SettleProgress
            {
                Done = true,
                Distance = 0.1,
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
        return ValueTask.FromResult<GuideStats?>(new GuideStats
        {
            TotalRMS = tracker?.TotalRmsAll ?? 0,
            RaRMS = tracker?.RaRmsAll ?? 0,
            DecRMS = tracker?.DecRmsAll ?? 0,
            PeakRa = (tracker?.RaRmsAll ?? 0) * 2,
            PeakDec = (tracker?.DecRmsAll ?? 0) * 2,
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
        _calibrationHourAngle = double.NaN;
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
            _calibrationHourAngle = -_calibrationHourAngle;
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
        var frame = await CaptureGuideFrameAsync(camera, exposureTime, External, ct);
        tracker.ProcessFrame(frame.GetChannelArray(0));

        if (!tracker.IsAcquired)
        {
            frame.Release();
            GuidingErrorEvent?.Invoke(this, new GuidingErrorEventArgs(_device, "Failed to acquire guide star"));
            return null;
        }

        var ext = External;
        async ValueTask<Image> CaptureFrame(CancellationToken token)
            => await CaptureGuideFrameAsync(camera, exposureTime, ext, token);

        var calibration = new GuiderCalibration
        {
            CalibrationPulseDuration = TimeSpan.FromSeconds(1),
            CalibrationSteps = 3,
            BacklashClearingEnabled = true,
            MaxBacklashClearingSteps = 10,
            BacklashMovementThresholdPx = 1.5
        };

        return await calibration.CalibrateAsync(pulseTarget, tracker, CaptureFrame, External, ct);
    }

    private async Task RunCalibrateAndGuideAsync(IPulseGuideTarget pulseTarget, ICameraDriver camera, IMountDriver mount, CancellationToken ct)
    {
        try
        {
            var exposureTime = await ExposureTimeAsync(ct);

            // Reuse previous calibration if available, otherwise run a fresh calibration
            var calResult = _lastCalibration;
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
                _calibrationHourAngle = await mount.GetSiderealTimeAsync(ct) - await mount.GetRightAscensionAsync(ct);
            }
            else if (ReverseDecOnFlip && !double.IsNaN(_calibrationHourAngle))
            {
                // Detect meridian flip: HA sign changed since calibration
                var currentHA = await mount.GetSiderealTimeAsync(ct) - await mount.GetRightAscensionAsync(ct);
                if (Math.Sign(_calibrationHourAngle) != Math.Sign(currentHA) && _calibrationHourAngle != 0 && currentHA != 0)
                {
                    // Reverse DEC direction by negating DecRatePixPerSec and DecDisplacementPx
                    var flipped = calResult.Value with
                    {
                        DecRatePixPerSec = -calResult.Value.DecRatePixPerSec,
                        DecDisplacementPx = -calResult.Value.DecDisplacementPx
                    };
                    calResult = flipped;
                    _lastCalibration = flipped;
                    _calibrationHourAngle = currentHA;

                    External.AppLogger.LogInformation("Built-in guider: detected meridian flip (calibration HA={CalHA:F3} → current HA={CurHA:F3}), reversed DEC direction.",
                        _calibrationHourAngle, currentHA);
                }
            }

            // Acquire guide star and set lock position
            var tracker = new GuiderCentroidTracker(maxStars: 1);
            var ext = External;
            async ValueTask<Image> CaptureFrame(CancellationToken token)
                => await CaptureGuideFrameAsync(camera, exposureTime, ext, token);

            var frame = await CaptureGuideFrameAsync(camera, exposureTime, External, ct);
            tracker.ProcessFrame(frame.GetChannelArray(0));
            tracker.SetLockPosition();

            // Build guide loop
            var pController = new ProportionalGuideController
            {
                AggressivenessRa = 0.7,
                AggressivenessDec = 0.7,
                MinPulseMs = 20
            };

            var guideLoop = new GuideLoop(pulseTarget, tracker, pController, External);
            guideLoop.SetCalibration(calResult.Value);
            _guideLoop = guideLoop;

            // Query mount for neural model features
            var declination = await mount.GetDeclinationAsync(ct);
            var ra = await mount.GetRightAscensionAsync(ct);
            var siteLatitude = await mount.GetSiteLatitudeAsync(ct);
            var siderealTime = await mount.GetSiderealTimeAsync(ct);
            var hourAngle = siderealTime - ra;

            // Transition: Calibrating → Settling → Guiding
            ForceState(GuiderState.Settling);
            StartSettleTimer(_settleTime);

            // Run the guide loop (blocks until cancelled)
            await guideLoop.RunAsync(CaptureFrame, exposureTime, hourAngle, declination, siteLatitude, ct);
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

    public ValueTask DitherAsync(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false, CancellationToken cancellationToken = default)
    {
        var current = CurrentState;
        if (current is not GuiderState.Guiding)
        {
            throw new GuiderException($"Cannot dither in state {current}");
        }

        _settlePixels = settlePixels;
        _settleTime = settleTime;

        ForceState(GuiderState.Settling);
        StartSettleTimer(settleTime);

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsGuidingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Guiding);

    public ValueTask<bool> IsLoopingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Looping or GuiderState.Guiding or GuiderState.Calibrating or GuiderState.Settling);

    public ValueTask<bool> IsSettlingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Settling or GuiderState.Calibrating);

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
        if (_camera is { PixelSizeX: > 0 } cam)
        {
            return ValueTask.FromResult(cam.PixelSizeX);
        }
        return ValueTask.FromResult(1.0);
    }

    public ValueTask<string?> SaveImageAsync(string outputFolder, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);

    public ValueTask StopCaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        CancelGuideLoop();
        Interlocked.Exchange(ref _settleTimer, null)?.Dispose();
        ForceState(GuiderState.Idle);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnpauseAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    private void StartSettleTimer(double settleTimeSeconds)
    {
        Interlocked.Exchange(ref _settleTimer, null)?.Dispose();
        _settleTimer = External.TimeProvider.CreateTimer(
            _ => OnSettleComplete(),
            null,
            TimeSpan.FromSeconds(settleTimeSeconds),
            Timeout.InfiniteTimeSpan);
    }

    private void OnSettleComplete()
    {
        TryTransition(GuiderState.Settling, GuiderState.Guiding);
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
        Interlocked.Exchange(ref _settleTimer, null)?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
