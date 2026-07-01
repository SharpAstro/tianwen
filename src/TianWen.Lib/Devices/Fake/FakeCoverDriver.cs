using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakeCoverDriver(FakeDevice fakeDevice, IServiceProvider serviceProvider) : FakeDeviceDriverBase(fakeDevice, serviceProvider), ICoverDriver
{
    // The ASCOM CoverCalibrator interface fuses two independent capabilities, and either half may be
    // absent. Two real hardware classes model as this fake:
    //   * a flip-flat (motorised cover + built-in panel) -- the default; has a cover flap.
    //   * a driver-controlled light panel with NO flap (e.g. the Gemini FlatPanel Lite), which reports
    //     CoverStatus.NotPresent -- selected by hasCover=false on the device URI.
    private volatile CoverStatus _coverState = HasCoverFlap(fakeDevice) ? CoverStatus.Closed : CoverStatus.NotPresent;
    private volatile CalibratorStatus _calibratorState = CalibratorStatus.Off;
    private int _brightness;
    private ITimer? _coverTimer;

    // Absent / unparseable defaults to a flip-flat WITH a motorised cover; only an explicit
    // hasCover=false models a bare light panel that has no flap to open or close.
    private static bool HasCoverFlap(FakeDevice fakeDevice)
        => !bool.TryParse(fakeDevice.Query.QueryValue(DeviceQueryKey.HasCover), out var hasCover) || hasCover;

    /// <summary>
    /// Simulated time for the cover to transition from Moving to Open/Closed.
    /// </summary>
    internal TimeSpan CoverMoveDuration { get; set; } = TimeSpan.FromSeconds(5);

    public int MaxBrightness => 255;

    public ValueTask<CoverStatus> GetCoverStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_coverState);

    public ValueTask<CalibratorStatus> GetCalibratorStateAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_calibratorState);

    public ValueTask<int> GetBrightnessAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_brightness);

    public Task BeginOpen(CancellationToken cancellationToken = default)
    {
        // A flap-less light panel has no cover to move; leave it NotPresent.
        if (_coverState is CoverStatus.NotPresent)
        {
            return Task.CompletedTask;
        }

        _coverState = CoverStatus.Moving;
        ScheduleCoverTransition(CoverStatus.Open);
        return Task.CompletedTask;
    }

    public Task BeginClose(CancellationToken cancellationToken = default)
    {
        // A flap-less light panel has no cover to move; leave it NotPresent.
        if (_coverState is CoverStatus.NotPresent)
        {
            return Task.CompletedTask;
        }

        _coverState = CoverStatus.Moving;
        ScheduleCoverTransition(CoverStatus.Closed);
        return Task.CompletedTask;
    }

    public Task BeginCalibratorOn(int brightness, CancellationToken cancellationToken = default)
    {
        _brightness = Math.Clamp(brightness, 0, MaxBrightness);
        _calibratorState = CalibratorStatus.Ready;
        return Task.CompletedTask;
    }

    public Task BeginCalibratorOff(CancellationToken cancellationToken = default)
    {
        _brightness = 0;
        _calibratorState = CalibratorStatus.Off;
        return Task.CompletedTask;
    }

    private void ScheduleCoverTransition(CoverStatus targetState)
    {
        var timer = TimeProvider.CreateTimer(
            _ => _coverState = targetState,
            null,
            CoverMoveDuration,
            Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _coverTimer, timer)?.Dispose();
    }
}
