using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A run's drivers live in the hub (P0b item 11 of docs/plans/hardware-in-the-server.md, #752): a wrapper
/// hands the hub the driver it built (<see cref="IDeviceHub.AdoptAsync"/>), so the node holds ONE driver per
/// device, the instance its caller configured, and switches to the hub's own when the hub already holds one
/// connected. The session end to end: <c>SessionLifecycleTests.GivenNothingPreConnectedWhenInitialisationThenTheHubHoldsTheSessionsOwnDrivers</c>.
/// </summary>
public class DeviceHubAdoptionTests(ITestOutputHelper output)
{
    private (IServiceProvider Services, IDeviceHub Hub) Build()
    {
        var services = new FakeExternal(output).BuildServiceProvider();
        return (services, services.GetRequiredService<IDeviceHub>());
    }

    [Fact]
    public async Task AnAdoptedDriverIsTheOneTheHubHandsToEveryone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (services, hub) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        device.TryInstantiateDriver<ICameraDriver>(services, out var own).ShouldBeTrue();

        (await hub.AdoptAsync(device, own, ct)).ShouldBeSameAs(own);

        own.Connected.ShouldBeTrue();
        hub.TryGetConnectedDriver<ICameraDriver>(device.DeviceUri, out var held).ShouldBeTrue();
        held.ShouldBeSameAs(own);
        (await hub.ConnectAsync(device, ct)).ShouldBeSameAs(own, "a second connect, the Alpaca plane's, opens no second driver");
    }

    [Fact]
    public async Task AWrappersOwnDriverBecomesTheHubsWithWhatItsCallerConfiguredOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (services, hub) = Build();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = new Camera(device, services);
        ((FakeCameraDriver)camera.Driver).TrueBestFocus = 1234;

        await camera.ConnectAsync(hub, ct);

        hub.TryGetConnectedDriver<ICameraDriver>(device.DeviceUri, out var held).ShouldBeTrue();
        held.ShouldBeSameAs(camera.Driver);
        ((FakeCameraDriver)held).TrueBestFocus.ShouldBe(1234, "a second instance would have lost what its caller set");

        // The hub's now: disposing the wrapper, as a finished session does, leaves it connected there.
        camera.Borrowed.ShouldBeTrue();
        await camera.DisposeAsync();
        held.Connected.ShouldBeTrue();
    }

    [Fact]
    public async Task AWrapperSwitchesToADriverTheHubConnectedAfterItWasBuilt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (services, hub) = Build();
        var device = new FakeDevice(DeviceType.Focuser, 1);
        var focuser = new Focuser(device, services);
        var own = focuser.Driver;
        focuser.Borrowed.ShouldBeFalse("nothing held it when the wrapper was built");

        // Something else connected the device through the hub in the meantime (an Alpaca client, say).
        var hubs = await hub.ConnectAsync(device, ct);
        await focuser.ConnectAsync(hub, ct);

        focuser.Driver.ShouldBeSameAs(hubs);
        focuser.Borrowed.ShouldBeTrue();
        own.Connected.ShouldBeFalse("its own driver never connected, so the device has one driver");
    }

    [Fact]
    public async Task AnEntryWhoseDriverWentDownIsReplacedByTheNextAdoption()
    {
        var ct = TestContext.Current.CancellationToken;
        var (services, hub) = Build();
        var device = new FakeDevice(DeviceType.Mount, 1);
        device.TryInstantiateDriver<IMountDriver>(services, out var first).ShouldBeTrue();
        await hub.AdoptAsync(device, first, ct);

        // A run's Finalise disconnects the mount it drove, straight through its driver.
        await first.DisconnectAsync(ct);

        device.TryInstantiateDriver<IMountDriver>(services, out var second).ShouldBeTrue();
        (await hub.AdoptAsync(device, second, ct)).ShouldBeSameAs(second);
        hub.TryGetConnectedDriver<IMountDriver>(device.DeviceUri, out var held).ShouldBeTrue();
        held.ShouldBeSameAs(second);
    }

    /// <summary>
    /// Every run now ends with such entries: <c>Finalise</c> parks the mount and disconnects it, and the
    /// guider, straight through drivers the hub holds, while the cameras it only warms stay connected. A
    /// listing that kept the down ones had the profile-switch gate name a parked mount as connected and the
    /// limit watcher evaluate it every tick, against a contract that says "currently connected".
    /// </summary>
    [Fact]
    public async Task ADeviceWhoseDriverWentDownIsNoLongerListedAsConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (services, hub) = Build();
        var mountDevice = new FakeDevice(DeviceType.Mount, 1);
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        mountDevice.TryInstantiateDriver<IMountDriver>(services, out var mount).ShouldBeTrue();
        cameraDevice.TryInstantiateDriver<ICameraDriver>(services, out var camera).ShouldBeTrue();
        await hub.AdoptAsync(mountDevice, mount, ct);
        await hub.AdoptAsync(cameraDevice, camera, ct);

        await mount.DisconnectAsync(ct);

        hub.ConnectedDevices.ShouldHaveSingleItem().Driver.ShouldBeSameAs(camera);
        hub.IsConnected(mountDevice.DeviceUri).ShouldBeFalse();
        ProfileSwitchGate.Evaluate(hub, sessionActive: false).ConnectedDevices.ShouldHaveSingleItem().ShouldStartWith("Camera");
    }

    /// <summary>
    /// A fake camera renders against whatever mount the hub holds, and a session's initialisation now puts
    /// its mount there, so hub presence can no longer be the switch a test turns coupling off with (coupled,
    /// one loop test takes 3 minutes of its 5-minute budget instead of 5 s). The opt-out lives on the camera's
    /// own driver instead, which is the instance the hub adopts.
    /// </summary>
    [Fact]
    public async Task ACameraToldNotToCoupleStaysUncoupledOnceItsSessionAdoptsTheMount()
    {
        var ct = TestContext.Current.CancellationToken;
        var (services, hub) = Build();
        var coupled = new Camera(new FakeDevice(DeviceType.Camera, 1), services);
        var uncoupled = new Camera(new FakeDevice(DeviceType.Camera, 2), services);
        ((FakeCameraDriver)uncoupled.Driver).CouplesToMount = false;
        var mount = new Mount(new FakeDevice(DeviceType.Mount, 1), services);

        // What a session's initialisation does: every device through the hub, the mount included.
        await mount.ConnectAsync(hub, ct);
        await coupled.ConnectAsync(hub, ct);
        await uncoupled.ConnectAsync(hub, ct);

        ((FakeCameraDriver)coupled.Driver).ResolveCoupledMount().ShouldBeSameAs(mount.Driver, "premise: a mount in the hub couples a fake camera");
        ((FakeCameraDriver)uncoupled.Driver).ResolveCoupledMount().ShouldBeNull();
    }
}
