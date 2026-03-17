using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices.Fake;

internal class FakeGuider(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), IGuider
{

    private const double DefaultPixelScale = 1.5;
    private const int GuideWidth = 320;
    private const int GuideHeight = 240;

    /// <summary>
    /// Current pointing RA in hours (J2000). Set by the test or by connecting to a mount.
    /// Used by <see cref="SaveImageAsync"/> to generate a FITS file with correct WCS headers.
    /// </summary>
    internal double PointingRA { get; set; } = double.NaN;

    /// <summary>
    /// Current pointing Dec in degrees (J2000). Set by the test or by connecting to a mount.
    /// Used by <see cref="SaveImageAsync"/> to generate a FITS file with correct WCS headers.
    /// </summary>
    internal double PointingDec { get; set; } = double.NaN;

    private int _state = (int)GuiderState.Idle;
    private bool _equipmentConnected;
    private bool _paused;
    private ITimer? _settleTimer;

    private double _settlePixels;
    private double _settleTime;
    private double _ditherPixels;

    private readonly GuideStats _stats = new GuideStats
    {
        TotalRMS = 0.3,
        RaRMS = 0.2,
        DecRMS = 0.2,
        PeakRa = 0.5,
        PeakDec = 0.4,
    };

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
        => ValueTask.FromResult<(int Width, int Height)?>((GuideWidth, GuideHeight));

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

    public ValueTask DitherAsync(double ditherPixels, double settlePixels, double settleTime, double settleTimeout, bool raOnly = false, CancellationToken cancellationToken = default)
    {
        var current = CurrentState;
        if (current is not GuiderState.Guiding)
        {
            throw new GuiderException($"Cannot dither in state {current}");
        }

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

        return ValueTask.FromResult<GuideStats?>(_stats.Clone());
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

    public ValueTask GuideAsync(double settlePixels, double settleTime, double settleTimeout, CancellationToken cancellationToken)
    {
        _settlePixels = settlePixels;
        _settleTime = settleTime;

        var current = CurrentState;
        if (current is not GuiderState.Idle and not GuiderState.Looping)
        {
            throw new GuiderException($"Cannot start guiding in state {current}");
        }

        // Calibration completes instantly in the fake, then settle via timer
        ForceState(GuiderState.Settling);
        StartSettleTimer(settleTime);

        return ValueTask.CompletedTask;
    }

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
        => ValueTask.FromResult(CurrentState is GuiderState.Guiding);

    public ValueTask<bool> IsLoopingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Looping or GuiderState.Guiding or GuiderState.Calibrating or GuiderState.Settling);

    public ValueTask<bool> IsSettlingAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CurrentState is GuiderState.Settling or GuiderState.Calibrating);

    public ValueTask<bool> LoopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var current = CurrentState;
        if (current is GuiderState.Idle)
        {
            ForceState(GuiderState.Looping);
        }

        return ValueTask.FromResult(true);
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        _paused = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask<double> PixelScaleAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(DefaultPixelScale);

    public ValueTask<string?> SaveImageAsync(string outputFolder, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);
        var path = Path.Combine(outputFolder, $"guider_{External.TimeProvider.GetUtcNow().UtcDateTime:yyyyMMdd_HHmmss}.fits");

        // Render a synthetic star field with proper WCS headers
        var array = SyntheticStarFieldRenderer.Render(GuideWidth, GuideHeight, defocusSteps: 0, exposureSeconds: 2, noiseSeed: 42);

        var dataMax = 0f;
        var dataMin = float.MaxValue;
        for (var y = 0; y < array.GetLength(0); y++)
        {
            for (var x = 0; x < array.GetLength(1); x++)
            {
                var val = array[y, x];
                if (val > dataMax) dataMax = val;
                if (val < dataMin) dataMin = val;
            }
        }

        var wcs = !double.IsNaN(PointingRA) && !double.IsNaN(PointingDec)
            ? new WCS(PointingRA, PointingDec)
            : null as WCS?;

        var image = new Image([array], BitDepth.Float32, dataMax, dataMin, 0f, default);
        image.WriteToFitsFile(path, wcs);

        return ValueTask.FromResult<string?>(path);
    }

    public ValueTask StopCaptureAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _settleTimer, null)?.Dispose();
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
