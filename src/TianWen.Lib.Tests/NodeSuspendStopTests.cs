using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

        public Rig()
        {
            Hub.ConnectedDevices.Returns(_connected);
            Service = new NodeSuspendStop(Hub, Notice, Clock, NullLogger<NodeSuspendStop>.Instance);
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
        var rig = new Rig();
        var silent = StubMount();
        var never = new TaskCompletionSource();
        // Ignores its token, as a driver wedged in a native call would.
        silent.AbortSlewAsync(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask(never.Task));
        var good = await SlewingTrackingMountAsync(ct);
        rig.Connect("silent", silent);
        rig.Connect("good", good);

        await rig.Service.StartAsync(ct);
        var stop = rig.Notice.RaiseSuspending();
        var pumped = await rig.Clock.PumpUntilCompletedAsync(stop, TimeSpan.FromSeconds(30), cancellationToken: ct);
        await stop;

        pumped.ShouldBeLessThan(TimeSpan.FromSeconds(5), "ended by the 1.5 s budget, not by the driver");
        (await good.IsSlewingAsync(ct)).ShouldBeFalse();
        (await good.IsTrackingAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 30_000)]
    public async Task Nothing_connected_is_a_quiet_no_op_and_stopping_the_service_unsubscribes()
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
