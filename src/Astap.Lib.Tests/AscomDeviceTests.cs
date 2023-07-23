using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using Astap.Lib.Devices.Builtin;
using CommunityToolkit.HighPerformance;
using Shouldly;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomDeviceTests
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

        using var profile = new AscomProfile();
        var devices = profile.RegisteredDevices(type);
        var device = devices.FirstOrDefault(e => e.DeviceId == $"ASCOM.Simulator.{type}");

        device.ShouldNotBeNull();
        device.DeviceClass.ShouldBe(nameof(AscomDevice), StringCompareShould.IgnoreCase);
        device.DeviceId.ShouldNotBeNullOrEmpty();
        device.DeviceType.ShouldBe(type);
        device.DisplayName.ShouldNotBeNullOrEmpty();

        device.TryInstantiateDriver<IDeviceDriver>(out var driver).ShouldBeTrue();

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
        using var profile = new AscomProfile();
        var allTelescopes = profile.RegisteredDevices(DeviceType.Telescope);
        var simTelescopeDevice = allTelescopes.FirstOrDefault(e => e.DeviceId == "ASCOM.Simulator." + DeviceType.Telescope);

        // when
        if (simTelescopeDevice?.TryInstantiateDriver(out IMountDriver? driver) is true)
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
        using var profile = new AscomProfile();
        var allCameras = profile.RegisteredDevices(DeviceType.Camera);
        var simCameraDevice = allCameras.FirstOrDefault(e => e.DeviceId == "ASCOM.Simulator." + DeviceType.Camera);

        // when / then
        if (simCameraDevice?.TryInstantiateDriver(out ICameraDriver? driver) is true)
        {
            using (driver)
            {
                driver.Connected = true;
                driver.StartExposure(TimeSpan.FromSeconds(0.1), true);

                Thread.Sleep((int)TimeSpan.FromSeconds(0.5).TotalMilliseconds);
                driver.ImageReady.ShouldBeTrue();
                var (data, expectedMax) = driver.ImageData.ShouldNotBeNull();

                var image = driver.Image.ShouldNotBeNull();

                driver.DriverType.ShouldBe(DeviceType.Camera);
                image.ShouldNotBeNull();
                image.Width.ShouldBe(data.GetLength(0));
                image.Height.ShouldBe(data.GetLength(1));
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

    [Theory]
    [InlineData(@"telescope://AscomDevice/EQMOD.Telescope#EQMOD ASCOM HEQ5/6", DeviceType.Telescope, "EQMOD.Telescope", "EQMOD ASCOM HEQ5/6")]
    [InlineData(@"Focuser://ascomdevice/ASCOM.EAF.Focuser#ZWO Focuser (1)", DeviceType.Focuser, "ASCOM.EAF.Focuser", "ZWO Focuser (1)")]
    [InlineData(@"filterWheel://ascomDevice/ASCOM.EFW.FilterWheel#ZWO Filter Wheel #1", DeviceType.FilterWheel, "ASCOM.EFW.FilterWheel", "ZWO Filter Wheel #1")]
    public void GivenAnUriDisplayNameDeviceTypeAndClassAreReturned(string uriString, DeviceType expectedType, string expectedId, string expectedDisplayName)
    {
        var uri = new Uri(uriString);
        DeviceBase.TryFromUri(uri, out var device).ShouldBeTrue();

        device.DeviceClass.ShouldBe(device.GetType().Name, StringCompareShould.IgnoreCase);

        device.DeviceType.ShouldBe(expectedType);
        device.DeviceId.ShouldBe(expectedId);
        device.DisplayName.ShouldBe(expectedDisplayName);
    }

    [Fact]
    public void GivenNoneDeviceItIsNoneClass()
    {
        var none = new NoneDevice();

        none.DeviceClass.ShouldBe(nameof(NoneDevice), StringCompareShould.IgnoreCase);
        none.DeviceId.ShouldBe("None");
        none.DisplayName.ShouldBe("");
        none.DeviceType.ShouldBe(DeviceType.None);
    }
}
