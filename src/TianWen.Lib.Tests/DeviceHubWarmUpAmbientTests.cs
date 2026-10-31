using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A warm-up outside a session aims for the rig's own ambient when the camera has no heat sink, read from the profile holding
/// the camera (<see cref="RigAmbient.ForCameraAsync"/>): the OTA's focuser, then the profile's weather device, then +25 C.
/// </summary>
public class DeviceHubWarmUpAmbientTests(ITestOutputHelper output)
{
    // FakeFocuserDriver's and FakeWeatherDriver's own temperatures.
    private const double FocuserC = 15.0;
    private const double WeatherC = 12.5;

    [Theory(Timeout = 60_000)]
    [InlineData(true, true, FocuserC)]
    [InlineData(false, true, WeatherC)]
    [InlineData(true, false, FocuserC)]
    [InlineData(false, false, CameraReading.WarmUpFallbackTargetC)]
    public async Task AWarmUpOfACameraWithoutAHeatSinkStopsAtTheRigAmbient(bool focuserInProfile, bool weatherInProfile, double expectedC)
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var hub = external.BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var cameraDevice = new FakeDevice(DeviceType.Camera, 1);
        var focuserDevice = new FakeDevice(DeviceType.Focuser, 1);
        var weatherDevice = new FakeDevice(DeviceType.Weather, 1);
        var camera = (ICameraDriver)await hub.ConnectAsync(cameraDevice, ct);
        // The room is the fallback's +25 C, so the cooler still holds the sensor down until the ramp reaches any target below it.
        ((FakeCameraDriver)camera).MakeHeatsinkless(CameraReading.WarmUpFallbackTargetC, sensorCannotPassAmbient: false);
        await hub.ConnectAsync(focuserDevice, ct);
        await hub.ConnectAsync(weatherDevice, ct);

        var discovery = DiscoveryOf(ProfileOf(cameraDevice.DeviceUri, focuserInProfile ? focuserDevice.DeviceUri : null,
            weatherInProfile ? weatherDevice.DeviceUri : null));

        await camera.SetSetCCDTemperatureAsync(-10, ct);
        await camera.SetCoolerOnAsync(true, ct);
        for (var i = 0; i < 40 && await camera.GetCCDTemperatureAsync(ct) > -5; i++)
        {
            await external.TimeProvider.SleepAsync(TimeSpan.FromSeconds(30), ct);
        }

        await hub.WarmAndCoolerOffAsync(cameraDevice.DeviceUri, external.TimeProvider, NullLogger.Instance, ct, discovery);

        (await camera.GetSetCCDTemperatureAsync(ct)).ShouldBe(expectedC, 2.01, "the last setpoint is the target, never past it");
    }

    [Fact(Timeout = 30_000)]
    public async Task ACameraInNoProfileHasNoRigAmbient()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new FakeExternal(output).BuildServiceProvider().GetRequiredService<IDeviceHub>();
        var other = new FakeDevice(DeviceType.Camera, 2);
        var discovery = DiscoveryOf(ProfileOf(new FakeDevice(DeviceType.Camera, 1).DeviceUri, null, null));

        double.IsNaN(await RigAmbient.ForCameraAsync(hub, discovery, other.DeviceUri, ct)).ShouldBeTrue();
        double.IsNaN(await RigAmbient.ForCameraAsync(hub, null, other.DeviceUri, ct)).ShouldBeTrue();
    }

    private static Profile ProfileOf(Uri camera, Uri? focuser, Uri? weather)
        => new(Guid.NewGuid(), "Rig", new ProfileData(NoneDevice.Instance.DeviceUri, NoneDevice.Instance.DeviceUri,
            [new OTAData("OTA", 500, camera, null, focuser, null, null, null)], Weather: weather));

    private static IDeviceDiscovery DiscoveryOf(Profile profile)
    {
        var discovery = Substitute.For<IDeviceDiscovery>();
        discovery.RegisteredDevices(DeviceType.Profile).Returns([profile]);
        return discovery;
    }
}
