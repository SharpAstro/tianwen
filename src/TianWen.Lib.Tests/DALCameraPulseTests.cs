using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A DAL camera's driver-timed guide pulse (a ZWO or Player One ST-4 port, whose SDK takes no duration): the ORDER of what it
/// does, on a fake clock, never how long it took (#2031). The pulse's length is the guide correction, so the driver's contract
/// is that the line is on for as long as its one-shot timer runs; how well the real clock keeps that timer is
/// <c>SystemTimeProviderPrecisionTests</c>' opt-in measurement.
/// </summary>
[Collection("Devices")]
public class DALCameraPulseTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(20);

    [Fact(Timeout = 60_000)]
    public async Task ADriverTimedPulseSwitchesTheLineOnArmsTheOneShotTimerSwitchesTheLineOffFromItAndOnlyThenClearsIsPulseGuiding()
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<string>();
        var time = new FakeTimeProviderWrapper();
        var services = new ServiceCollection()
            .AddSingleton<IExternal>(new FakeExternal(output, time))
            .AddSingleton<ITimeProvider>(new EventLoggingTimeProvider(time, events))
            .AddSingleton<IDeviceHub, DeviceHub>()
            .AddLogging()
            .BuildServiceProvider();
        var device = new FakeDevice(DeviceType.Camera, 1);

        // The scripted body takes no duration (CanPulseGuideForDuration is false), so the driver itself switches the line off.
        var state = new FakeCmosState(device.DeviceId) { HasST4Port = true };
        var camera = new TestDalCameraDriver(device, services, state);
        await camera.ConnectAsync(ct);
        camera.CanPulseGuide.ShouldBeTrue();

        // What connecting did with timers is not the pulse's.
        events.Clear();
        state.OnPulseLine = (direction, on) => events.Enqueue(on
            ? $"line on {direction}"
            : $"line off {direction}, IsPulseGuiding still {IsPulseGuidingNow(camera)}");

        await camera.StartPulseGuideAsync(GuideDirection.West, Pulse, ct);

        events.ToArray().ShouldBe(["line on West", $"arm one-shot {Pulse}"], "the line goes on, then the timer that will end it is armed for the pulse's length");
        (await camera.GetIsPulseGuidingAsync(ct)).ShouldBeTrue();

        // The clock stops a tick short of the pulse: nothing has ended it.
        time.Advance(Pulse - TimeSpan.FromTicks(1));

        events.Count.ShouldBe(2, "the line is not switched off before the timer is due");
        (await camera.GetIsPulseGuidingAsync(ct)).ShouldBeTrue();

        // The tick that makes the timer due switches the line off, from the timer's own callback, with the pulse still in flight.
        time.Advance(TimeSpan.FromTicks(1));

        events.ToArray().ShouldBe(
            ["line on West", $"arm one-shot {Pulse}", "line off West, IsPulseGuiding still True"],
            "the timer switches the line off, and IsPulseGuiding is still true while it does");
        (await camera.GetIsPulseGuidingAsync(ct)).ShouldBeFalse("the in-flight bookkeeping clears only after the line is off");

        await camera.DisconnectAsync(ct);
    }

    /// <summary>
    /// <see cref="DALCameraDriver{TDeviceInfo,TNativeDevice}.GetIsPulseGuidingAsync"/> from inside the scripted body's synchronous
    /// line switch, where it cannot be awaited: it answers from a field, so its task is complete when it returns.
    /// </summary>
    private static bool IsPulseGuidingNow(TestDalCameraDriver camera)
    {
        var guiding = camera.GetIsPulseGuidingAsync();
        guiding.IsCompletedSuccessfully.ShouldBeTrue();
        return guiding.Result;
    }

    /// <summary>
    /// A clock that logs every ONE-SHOT timer armed through it, before it forwards to the real (fake) one: the driver's
    /// "arm the timer" step as an event in order with the scripted body's line switches.
    /// </summary>
    private sealed class EventLoggingTimeProvider(ITimeProvider inner, ConcurrentQueue<string> events) : ITimeProvider
    {
        public DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public long GetTimestamp() => inner.GetTimestamp();

        public long TimestampFrequency => inner.TimestampFrequency;

        public ValueTask SleepAsync(TimeSpan duration, CancellationToken cancellationToken = default) => inner.SleepAsync(duration, cancellationToken);

        public TimeProvider System => inner.System;

        public ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            LogArm(events, dueTime, period);
            return new EventLoggingTimer(inner.CreateTimer(callback, state, dueTime, period), events);
        }

        internal static void LogArm(ConcurrentQueue<string> events, TimeSpan dueTime, TimeSpan period)
        {
            // A disarmed timer is no event; a periodic one is logged as what it is, so a driver that moved to one fails the order.
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                events.Enqueue(period == Timeout.InfiniteTimeSpan ? $"arm one-shot {dueTime}" : $"arm periodic {dueTime} every {period}");
            }
        }
    }

    /// <summary>The timer <see cref="EventLoggingTimeProvider"/> hands out: <see cref="Change"/> is an arm when it gives a due time.</summary>
    private sealed class EventLoggingTimer(ITimer inner, ConcurrentQueue<string> events) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            EventLoggingTimeProvider.LogArm(events, dueTime, period);
            return inner.Change(dueTime, period);
        }

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
