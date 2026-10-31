using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using TianWen.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The camera state the node publishes resolves its warm-up target with the rig's ambient, as the node's own ramp does (#1936),
/// so a client's <see cref="CameraReading.NeedsWarmUp"/>, <c>WarmUpTargetC</c> and the shutdown's progress line agree with it.
/// </summary>
public class DeviceStatePollerRigAmbientTests(ITestOutputHelper output)
{
    // FakeFocuserDriver's own temperature.
    private const double FocuserC = 15.0;

    private sealed record Rig(IDeviceHub Hub, FakeDevice CameraDevice, FakeDevice FocuserDevice, IDeviceDiscovery Discovery);

    private async Task<Rig> BuildAsync(bool heatsink, CancellationToken ct)
    {
        var hub = new FakeExternal(output).BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(cameraDevice, ct);
        if (!heatsink)
        {
            ((FakeCameraDriver)camera).MakeHeatsinkless(CameraReading.WarmUpFallbackTargetC, sensorCannotPassAmbient: false);
        }
        await hub.ConnectAsync(focuserDevice, ct);
        await camera.SetCoolerOnAsync(true, ct);

        var discovery = DeviceHubWarmUpAmbientTests.DiscoveryOf(
            DeviceHubWarmUpAmbientTests.ProfileOf(cameraDevice.DeviceUri, focuserDevice.DeviceUri, null));
        return new Rig(hub, cameraDevice, focuserDevice, discovery);
    }

    private static async Task<CameraDeviceStateDto> PublishedAsync(Rig rig, IDeviceDiscovery? discovery, CancellationToken ct)
    {
        var reading = (await rig.Hub.ReadCameraAsync(rig.CameraDevice.DeviceUri, NullLogger.Instance, ct)).ShouldNotBeNull();
        return await DeviceStatePoller.CameraStateAsync(rig.Hub, discovery, rig.CameraDevice.DeviceUri, reading, null, ct);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACameraWithoutAHeatSinkPublishesTheFocusersReadingAsItsTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await BuildAsync(heatsink: false, ct);

        var published = await PublishedAsync(rig, rig.Discovery, ct);

        published.WarmUpTargetC.ShouldBe(FocuserC);
        published.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.RigAmbient);
    }

    [Fact(Timeout = 60_000)]
    public async Task AClientReadsTheNodesTargetBackSoTheQuitQuestionAgrees()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await BuildAsync(heatsink: false, ct);

        var reading = (await PublishedAsync(rig, rig.Discovery, ct)).ToReading();

        (reading with { CcdTemperatureC = FocuserC }).NeedsWarmUp.ShouldBeFalse("a sensor at the rig's ambient is warm");
        (reading with { CcdTemperatureC = FocuserC - 10 }).NeedsWarmUp.ShouldBeTrue("a sensor well below it is cold");
        reading.RigAmbientC.ShouldBe(FocuserC);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheSourceSurvivesTheWire()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await BuildAsync(heatsink: false, ct);
        var published = await PublishedAsync(rig, rig.Discovery, ct);

        var json = JsonSerializer.Serialize(published, HostingJsonContext.Default.CameraDeviceStateDto);
        var read = JsonSerializer.Deserialize(json, HostingJsonContext.Default.CameraDeviceStateDto).ShouldNotBeNull();

        read.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.RigAmbient);
        read.WarmUpTargetC.ShouldBe(FocuserC);
        (read.ToReading() with { CcdTemperatureC = FocuserC }).NeedsWarmUp.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task ACameraWithAHeatSinkStillPublishesTheHeatSink()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await BuildAsync(heatsink: true, ct);

        var published = await PublishedAsync(rig, rig.Discovery, ct);

        published.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.HeatSink);
        published.WarmUpTargetC.ShouldNotBe(FocuserC);
    }

    [Fact(Timeout = 60_000)]
    public async Task WithNoDiscoveryTheTargetFallsBackAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await BuildAsync(heatsink: false, ct);

        var published = await PublishedAsync(rig, discovery: null, ct);

        published.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.Fallback);
        published.WarmUpTargetC.ShouldBe(CameraReading.WarmUpFallbackTargetC);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACameraInNoProfileFallsBackAsBefore()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await BuildAsync(heatsink: false, ct);
        var other = DeviceHubWarmUpAmbientTests.DiscoveryOf(
            DeviceHubWarmUpAmbientTests.ProfileOf(new FakeDevice(DeviceType.Camera, 9).DeviceUri, rig.FocuserDevice.DeviceUri, null));

        var published = await PublishedAsync(rig, other, ct);

        published.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.Fallback);
    }

    [Fact(Timeout = 60_000)]
    public async Task AFocuserARunHoldsIsNotRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = await BuildAsync(heatsink: false, ct);
        rig.Hub.TryAcquireLease(rig.FocuserDevice.DeviceUri, "Session run", out var lease).ShouldBeTrue();
        using (lease)
        {
            var published = await PublishedAsync(rig, rig.Discovery, ct);

            published.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.Fallback, "the node never reads a device a run holds");
        }
    }
}
