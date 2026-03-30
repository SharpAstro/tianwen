using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Fake;

internal class FakeGuider(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IDeviceDependentGuider
{

    private const double DefaultPixelScale = 1.5;

    /// <summary>
    /// Mount driver for reading current RA/Dec. Set via <see cref="LinkDevices"/>.
    /// </summary>
    private IMountDriver? _mount;

    /// <summary>
    /// Current pointing RA in hours (J2000). Set by the test or read from the mount driver.
    /// </summary>
    internal double PointingRA { get; set; } = double.NaN;

    /// <summary>
    /// Current pointing Dec in degrees (J2000). Set by the test or read from the mount driver.
    /// </summary>
    internal double PointingDec { get; set; } = double.NaN;

    /// <summary>
    /// Guider camera for reading sensor dimensions. Set via <see cref="LinkDevices"/>.
    /// </summary>
    private ICameraDriver? _camera;

    /// <inheritdoc/>
    public void LinkDevices(IMountDriver mount, ICameraDriver? camera)
    {
        _mount = mount;
        _camera = camera;
    }

    private int _state = (int)GuiderState.Idle;
    private bool _equipmentConnected;
    private bool _paused;
    private CancellationTokenSource? _loopCts;
    private GuideLoop? _guideLoop;
    private volatile Image? _lastLoopFrame;

    private double _settlePixels;
    private double _settleTime;
    private double _ditherPixels;
    private long _settleStartedTicks;

    private enum GuiderState
    {
        Idle = 0,
        Looping = 1,
        Calibrating = 2,
        Guiding = 3,
        Settling = 4,
    }

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

    public ValueTask<(int Width, int Height)?> CameraFrameSizeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_camera is { Connected: true, NumX: > 0, NumY: > 0 } cam
                ? ((int Width, int Height)?)(cam.NumX, cam.NumY)
                : null);

    public ValueTask ConnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        _equipmentConnected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectEquipmentAsync(CancellationToken cancellationToken = default)
    {
        _equipmentConnected = false;

        ForceState(GuiderState.Idle);
        return ValueTask.CompletedTask;
    }

    private int _ditherCount;

    /// <summary>
    /// Number of times <see cref="DitherAsync"/> has been called. Used by tests to verify dithering was triggered.
    /// </summary>
    public int DitherCount => _ditherCount;

    public ValueTask DitherAsync(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false, CancellationToken cancellationToken = default)
    {
        var current = CurrentState;
        if (current is not GuiderState.Guiding)
        {
            throw new GuiderException($"Cannot dither in state {current}");
        }

        Interlocked.Increment(ref _ditherCount);

        _ditherPixels = ditherPixels;
        _settlePixels = settlePixels;
        _settleTime = settleTime;

        ForceState(GuiderState.Settling);
        RecordSettleStart();

        return ValueTask.CompletedTask;
    }

    public ValueTask<TimeSpan> ExposureTimeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(TimeSpan.FromSeconds(2));

    public ValueTask<string?> GetActiveProfileNameAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>("FakeProfile");

    public ValueTask<IReadOnlyList<string>> GetEquipmentProfilesAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<string>>(["FakeProfile"]);

    public ValueTask<SettleProgress?> GetSettleProgressAsync(CancellationToken cancellationToken = default)
    {
        var state = CurrentState;

        if (state is GuiderState.Settling or GuiderState.Calibrating)
        {
            return ValueTask.FromResult<SettleProgress?>(new SettleProgress
            {
                Done = false,
                Distance = _ditherPixels * 0.5,
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
            TotalRMS = tracker?.TotalRmsAll ?? 0.3,
            RaRMS = tracker?.RaRmsAll ?? 0.2,
            DecRMS = tracker?.DecRmsAll ?? 0.2,
            PeakRa = tracker?.PeakRa ?? 0.5,
            PeakDec = tracker?.PeakDec ?? 0.4,
            LastRaErr = tracker?.LastRaError,
            LastDecErr = tracker?.LastDecError,
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

        var avgDist = state is GuiderState.Guiding or GuiderState.Settling ? 0.2 : 0.0;
        return ValueTask.FromResult<(string?, double)>((appState, avgDist));
    }

    public ValueTask ClearCalibrationAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask FlipCalibrationAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask GuideAsync(double settlePixels, double settleTime, double settleTimeout, CancellationToken cancellationToken)
    {
        if (!_equipmentConnected)
        {
            throw new GuiderException("Equipment is not connected. Call ConnectEquipmentAsync first.");
        }

        _settlePixels = settlePixels;
        _settleTime = settleTime;

        var current = CurrentState;
        if (current is GuiderState.Guiding)
        {
            // Already guiding — nothing to do
            return ValueTask.CompletedTask;
        }

        if (current is GuiderState.Settling)
        {
            // Already settling — update settle params and restart settle timer
            _settlePixels = settlePixels;
            RecordSettleStart();
            return ValueTask.CompletedTask;
        }

        if (current is not GuiderState.Idle and not GuiderState.Looping)
        {
            throw new GuiderException($"Cannot start guiding in state {current}");
        }

        // Transition to Settling — the shared capture loop (started by LoopAsync) will
        // detect the state change and start applying guide corrections once settled.
        ForceState(GuiderState.Settling);
        RecordSettleStart();

        // If not already looping, start the capture loop now
        if (_camera is { Connected: true } camera && _mount is { Connected: true } mount && _loopCts is null)
        {
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = Task.Run(() => RunCaptureLoopAsync(camera, mount, _loopCts.Token), _loopCts.Token);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Last guide frame as a mono Image.
    /// Returns the guide loop frame (when guiding) or the loop capture frame (when looping).</summary>
    public Image? LastGuideFrame => _guideLoop?.LastFrame ?? _lastLoopFrame;

    /// <summary>Guide star position in frame pixels.</summary>
    public (double X, double Y)? GuideStarPosition =>
        _guideLoop?.LastCentroidResult is { } r ? (r.X, r.Y) : null;

    /// <summary>Guide star SNR.</summary>
    public double? GuideStarSNR =>
        _guideLoop?.LastCentroidResult?.SNR;

    /// <summary>Star profile: horizontal and vertical intensity cross-sections.</summary>
    public (float[] H, float[] V)? GuideStarProfile =>
        _guideLoop?.LastCentroidResult is { HProfile: { } h, VProfile: { } v } ? (h, v) : null;

    private void RecordSettleStart()
    {
        Interlocked.Exchange(ref _settleStartedTicks, External.TimeProvider.GetTimestamp());
    }

    /// <summary>
    /// Checks whether enough (fake) time has elapsed since settling started.
    /// If so, transitions from Settling to Guiding. This is polled by
    /// <see cref="IsGuidingAsync"/> and <see cref="IsSettlingAsync"/>,
    /// making it reliable with <see cref="FakeTimeProvider"/> (no timer callback needed).
    /// </summary>
    private bool TryCompleteSettle()
    {
        if (CurrentState is not GuiderState.Settling)
        {
            return false;
        }

        var elapsed = External.TimeProvider.GetElapsedTime(_settleStartedTicks);
        if (elapsed.TotalSeconds >= _settleTime)
        {
            TryTransition(GuiderState.Settling, GuiderState.Guiding);
            return true;
        }

        return false;
    }

    public ValueTask<bool> IsGuidingAsync(CancellationToken cancellationToken = default)
    {
        TryCompleteSettle();
        return ValueTask.FromResult(CurrentState is GuiderState.Guiding && !_paused);
    }

    public ValueTask<bool> IsLoopingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Looping or GuiderState.Guiding or GuiderState.Calibrating or GuiderState.Settling);

    public ValueTask<bool> IsSettlingAsync(CancellationToken cancellationToken = default)
    {
        TryCompleteSettle();
        return ValueTask.FromResult(CurrentState is GuiderState.Settling or GuiderState.Calibrating);
    }

    public async ValueTask<bool> LoopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var current = CurrentState;
        if (current is GuiderState.Idle)
        {
            ForceState(GuiderState.Looping);

            // Capture one frame immediately so SaveImageAsync has data right away (like PHD2 looping)
            if (_camera is { Connected: true } camera)
            {
                var exposureTime = TimeSpan.FromSeconds(2);
                _lastLoopFrame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, External, cancellationToken);

                // Start the unified capture loop in background
                _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = Task.Run(() => RunCaptureLoopAsync(camera, _mount!, _loopCts.Token), _loopCts.Token);
            }
        }

        return true;
    }

    /// <summary>
    /// Unified capture loop: continuously captures frames on the guide camera.
    /// In Looping/Settling state, just captures and stores frames.
    /// When state transitions to Guiding, sets up the GuideLoop and hands off
    /// to <see cref="GuideLoop.RunAsync"/> which takes over the capture loop
    /// with correction computations (like real PHD2).
    /// </summary>
    private async Task RunCaptureLoopAsync(ICameraDriver camera, IMountDriver mount, CancellationToken ct)
    {
        try
        {
            var exposureTime = TimeSpan.FromSeconds(2);
            var ext = External;

            // Phase 1: Loop capture — expose and store frames until guiding starts
            while (!ct.IsCancellationRequested)
            {
                var state = CurrentState;
                if (state is GuiderState.Idle)
                {
                    return;
                }

                if (state is GuiderState.Guiding or GuiderState.Settling)
                {
                    // Abort any in-flight exposure before transitioning
                    if (await camera.GetCameraStateAsync(ct) is CameraState.Exposing)
                    {
                        await camera.AbortExposureAsync(ct);
                    }
                    break;
                }

                _lastLoopFrame?.Release();
                var frame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, ct);
                _lastLoopFrame = frame;
            }

            // Continue capturing during settle — keeps the guider view updating
            while (!TryCompleteSettle() && CurrentState is GuiderState.Settling && !ct.IsCancellationRequested)
            {
                _lastLoopFrame?.Release();
                var settleFrame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, ct);
                _lastLoopFrame = settleFrame;
            }

            if (ct.IsCancellationRequested || CurrentState is GuiderState.Idle) return;

            // Phase 2: Guided capture — acquire guide star, then run GuideLoop
            var tracker = new GuiderCentroidTracker(maxStars: 1);
            var initFrame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, ct);
            _lastLoopFrame = initFrame;
            tracker.ProcessFrame(initFrame.GetChannelArray(0));
            tracker.SetLockPosition();

            var pulseTarget = new PulseGuideRouter(PulseGuideSource.Auto, camera, mount);
            var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
            var guideLoop = new GuideLoop(pulseTarget, tracker, pController, External);
            guideLoop.SetCalibration(new GuiderCalibrationResult(0, 1.0, 1.0, 0, 0, 0));
            _guideLoop = guideLoop;

            var declination = await mount.GetDeclinationAsync(ct);
            var ra = await mount.GetRightAscensionAsync(ct);
            var siderealTime = await mount.GetSiderealTimeAsync(ct);
            var hourAngle = siderealTime - ra;
            var siteLatitude = await mount.GetSiteLatitudeAsync(ct);

            // GuideLoop.RunAsync captures frames via the delegate and applies corrections
            await guideLoop.RunAsync(
                async token =>
                {
                    var f = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, token);
                    _lastLoopFrame = f; // same ref as GuideLoop.LastFrame — no extra Release needed
                    return f;
                },
                exposureTime, hourAngle, declination, siteLatitude, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on stop
        }
        catch (Exception ex)
        {
            External.AppLogger.LogError(ex, "FakeGuider capture loop error");
        }
        finally
        {
            _guideLoop = null;
        }
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        _paused = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> PixelScaleAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(
            _camera is { Connected: true, PixelSizeX: > 0, FocalLength: > 0 }
                ? 206.265 * _camera.PixelSizeX / _camera.FocalLength
                : DefaultPixelScale);

    public async ValueTask<string?> SaveImageAsync(string outputFolder, CancellationToken cancellationToken = default)
    {
        // Save the last guide or loop frame if available
        var image = _guideLoop?.LastFrame ?? _lastLoopFrame;
        if (image is null)
        {
            return null;
        }

        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, $"guider_{External.TimeProvider.GetUtcNow().UtcDateTime:yyyyMMdd_HHmmss}.fits");

        // Write WCS headers from current mount pointing so FakePlateSolver can read them
        WCS? wcs = null;
        if (_mount is { Connected: true } mount)
        {
            var ra = double.IsNaN(PointingRA) ? await mount.GetRightAscensionAsync(cancellationToken) : PointingRA;
            var dec = double.IsNaN(PointingDec) ? await mount.GetDeclinationAsync(cancellationToken) : PointingDec;
            wcs = new WCS(ra, dec);
        }
        else if (!double.IsNaN(PointingRA) && !double.IsNaN(PointingDec))
        {
            wcs = new WCS(PointingRA, PointingDec);
        }

        image.WriteToFitsFile(path, wcs);

        return path;
    }

    public ValueTask StopCaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {

        _loopCts?.Cancel();
        _loopCts = null;
        ForceState(GuiderState.Idle);
        return ValueTask.CompletedTask;
    }

    public ValueTask UnpauseAsync(CancellationToken cancellationToken = default)
    {
        _paused = false;
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

    }
}
