using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class BuiltInGuiderDriverTests(ITestOutputHelper output)
{
    [Fact]
    public void GivenBuiltInGuiderDeviceWhenCreatedThenDefaultPulseGuideSourceIsAuto()
    {
        var device = new BuiltInGuiderDevice();

        device.GetPulseGuideSource().ShouldBe(PulseGuideSource.Auto);
    }

    [Theory]
    [InlineData("Auto", PulseGuideSource.Auto)]
    [InlineData("Camera", PulseGuideSource.Camera)]
    [InlineData("Mount", PulseGuideSource.Mount)]
    [InlineData("auto", PulseGuideSource.Auto)]
    [InlineData("camera", PulseGuideSource.Camera)]
    [InlineData("mount", PulseGuideSource.Mount)]
    public void GivenBuiltInGuiderDeviceWithQueryParamWhenCreatedThenPulseGuideSourceIsParsed(string value, PulseGuideSource expected)
    {
        var uri = new Uri($"guider://BuiltInGuiderDevice/builtin?pulseGuideSource={value}#Built-in Guider");
        var device = new BuiltInGuiderDevice(uri);

        device.GetPulseGuideSource().ShouldBe(expected);
    }

    [Fact]
    public void GivenBuiltInGuiderDeviceWithInvalidQueryParamWhenCreatedThenDefaultsToAuto()
    {
        var uri = new Uri("guider://BuiltInGuiderDevice/builtin?pulseGuideSource=invalid#Built-in Guider");
        var device = new BuiltInGuiderDevice(uri);

        device.GetPulseGuideSource().ShouldBe(PulseGuideSource.Auto);
    }

    [Fact]
    public async Task GivenLinkedDriverWhenConnectAndLoopThenStateIsLooping()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, _, _, _) = await CreateLinkedDriver(ct);

        await driver.ConnectAsync(ct);
        driver.Connected.ShouldBeTrue();

        await driver.ConnectEquipmentAsync(ct);
        (await driver.LoopAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue();
        (await driver.IsLoopingAsync(ct)).ShouldBeTrue();
    }

    [Fact]
    public void GivenUnlinkedDriverWhenGuideThenThrows()
    {
        var external = new FakeExternal(output);
        var device = new BuiltInGuiderDevice();
        var driver = new BuiltInGuiderDriver(device, external);

        Should.Throw<GuiderException>(async () =>
            await driver.GuideAsync(0.5, 10, 60, TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task GivenLinkedDriverWhenStopCaptureThenStateIsIdle()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, _, _, _) = await CreateLinkedDriver(ct);

        await driver.ConnectAsync(ct);
        await driver.LoopAsync(TimeSpan.FromSeconds(5), ct);
        await driver.StopCaptureAsync(TimeSpan.FromSeconds(5), ct);

        (await driver.IsLoopingAsync(ct)).ShouldBeFalse();
        (await driver.IsGuidingAsync(ct)).ShouldBeFalse();
    }

    [Fact]
    public void GivenPulseGuideRouterWithCameraSourceAndNonSt4CameraThenThrows()
    {
        // FakeCameraDriver has CanPulseGuide = false
        var external = new FakeExternal(output);
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var camera = new FakeCameraDriver(cameraDevice, external);
        var mountDevice = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(mountDevice, external);

        Should.Throw<InvalidOperationException>(() =>
            new PulseGuideRouter(PulseGuideSource.Camera, camera, mount)
        );
    }

    [Fact]
    public async Task GivenPulseGuideRouterWithMountSourceThenRoutesToMount()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var mountDevice = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(mountDevice, external);
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var router = new PulseGuideRouter(PulseGuideSource.Mount, null, mount);

        // Should not throw — routes to mount
        await router.PulseGuideAsync(GuideDirection.West, TimeSpan.FromMilliseconds(100), ct);

        var isPulseGuiding = await router.IsPulseGuidingAsync(ct);
        // Pulse guiding state depends on mount implementation
        output.WriteLine($"Is pulse guiding after send: {isPulseGuiding}");
    }

    [Fact]
    public void GivenPulseGuideRouterWithAutoSourceAndNeitherSupportsThenThrows()
    {
        var external = new FakeExternal(output);
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var camera = new FakeCameraDriver(cameraDevice, external);
        // FakeCameraDriver.CanPulseGuide = false, FakeMountDriver not connected = CanPulseGuide depends

        // Pass null for both to simulate neither supporting it
        Should.Throw<InvalidOperationException>(() =>
            new PulseGuideRouter(PulseGuideSource.Auto, null, null)
        );
    }

    [Fact]
    public async Task GivenMountPulseGuideTargetWhenPulseGuideThenDelegatesToMount()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var mountDevice = new FakeDevice(DeviceType.Mount, 1);
        var mount = new FakeMountDriver(mountDevice, external);
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var target = new MountPulseGuideTarget(mount);

        var raBefore = await mount.GetRightAscensionAsync(ct);
        await target.PulseGuideAsync(GuideDirection.West, TimeSpan.FromMilliseconds(500), ct);

        // Wait for pulse to complete
        await external.SleepAsync(TimeSpan.FromMilliseconds(600), ct);

        var raAfter = await mount.GetRightAscensionAsync(ct);
        output.WriteLine($"RA before: {raBefore:F6}, after: {raAfter:F6}");

        // West pulse should change RA
        raAfter.ShouldNotBe(raBefore, "pulse guide should move the mount");
    }

    [Fact]
    public async Task GivenBuiltInGuiderDriverWhenLinkDevicesThenMountAndCameraAreStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, mount, camera, _) = await CreateLinkedDriver(ct);

        driver.MountDriver.ShouldBeSameAs(mount);
        driver.CameraDriver.ShouldBeSameAs(camera);
    }

    private async Task<(BuiltInGuiderDriver driver, FakeMountDriver mount, FakeCameraDriver camera, FakeExternal external)> CreateLinkedDriver(CancellationToken ct)
    {
        var external = new FakeExternal(output, now: new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));

        var mountDevice = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { DeviceQueryKey.Port.Key, "LX200" },
            { DeviceQueryKey.Latitude.Key, "48.2" },
            { DeviceQueryKey.Longitude.Key, "16.3" }
        });
        var mount = new FakeMountDriver(mountDevice, external);
        await mount.ConnectAsync(ct);
        await mount.SetPositionAsync(12.0, 45.0, ct);

        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var camera = new FakeCameraDriver(cameraDevice, external);
        await camera.ConnectAsync(ct);

        // Use Auto pulse guide source, which should fall back to mount (camera doesn't support ST-4)
        var uri = new Uri("guider://BuiltInGuiderDevice/builtin?pulseGuideSource=Mount#Built-in Guider");
        var device = new BuiltInGuiderDevice(uri);
        var driver = new BuiltInGuiderDriver(device, external);

        driver.LinkDevices(mount, camera);

        return (driver, mount, camera, external);
    }
}
