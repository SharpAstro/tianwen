using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The one sampler of a connected device's state (<see cref="DeviceHubReadingExtensions"/>), which the GUI's Equipment
/// and Live Session tabs and a node's device plane read through alike (P2 part 1 of docs/plans/hardware-in-the-server.md,
/// #929).
/// </summary>
public class DeviceHubReadingTests(ITestOutputHelper output)
{
    private IDeviceHub Hub() => new FakeExternal(output).BuildServiceProvider().GetRequiredService<IDeviceHub>();

    [Fact]
    public async Task ACameraReadsItsCoolerAndGeometry()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = Hub();
        var device = new FakeDevice(DeviceType.Camera, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(device, ct);
        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);

        var reading = (await hub.ReadCameraAsync(device.DeviceUri, FakeExternal.CreateLogger(output), ct)).ShouldNotBeNull();

        reading.CoolerOn.ShouldBeTrue();
        reading.SetpointC.ShouldBe(-10);
        reading.State.ShouldBe(CameraState.Idle);
        reading.IsBusy.ShouldBeFalse();
        reading.SensorWidth.ShouldBe(camera.CameraXSize);
        reading.SensorHeight.ShouldBe(camera.CameraYSize);
    }

    [Fact]
    public async Task AFocuserReadsWhereItIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = Hub();
        var device = new FakeDevice(DeviceType.Focuser, 1);
        var focuser = (IFocuserDriver)await hub.ConnectAsync(device, ct);
        var expected = await focuser.GetPositionAsync(ct);

        var reading = (await hub.ReadFocuserAsync(device.DeviceUri, FakeExternal.CreateLogger(output), ct)).ShouldNotBeNull();

        reading.Position.ShouldBe(expected);
        reading.IsMoving.ShouldBeFalse();
    }

    [Fact]
    public async Task AFilterWheelReadsItsSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = Hub();
        var device = new FakeDevice(DeviceType.FilterWheel, 1);
        var filterWheel = (IFilterWheelDriver)await hub.ConnectAsync(device, ct);
        var expected = await filterWheel.GetCurrentFilterAsync(ct);

        var reading = (await hub.ReadFilterWheelAsync(device.DeviceUri, FakeExternal.CreateLogger(output), ct)).ShouldNotBeNull();

        reading.Position.ShouldBe(expected.Position);
        reading.FilterName.ShouldBe(expected.DisplayName);
    }

    [Fact]
    public async Task AFlipFlatReadsItsFlapAndItsLight()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = Hub();
        var device = new FakeDevice(DeviceType.CoverCalibrator, 1);
        var cover = (ICoverDriver)await hub.ConnectAsync(device, ct);
        await cover.BeginCalibratorOn(120, ct);
        var logger = FakeExternal.CreateLogger(output);

        var reading = (await hub.ReadCoverAsync(device.DeviceUri, logger, ct)).ShouldNotBeNull();

        reading.Cover.ShouldBe(CoverStatus.Closed);
        reading.Calibrator.ShouldBe(CalibratorStatus.Ready);
        reading.Brightness.ShouldBe(120);
        reading.MaxBrightness.ShouldBe(255);
        reading.CanControlBrightness.ShouldBeTrue();

        await cover.BeginOpen(ct);
        (await hub.ReadCoverAsync(device.DeviceUri, logger, ct)).ShouldNotBeNull().Cover.ShouldBe(CoverStatus.Moving);
    }

    // A light panel with no flap (the Gemini FlatPanel Lite) says so, rather than reading as a flap that is stuck.
    [Fact]
    public async Task ALightPanelWithNoFlapReadsItsFlapAsNotThere()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = Hub();
        var device = new FakeDevice(DeviceType.CoverCalibrator, 1, new System.Collections.Specialized.NameValueCollection
        {
            { DeviceQueryKey.HasCover.Key, "false" },
        });
        await hub.ConnectAsync(device, ct);

        var reading = (await hub.ReadCoverAsync(device.DeviceUri, FakeExternal.CreateLogger(output), ct)).ShouldNotBeNull();

        reading.Cover.ShouldBe(CoverStatus.NotPresent);
        reading.Calibrator.ShouldBe(CalibratorStatus.Off);
    }

    // A J2000 mount's coordinates ARE J2000: the transform a topocentric mount needs is never asked for, so a caller
    // that cannot make one (a profile with no site) has nothing to warn about.
    [Fact]
    public async Task AJ2000MountReadsItsPointingWithoutAskingForATransform()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = Hub();
        var device = new FakeDevice(DeviceType.Mount, 1);
        var mount = (IMountDriver)await hub.ConnectAsync(device, ct);
        var asked = 0;
        Transform? NoTransform()
        {
            asked++;
            return null;
        }

        var state = (await hub.ReadMountAsync(device.DeviceUri, NoTransform, FakeExternal.CreateLogger(output), ct)).ShouldNotBeNull();

        state.RightAscension.ShouldBe(await mount.GetRightAscensionAsync(ct), 1e-6);
        state.Declination.ShouldBe(await mount.GetDeclinationAsync(ct), 1e-6);
        state.RaJ2000.ShouldBe(state.RightAscension);
        state.DecJ2000.ShouldBe(state.Declination);
        asked.ShouldBe(0);
    }

    [Fact]
    public async Task ADeviceThatIsNotConnectedReadsAsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = Hub();
        var logger = FakeExternal.CreateLogger(output);

        (await hub.ReadCameraAsync(new FakeDevice(DeviceType.Camera, 7).DeviceUri, logger, ct)).ShouldBeNull();
        (await hub.ReadFocuserAsync(new FakeDevice(DeviceType.Focuser, 7).DeviceUri, logger, ct)).ShouldBeNull();
        (await hub.ReadFilterWheelAsync(new FakeDevice(DeviceType.FilterWheel, 7).DeviceUri, logger, ct)).ShouldBeNull();
        (await hub.ReadMountAsync(new FakeDevice(DeviceType.Mount, 7).DeviceUri, null, logger, ct)).ShouldBeNull();
        (await hub.ReadCoverAsync(new FakeDevice(DeviceType.CoverCalibrator, 7).DeviceUri, logger, ct)).ShouldBeNull();
    }
}
