using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Ascom;
using Shouldly;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace TianWen.Lib.Tests;

public class AscomDeviceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void TestWhenPlatformIsWindowsThatDeviceTypesAreReturned()
    {
        using var profile = new AscomProfile();
        var types = profile.RegisteredDeviceTypes;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && profile.IsSupported)
        {
            types.ShouldNotBeEmpty();
        }
        else
        {
            types.ShouldBeEmpty();
        }
    }

    [SkippableTheory]
    [InlineData(DeviceType.Camera)]
    [InlineData(DeviceType.CoverCalibrator)]
    [InlineData(DeviceType.Focuser)]
    [InlineData(DeviceType.Switch)]
    public void GivenSimulatorDeviceTypeVersionAndNameAreReturned(DeviceType type)
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Debugger.IsAttached);

        var external = new FakeExternal(testOutputHelper);
        using var profile = new AscomProfile();
        var devices = profile.RegisteredDevices(type);
        var device = devices.FirstOrDefault(e => e.DeviceId == $"ASCOM.Simulator.{type}");

        device.ShouldNotBeNull();
        device.DeviceClass.ShouldBe(nameof(AscomDevice), StringCompareShould.IgnoreCase);
        device.DeviceId.ShouldNotBeNullOrEmpty();
        device.DeviceType.ShouldBe(type);
        device.DisplayName.ShouldNotBeNullOrEmpty();

        device.TryInstantiateDriver<IDeviceDriver>(external, out var driver).ShouldBeTrue();

        using (driver)
        {
            driver.DriverType.ShouldBe(type);
            driver.Connected.ShouldBeFalse();
        }
    }

    [SkippableFact]
    public void GivenAConnectedAscomSimulatorTelescopeWhenConnectedThenTrackingRatesArePopulated()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Debugger.IsAttached, "Skipped as this test is only run when on Windows and debugger is attached");

        // given
        var external = new FakeExternal(testOutputHelper);
        using var profile = new AscomProfile();
        var allTelescopes = profile.RegisteredDevices(DeviceType.Telescope);
        var simTelescopeDevice = allTelescopes.FirstOrDefault(e => e.DeviceId == "ASCOM.Simulator." + DeviceType.Telescope);

        // when
        if (simTelescopeDevice?.TryInstantiateDriver(external, out IMountDriver? driver) is true)
        {
            using (driver)
            {
                driver.Connected = true;
            }
        }
    }

    [SkippableFact]
    public void GivenAConnectedAscomSimulatorCameraWhenImageReadyThenItCanBeDownloaded()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Debugger.IsAttached, "Skipped as this test is only run when on Windows and debugger is attached");

        // given
        var external = new FakeExternal(testOutputHelper);
        using var profile = new AscomProfile();
        var allCameras = profile.RegisteredDevices(DeviceType.Camera);
        var simCameraDevice = allCameras.FirstOrDefault(e => e.DeviceId == "ASCOM.Simulator." + DeviceType.Camera);

        // when / then
        if (simCameraDevice?.TryInstantiateDriver(external, out ICameraDriver? driver) is true)
        {
            using (driver)
            {
                driver.Connected = true;
                var startExposure = driver.StartExposure(TimeSpan.FromSeconds(0.1));

                Thread.Sleep((int)TimeSpan.FromSeconds(0.5).TotalMilliseconds);
                driver.ImageReady.ShouldBeTrue();
                var (data, expectedMax) = driver.ImageData.ShouldNotBeNull();

                var image = driver.Image.ShouldNotBeNull();

                driver.DriverType.ShouldBe(DeviceType.Camera);
                image.ImageMeta.ExposureStartTime.ShouldBe(startExposure);
                image.Width.ShouldBe(data.GetLength(1));
                image.Height.ShouldBe(data.GetLength(0));
                image.BitDepth.ShouldBe(driver.BitDepth.ShouldNotBeNull());
                image.MaxValue.ShouldBeGreaterThan(0f);
                image.MaxValue.ShouldBe(expectedMax);
                var stars = image.FindStars(snr_min: 10);
                stars.ShouldNotBeEmpty();
            }
        }
        else
        {
            Assert.Fail($"Could not instantiate camera device {simCameraDevice}");
        }
    }
}
