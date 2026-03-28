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
    private ITimer? _settleTimer;
    private CancellationTokenSource? _guideCts;
    private CancellationTokenSource? _loopCts;
    private GuideLoop? _guideLoop;
    private volatile float[,]? _lastLoopFrame;

    private double _settlePixels;
    private double _settleTime;
    private double _ditherPixels;

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
        Interlocked.Exchange(ref _settleTimer, null)?.Dispose();
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
        StartSettleTimer(settleTime);

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
        if (current is not GuiderState.Idle and not GuiderState.Looping)
        {
            throw new GuiderException($"Cannot start guiding in state {current}");
        }

        // Stop any looping capture before transitioning to guiding
        _loopCts?.Cancel();
        _loopCts = null;

        // Settle via timer, then start real guide loop in background
        ForceState(GuiderState.Settling);
        StartSettleTimer(settleTime);

        if (_camera is { Connected: true } camera && _mount is { Connected: true } mount)
        {
            _guideCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = Task.Run(() => RunGuideLoopAsync(camera, mount, _guideCts.Token), _guideCts.Token);
        }

        return ValueTask.CompletedTask;
    }

    private async Task RunGuideLoopAsync(ICameraDriver camera, IMountDriver mount, CancellationToken ct)
    {
        try
        {
            var exposureTime = TimeSpan.FromSeconds(2);
            var tracker = new GuiderCentroidTracker(maxStars: 1);

            // Capture initial frame and acquire guide star
            var ext = External;
            var frame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, ct);
            tracker.ProcessFrame(frame);
            tracker.SetLockPosition();

            // Create guide loop with a simple P-controller (no calibration needed for fake — corrections are no-ops)
            var pulseTarget = new PulseGuideRouter(PulseGuideSource.Auto, camera, mount);
            var pController = new ProportionalGuideController { AggressivenessRa = 0.7, AggressivenessDec = 0.7, MinPulseMs = 20 };
            var guideLoop = new GuideLoop(pulseTarget, tracker, pController, External);

            // Set a unit calibration (1:1 pixel-to-axis mapping, no rotation)
            guideLoop.SetCalibration(new GuiderCalibrationResult(0, 1.0, 1.0, 0, 0, 0));
            _guideLoop = guideLoop;

            var declination = await mount.GetDeclinationAsync(ct);
            var ra = await mount.GetRightAscensionAsync(ct);
            var siderealTime = await mount.GetSiderealTimeAsync(ct);
            var hourAngle = siderealTime - ra;
            var siteLatitude = await mount.GetSiteLatitudeAsync(ct);

            await guideLoop.RunAsync(
                async token => await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, ext, token),
                exposureTime, hourAngle, declination, siteLatitude, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on stop
        }
        catch (Exception ex)
        {
            External.AppLogger.LogError(ex, "FakeGuider guide loop error");
        }
        finally
        {
            _guideLoop = null;
        }
    }

    private Image? _cachedGuideImage;
    private float[,]? _cachedGuideFrame;

    /// <summary>Last guide frame as a mono Image — zero-copy, cached until frame changes.</summary>
    public Image? LastGuideFrame
    {
        get
        {
            if (_guideLoop?.LastFrame is not { } frame) return null;
            if (ReferenceEquals(frame, _cachedGuideFrame)) return _cachedGuideImage;

            _cachedGuideFrame = frame;
            var height = frame.GetLength(0);
            var width = frame.GetLength(1);
            var max = 0f;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (frame[y, x] > max) max = frame[y, x];
            _cachedGuideImage = new Image([frame], BitDepth.Float32, max, 0f, 0f,
                new ImageMeta { SensorType = SensorType.Monochrome });
            return _cachedGuideImage;
        }
    }

    /// <summary>Guide star position in frame pixels.</summary>
    public (double X, double Y)? GuideStarPosition =>
        _guideLoop?.LastCentroidResult is { } r ? (r.X, r.Y) : null;

    /// <summary>Guide star SNR.</summary>
    public double? GuideStarSNR =>
        _guideLoop?.LastCentroidResult?.SNR;

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

    public ValueTask<bool> IsGuidingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Guiding && !_paused);

    public ValueTask<bool> IsLoopingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Looping or GuiderState.Guiding or GuiderState.Calibrating or GuiderState.Settling);

    public ValueTask<bool> IsSettlingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Settling or GuiderState.Calibrating);

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

                // Continue capturing in background
                _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _ = Task.Run(() => RunLoopCaptureAsync(camera, _loopCts.Token), _loopCts.Token);
            }
        }

        return true;
    }

    private async Task RunLoopCaptureAsync(ICameraDriver camera, CancellationToken ct)
    {
        try
        {
            var exposureTime = TimeSpan.FromSeconds(2);
            while (!ct.IsCancellationRequested && CurrentState is GuiderState.Looping)
            {
                var frame = await BuiltInGuiderDriver.CaptureGuideFrameAsync(camera, exposureTime, External, ct);
                _lastLoopFrame = frame;
            }
        }
        catch (OperationCanceledException)
        {
            // expected on stop
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
        var frame = _guideLoop?.LastFrame ?? _lastLoopFrame;
        if (frame is null)
        {
            return null;
        }

        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, $"guider_{External.TimeProvider.GetUtcNow().UtcDateTime:yyyyMMdd_HHmmss}.fits");

        var height = frame.GetLength(0);
        var width = frame.GetLength(1);
        var dataMax = 0f;
        var dataMin = float.MaxValue;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var val = frame[y, x];
                if (val > dataMax) dataMax = val;
                if (val < dataMin) dataMin = val;
            }
        }

        var image = new Image([frame], BitDepth.Float32, dataMax, dataMin, 0f, default);

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
        Interlocked.Exchange(ref _settleTimer, null)?.Dispose();
        _guideCts?.Cancel();
        _guideCts = null;
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
        Interlocked.Exchange(ref _settleTimer, null)?.Dispose();
    }
}
