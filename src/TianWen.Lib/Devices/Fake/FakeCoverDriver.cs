using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Fake;

internal class FakeCoverDriver(FakeDevice fakeDevice, IExternal external) : FakeDeviceDriverBase(fakeDevice, external), ICoverDriver
{
    private volatile CoverStatus _coverState = CoverStatus.Closed;
    private volatile CalibratorStatus _calibratorState = CalibratorStatus.Off;
    private int _brightness;
    private ITimer? _coverTimer;

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
        _coverState = CoverStatus.Moving;
        ScheduleCoverTransition(CoverStatus.Open);
        return Task.CompletedTask;
    }

    public Task BeginClose(CancellationToken cancellationToken = default)
    {
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
        var timer = External.TimeProvider.CreateTimer(
            _ => _coverState = targetState,
            null,
            CoverMoveDuration,
            Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _coverTimer, timer)?.Dispose();
    }
}
