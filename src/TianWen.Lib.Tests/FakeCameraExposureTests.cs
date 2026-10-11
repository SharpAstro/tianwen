using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A fake camera's exposure ends at the start it REPORTED plus its duration. The session plans its fetch from that start
/// (it waits out the rest of the exposure, then polls a fifth of a tick), so an end that drifts past it is a fetch given up.
/// A pumped test's clock moves on another thread, and it can move between the camera reading its start and arming the timer
/// that ends the exposure: on CI that put the end a whole pump step late and gave one fetch up
/// (<c>SessionImagingTests.GivenHighAltitudeTargetWhenImagingLoopThenHighUtilization</c>, #1414).
/// </summary>
public class FakeCameraExposureTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task AnExposureEndsAtTheStartItReportedEvenWhenTheClockMovesAsItIsArmed()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.Zero));
        var arming = new ClockThatMovesAsATimerIsArmed(clock);
        var external = new FakeExternal(output, clock);
        var services = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(arming)
            .AddSingleton<IDeviceHub, DeviceHub>()
            .AddLogging()
            .BuildServiceProvider();
        var camera = new FakeCameraDriver(new FakeDevice(DeviceType.Camera, 1), services);
        await camera.ConnectAsync(ct);

        // What the pump did on CI: moved the clock by a step between the camera reading its start and arming its timer.
        arming.MoveBy = TimeSpan.FromSeconds(2);
        var duration = TimeSpan.FromSeconds(10);
        var start = await camera.StartExposureAsync(duration, cancellationToken: ct);
        arming.Moved.ShouldBeTrue("the clock moved as the camera armed its exposure");

        clock.Advance(start + duration - clock.GetUtcNow());

        (await camera.GetImageReadyAsync(ct)).ShouldBeTrue("the exposure ended at the start it reported plus its duration");
        (await camera.GetCameraStateAsync(ct)).ShouldBe(CameraState.Idle);
        camera.LastExposureDuration.ShouldBe(duration);
        var image = (await ((ICameraDriver)camera).GetImageAsync(ct)).ShouldNotBeNull();
        image.Release();
    }

    /// <summary>
    /// The test's clock, moved once by <see cref="MoveBy"/> the moment a timer is armed with a due time: between the camera
    /// reading its start and its timer taking the due time, as a pump on another thread can.
    /// </summary>
    private sealed class ClockThatMovesAsATimerIsArmed(FakeTimeProviderWrapper clock) : ITimeProvider
    {
        public TimeSpan MoveBy { get; set; }

        public bool Moved { get; private set; }

        public DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public long GetTimestamp() => clock.GetTimestamp();

        public long TimestampFrequency => clock.TimestampFrequency;

        public TimeProvider System => clock.System;

        public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default) => clock.SleepAsync(duration, cancellationToken);

        public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => new MovingTimer(this, clock.CreateTimer(callback, state, dueTime, period));

        private void MoveOnce()
        {
            if (MoveBy > TimeSpan.Zero && !Moved)
            {
                Moved = true;
                clock.Advance(MoveBy);
            }
        }

        private sealed class MovingTimer(ClockThatMovesAsATimerIsArmed owner, ITimer inner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    owner.MoveOnce();
                }

                return inner.Change(dueTime, period);
            }

            public void Dispose() => inner.Dispose();

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
