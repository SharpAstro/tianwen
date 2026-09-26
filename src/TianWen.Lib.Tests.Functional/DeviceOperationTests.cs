using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.RemoteClient;
using Xunit;
using static TianWen.Lib.Tests.Functional.NodeWait;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Connect, disconnect and warm-and-disconnect as the node's jobs, through the client P6 switches the Equipment tab to
/// (P2 part 2 of docs/plans/hardware-in-the-server.md, #929). The rules are the Equipment tab's, in its order: a device a
/// run holds is refused first, then a camera that is cold or at work unless the client skips the warm-up, and a device
/// takes one job at a time. The per-device slot on its own: <c>NodeJobsPerDeviceTests</c>.
/// </summary>
[Collection("Hosting")]
public class DeviceOperationTests(ITestOutputHelper outputHelper) : IAsyncLifetime
{
    private NodeHarness _node = null!;

    public async ValueTask InitializeAsync() => _node = await NodeHarness.StartAsync(outputHelper, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _node.DisposeAsync();

    private IDeviceHub Hub => _node.App.Services.GetRequiredService<IDeviceHub>();

    private TianWenNodeClient Client => new TianWenNodeClient(_node.Client);

    private Task<JobDto> UntilEndedAsync(string id, CancellationToken ct) =>
        UntilAsync<JobDto>($"job {id} to end", async token =>
        {
            var job = (await Client.GetJobAsync(id, token)).Value;
            return (job is { State: not JobState.Running } ? job : null, job is null ? "not found" : $"{job.State}: {job.Step}");
        }, ct);

    /// <summary>A connected fake camera cooled to -10 °C: each read of its sensor moves it a degree toward the setpoint.</summary>
    private async Task<ICameraDriver> ConnectCooledCameraAsync(FakeDevice device, CancellationToken ct)
    {
        var camera = (ICameraDriver)await Hub.ConnectAsync(device, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);
        while (await camera.GetCCDTemperatureAsync(ct) > -10)
        {
            // Reading it is what cools it.
        }
        return camera;
    }

    [Fact(Timeout = 60_000)]
    public async Task ADeviceIsConnectedByItsUriAsAJobThatNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Focuser, 1);

        var started = await Client.ConnectDeviceAsync(device.DeviceUri, ct);

        started.IsSuccess.ShouldBeTrue(started.Error);
        var job = started.Value.ShouldNotBeNull();
        job.Kind.ShouldBe("connect");
        new Uri(job.DeviceUri.ShouldNotBeNull()).DeviceKey.ShouldBe(device.DeviceUri.DeviceKey);
        (await UntilEndedAsync(job.Id, ct)).State.ShouldBe(JobState.Succeeded);
        Hub.IsConnected(device.DeviceUri).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task ADeviceNoSourceKnowsIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        (await Client.ConnectDeviceAsync(new Uri("Camera://NoSuchSource/1"), ct)).IsNotFound.ShouldBeTrue();
        (await Client.DisconnectDeviceAsync(new FakeDevice(DeviceType.Camera, 9).DeviceUri, skipWarmUp: true, ct)).IsNotFound.ShouldBeTrue("it is not connected");
        (await Client.GetDisconnectSafetyAsync(new FakeDevice(DeviceType.Camera, 9).DeviceUri, ct)).IsNotFound.ShouldBeTrue();
    }

    // The Equipment tab's flow: read whether it is safe, and offer a warm-up or a cold disconnect when it is not.
    [Fact(Timeout = 60_000)]
    public async Task ACooledCameraIsNotDisconnectedColdUnlessTheClientSkipsTheWarmUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Camera, 1);
        await ConnectCooledCameraAsync(device, ct);

        var check = (await Client.GetDisconnectSafetyAsync(device.DeviceUri, ct)).Value.ShouldNotBeNull();
        check.Safety.ShouldBe(DisconnectSafety.CoolerOn);
        check.LeaseOwner.ShouldBeNull();

        var refused = await Client.DisconnectDeviceAsync(device.DeviceUri, skipWarmUp: false, ct);
        refused.StatusCode.ShouldBe(409);
        refused.Error.ShouldNotBeNull().ShouldContain("cooler on");
        Hub.IsConnected(device.DeviceUri).ShouldBeTrue();

        var cold = (await Client.DisconnectDeviceAsync(device.DeviceUri, skipWarmUp: true, ct)).Value.ShouldNotBeNull();
        (await UntilEndedAsync(cold.Id, ct)).State.ShouldBe(JobState.Succeeded);
        Hub.IsConnected(device.DeviceUri).ShouldBeFalse();
    }

    // Skipping the warm-up is consent to a cold disconnect, never to ending the night: only stopping the run gets past a lease.
    [Fact(Timeout = 60_000)]
    public async Task ADeviceARunHoldsIsRefusedEveryDisconnectAndNamesTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await Hub.ConnectAsync(device, ct);
        Hub.TryAcquireLease(device.DeviceUri, "Session run", out var lease).ShouldBeTrue();
        using (lease)
        {
            (await Client.GetDisconnectSafetyAsync(device.DeviceUri, ct)).Value.ShouldNotBeNull().LeaseOwner.ShouldBe("Session run");

            foreach (var refused in new[]
            {
                await Client.DisconnectDeviceAsync(device.DeviceUri, skipWarmUp: true, ct),
                await Client.WarmAndDisconnectDeviceAsync(device.DeviceUri, ct),
            })
            {
                refused.StatusCode.ShouldBe(409);
                refused.Error.ShouldNotBeNull().ShouldContain("Session run");
            }
            Hub.IsConnected(device.DeviceUri).ShouldBeTrue();
        }

        var job = (await Client.DisconnectDeviceAsync(device.DeviceUri, skipWarmUp: false, ct)).Value.ShouldNotBeNull();
        (await UntilEndedAsync(job.Id, ct)).State.ShouldBe(JobState.Succeeded);
        Hub.IsConnected(device.DeviceUri).ShouldBeFalse("let go, it disconnects");
    }

    [Fact(Timeout = 60_000)]
    public async Task ACameraWhoseCoolerIsOffIsWarmedAndDisconnectedAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Camera, 1);
        await Hub.ConnectAsync(device, ct);

        var job = (await Client.WarmAndDisconnectDeviceAsync(device.DeviceUri, ct)).Value.ShouldNotBeNull();

        job.Kind.ShouldBe("warm-and-disconnect");
        (await UntilEndedAsync(job.Id, ct)).State.ShouldBe(JobState.Succeeded);
        Hub.IsConnected(device.DeviceUri).ShouldBeFalse();
    }

    // The ramp runs in the node, one 2 °C step every 30 s: while it runs the camera is the job's. The same job again joins
    // it, another kind is refused naming it, and cancelling it leaves the camera connected where the ramp stopped.
    [Fact(Timeout = 60_000)]
    public async Task AWarmUpHoldsTheCameraUntilItEndsOrIsCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = await ConnectCooledCameraAsync(device, ct);

        var warming = (await Client.WarmAndDisconnectDeviceAsync(device.DeviceUri, ct)).Value.ShouldNotBeNull();
        await UntilAsync("the ramp to start", async token =>
        {
            var job = (await Client.GetJobAsync(warming.Id, token)).Value;
            return (job is { State: JobState.Running, Step: { } step } && step.StartsWith("Warming", StringComparison.Ordinal), $"{job?.State}: {job?.Step}");
        }, ct);

        (await Client.WarmAndDisconnectDeviceAsync(device.DeviceUri, ct)).Value.ShouldNotBeNull().Id.ShouldBe(warming.Id, "the same warm-up, joined");
        var busy = await Client.DisconnectDeviceAsync(device.DeviceUri, skipWarmUp: true, ct);
        busy.StatusCode.ShouldBe(409);
        busy.Error.ShouldNotBeNull().ShouldContain(warming.Id);

        (await Client.CancelJobAsync(warming.Id, ct)).IsSuccess.ShouldBeTrue();

        (await UntilEndedAsync(warming.Id, ct)).State.ShouldBe(JobState.Cancelled);
        Hub.IsConnected(device.DeviceUri).ShouldBeTrue("a cancelled warm-up disconnects nothing");

        // A node stopping warms every cooled camera it holds, which would spend this test's teardown on a ramp.
        await camera.SetCoolerOnAsync(false, ct);
    }
}
