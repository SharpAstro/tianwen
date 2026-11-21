using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Hosting;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.IO;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// On a suspend notice the node stops every connected mount (#1957): a slewing, tracking mount ends neither slewing nor
/// tracking, a leased mount is stopped too, one mount's failure keeps the others from stopping, and a mount that never answers
/// is cut off at the budget. Driven through a fake notice over the fake clock, so no test waits in real time.
/// </summary>
public class NodeSuspendStopTests(ITestOutputHelper output)
{
    private sealed class FakeNotice : ISuspendNotice
    {
        public event Func<Task>? Suspending;
        public event Action? Resumed;

        public Task RaiseSuspending() => Suspending?.Invoke() ?? Task.CompletedTask;

        public void RaiseResumed() => Resumed?.Invoke();
    }

    private sealed class Rig
    {
        public readonly FakeTimeProviderWrapper Clock = new FakeTimeProviderWrapper { ExternalTimePump = true };
        public readonly IDeviceHub Hub = Substitute.For<IDeviceHub>();
        public readonly FakeNotice Notice = new FakeNotice();
        public readonly NodeSuspendStop Service;
        private readonly List<(Uri, IDeviceDriver)> _connected = [];

        public Rig(ILogger<NodeSuspendStop>? logger = null)
        {
            Hub.ConnectedDevices.Returns(_connected);
            Service = new NodeSuspendStop(Hub, Notice, Clock, logger ?? NullLogger<NodeSuspendStop>.Instance);
        }

        public void Connect(string name, IDeviceDriver driver) => _connected.Add((new Uri($"Mount://FakeDevice/{name}"), driver));

        public async Task SuspendAsync(CancellationToken ct)
        {
            await Service.StartAsync(ct);
            var stop = Notice.RaiseSuspending();
            await Clock.PumpUntilCompletedAsync(stop, TimeSpan.FromSeconds(30), cancellationToken: ct);
            await stop;
        }
    }

    private async Task<FakeMountDriver> SlewingTrackingMountAsync(CancellationToken ct)
    {
        var mount = new FakeMountDriver(new FakeDevice(DeviceType.Mount, 1), new FakeExternal(output).BuildServiceProvider());
        await mount.ConnectAsync(ct);
        await mount.SetTrackingAsync(true, ct);
        await mount.MoveAxisAsync(TelescopeAxis.Primary, 1.0, ct);
        (await mount.IsSlewingAsync(ct)).ShouldBeTrue("the precondition: an axis is moving");
        (await mount.IsTrackingAsync(ct)).ShouldBeTrue("the precondition: tracking");
        return mount;
    }

    private static IMountDriver StubMount()
    {
        var mount = Substitute.For<IMountDriver>();
        mount.CanSetTracking.Returns(true);
        mount.CanMoveAxis(Arg.Any<TelescopeAxis>()).Returns(true);
        return mount;
    }

    [Fact(Timeout = 30_000)]
    public async Task A_slewing_tracking_mount_is_neither_after_the_notice()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var mount = await SlewingTrackingMountAsync(ct);
        rig.Connect("a", mount);

        await rig.SuspendAsync(ct);

        (await mount.IsSlewingAsync(ct)).ShouldBeFalse();
        (await mount.IsTrackingAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task A_leased_mount_is_stopped_too()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var mount = await SlewingTrackingMountAsync(ct);
        rig.Connect("leased", mount);
        rig.Hub.Leases.Returns([new DeviceLease(new Uri("Mount://FakeDevice/leased"), "the imaging session")]);

        await rig.SuspendAsync(ct);

        (await mount.IsSlewingAsync(ct)).ShouldBeFalse("the run cannot act once the OS freezes it");
        (await mount.IsTrackingAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task A_mount_whose_stop_throws_does_not_keep_the_others_from_stopping()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var broken = StubMount();
        broken.AbortSlewAsync(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromException(new InvalidOperationException("serial gone")));
        broken.SetTrackingAsync(false, Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromException(new InvalidOperationException("serial gone")));
        var good = await SlewingTrackingMountAsync(ct);
        rig.Connect("broken", broken);
        rig.Connect("good", good);

        await rig.SuspendAsync(ct);

        (await good.IsSlewingAsync(ct)).ShouldBeFalse();
        (await good.IsTrackingAsync(ct)).ShouldBeFalse();
        await broken.Received(1).MoveAxisAsync(TelescopeAxis.Primary, 0, Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_mount_that_never_answers_is_cut_off_at_the_budget_and_the_others_still_stop()
    {
        var ct = TestContext.Current.CancellationToken;
        var said = new RecordingLogger<NodeSuspendStop>();
        var rig = new Rig(said);
        var silent = StubMount();
        var never = new TaskCompletionSource();
        var cutOffAtTicks = 0L;
        // Ignores its token, as a driver wedged in a native call would. All it does with it is note WHEN it was
        // cancelled, on the fake clock: a token's callbacks run inside the advance that fires the deadline's timer, at
        // that timer's instant, so the reading is the deadline's own time whatever the thread pool does afterwards.
        silent.AbortSlewAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().Register(() => Volatile.Write(ref cutOffAtTicks, rig.Clock.GetUtcNow().UtcTicks));
            return new ValueTask(never.Task);
        });
        var good = await SlewingTrackingMountAsync(ct);
        rig.Connect("silent", silent);
        rig.Connect("good", good);

        await rig.Service.StartAsync(ct);
        var suspendedAt = rig.Clock.GetUtcNow();
        var stop = rig.Notice.RaiseSuspending();
        await rig.Clock.PumpUntilCompletedAsync(stop, TimeSpan.FromSeconds(30), cancellationToken: ct);
        await stop;

        // Ended by the budget and not by the driver: the stop says it was cut off, and the driver's token was
        // cancelled exactly the budget after the suspend. How much fake time the PUMP moved does not say it (#2383):
        // the deadline's timer fires at 1.5 s and the stop's continuation then runs on the thread pool; a pump that
        // looks again before it has run finds nothing else waiting on the clock and takes one more step of its
        // coarsest size, so the same stop read 1.5 s on a quiet runner and 6.5 s on a busy one. The token's own
        // instant is read inside the advance that fired it, where no runner moves it (#2388).
        said.Drain().ShouldContain("took longer than", Case.Sensitive, "the stop was cut off, and said so");
        var cutOffAt = Volatile.Read(ref cutOffAtTicks);
        cutOffAt.ShouldNotBe(0L, "the driver's token was cancelled while it was still waiting");
        TimeSpan.FromTicks(cutOffAt - suspendedAt.UtcTicks).ShouldBe(NodeSuspendStop.Budget, "at the budget, to the tick");
        // And the budget is one its reasons allow, on both sides (#2394). Windows gives about two seconds before it
        // suspends, so it is under that; and it is most of that, because a stop that is cut off may leave its mount
        // driving, so it is not under half. The assertion above holds for any budget: these two are what a budget
        // cut to a tenth of a second, or raised past the suspend, would fail.
        var windowsGives = TimeSpan.FromSeconds(2);
        NodeSuspendStop.Budget.ShouldBeLessThan(windowsGives, "the stop has ended before Windows suspends");
        NodeSuspendStop.Budget.ShouldBeGreaterThanOrEqualTo(windowsGives / 2, "and a mount has most of that time to take its stop");
        (await good.IsSlewingAsync(ct)).ShouldBeFalse();
        (await good.IsTrackingAsync(ct)).ShouldBeFalse();
        // Behind a command that never returns no later one is made (the review of #2399): a mount's stop goes one
        // command after another, so the silent mount, stuck in its abort, is asked for neither axis nor for tracking,
        // though it can do both (StubMount). Were its abort to return at the cut-off, all three would be made at once,
        // inside the advance that fires the deadline, so this does not pass by being looked at too early.
        await silent.DidNotReceive().MoveAxisAsync(Arg.Any<TelescopeAxis>(), Arg.Any<double>(), Arg.Any<CancellationToken>());
        await silent.DidNotReceive().SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = 30_000)]
    public async Task A_stopped_service_takes_no_notice_of_a_suspend()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new Rig();
        var mount = await SlewingTrackingMountAsync(ct);
        rig.Connect("a", mount);

        await rig.Service.StartAsync(ct);
        await rig.Service.StopAsync(ct);
        await rig.Notice.RaiseSuspending();
        rig.Notice.RaiseResumed();

        (await mount.IsSlewingAsync(ct)).ShouldBeTrue("a stopped service takes no notice");
    }
}
