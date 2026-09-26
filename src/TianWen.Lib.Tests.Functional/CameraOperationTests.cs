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
/// A camera's cooling and settings through the node, with no session running (P2 part 3 of
/// docs/plans/hardware-in-the-server.md, #929). Cooling and warming are ramps, so jobs; the cooler off and the settings
/// are immediate, and refused while a job is working on the camera or a run holds it. The frame's own rules:
/// <c>CameraFrameTests</c>.
/// </summary>
[Collection("Hosting")]
public class CameraOperationTests(ITestOutputHelper outputHelper) : IAsyncLifetime
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

    // A ramp of 2 °C steps every 15 s takes minutes from ambient, so this watches it start, sees what it holds, and
    // cancels it. The ramp's own arithmetic is CameraCoolingRamp's and pinned there.
    [Fact(Timeout = 60_000)]
    public async Task ACoolDownIsARampTheNodeRunsAndRecordsAsTheCamerasIntent()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await Hub.ConnectAsync(device, ct);

        var cooling = (await Client.CoolCameraAsync(device.DeviceUri, -10.4, rampMinutes: null, ct)).Value.ShouldNotBeNull();
        cooling.Kind.ShouldBe("cool");
        await UntilAsync("the ramp to switch the cooler on", async token => (await camera.GetCoolerOnAsync(token), "cooler still off"), ct);

        Hub.TryGetCoolerIntent(device.DeviceUri, out var intent).ShouldBeTrue();
        intent.ShouldBe(CoolerIntent.CoolTo(-10), "the ramp's whole-degree TARGET, which a crash journal re-establishes");

        // While the ramp runs the camera is the job's: an immediate command would fight it.
        var coolerOff = await Client.CameraCoolerOffAsync(device.DeviceUri, ct);
        coolerOff.StatusCode.ShouldBe(409);
        coolerOff.Error.ShouldNotBeNull().ShouldContain(cooling.Id);
        (await Client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = device.DeviceUri.ToString(), Bin = 2 }, ct)).StatusCode.ShouldBe(409);

        (await Client.CancelJobAsync(cooling.Id, ct)).IsSuccess.ShouldBeTrue();
        (await UntilEndedAsync(cooling.Id, ct)).State.ShouldBe(JobState.Cancelled);

        // Free again, the cooler goes off at once, and says so to a journal. Off also spares the teardown a warm-up.
        (await Client.CameraCoolerOffAsync(device.DeviceUri, ct)).IsSuccess.ShouldBeTrue();
        (await camera.GetCoolerOnAsync(ct)).ShouldBeFalse();
        Hub.TryGetCoolerIntent(device.DeviceUri, out var off).ShouldBeTrue();
        off.ShouldBe(CoolerIntent.Off);
    }

    [Fact(Timeout = 60_000)]
    public async Task AWarmUpOfACameraWhoseCoolerIsOffEndsAtOnceAndLeavesItConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Camera, 1);
        await Hub.ConnectAsync(device, ct);

        var job = (await Client.WarmCameraAsync(device.DeviceUri, ct)).Value.ShouldNotBeNull();

        job.Kind.ShouldBe("warm");
        (await UntilEndedAsync(job.Id, ct)).State.ShouldBe(JobState.Succeeded);
        Hub.IsConnected(device.DeviceUri).ShouldBeTrue("a warm-up is not a disconnect");
    }

    [Fact(Timeout = 60_000)]
    public async Task SettingsAreAppliedAndReadBackAsTheCameraTookThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await Hub.ConnectAsync(device, ct);
        var gain = (short)((camera.GainMin + camera.GainMax) / 2);

        var result = await Client.SetCameraSettingsAsync(new CameraSettingsRequestDto
        {
            DeviceUri = device.DeviceUri.ToString(),
            Gain = gain,
            Offset = 10,
            Bin = 1,
            Frame = new FrameDto { X = 13, Y = 7, Width = 101, Height = 51 },
        }, ct);

        var settings = result.Value.ShouldNotBeNull(result.Error);
        settings.Gain.ShouldBe(gain);
        settings.Offset.ShouldBe(10);
        (settings.BinX, settings.BinY).ShouldBe((1, 1));
        (settings.Frame.X, settings.Frame.Y, settings.Frame.Width, settings.Frame.Height).ShouldBe((8, 6, 96, 50), "snapped to the camera's steps");
    }

    // Every value is checked before any is applied: a bad gain leaves the binning it came with unchanged.
    [Fact(Timeout = 60_000)]
    public async Task AValueTheCameraDoesNotTakeChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await Hub.ConnectAsync(device, ct);
        camera.BinX = 1;

        var refused = await Client.SetCameraSettingsAsync(new CameraSettingsRequestDto
        {
            DeviceUri = device.DeviceUri.ToString(),
            Bin = 2,
            Gain = (short)(camera.GainMax + 1),
        }, ct);

        refused.StatusCode.ShouldBe(400);
        refused.Error.ShouldNotBeNull().ShouldContain("gain");
        camera.BinX.ShouldBe(1);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACameraARunHoldsTakesNoCommandAndTheRunIsNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Camera, 1);
        await Hub.ConnectAsync(device, ct);
        Hub.TryAcquireLease(device.DeviceUri, "Session run", out var lease).ShouldBeTrue();
        using (lease)
        {
            foreach (var refused in new[]
            {
                (await Client.CoolCameraAsync(device.DeviceUri, -10, rampMinutes: null, ct)).Error,
                (await Client.WarmCameraAsync(device.DeviceUri, ct)).Error,
                (await Client.CameraCoolerOffAsync(device.DeviceUri, ct)).Error,
                (await Client.SetCameraSettingsAsync(new CameraSettingsRequestDto { DeviceUri = device.DeviceUri.ToString(), Bin = 2 }, ct)).Error,
            })
            {
                refused.ShouldNotBeNull().ShouldContain("Session run");
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ADeviceThatIsNotACameraIsNotTreatedAsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Focuser, 1);
        await Hub.ConnectAsync(device, ct);

        var refused = await Client.CoolCameraAsync(device.DeviceUri, -10, rampMinutes: null, ct);

        refused.StatusCode.ShouldBe(400);
        refused.Error.ShouldNotBeNull().ShouldContain("not a camera");
    }
}
