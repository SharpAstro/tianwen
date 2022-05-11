using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using Shouldly;
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomTests
{
    [Fact]
    public void TestWhenPlatformIsWindowsThatDeviceTypesAreReturned()
    {
        using var profile = new AscomProfile();
        var types = profile.RegisteredDeviceTypes;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            types.ShouldNotBeEmpty();
        }
        else
        {
            types.ShouldBeEmpty();
        }
    }

    [Fact]
    public void TestWhenPlatformIsWindowsThatTelescopesCanBeFound()
    {
        using var profile = new AscomProfile();
        var telescopes = profile.RegisteredDevices("Telescope");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            telescopes.ShouldNotBeEmpty();
        }
        else
        {
            telescopes.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("Focuser")]
    [InlineData("CoverCalibrator")]
    [InlineData("Switch")]
    public void GivenSimulatorDeviceTypeVersionAndNameAreReturned(string type)
    {
        using var profile = new AscomProfile();
        var devices = profile.RegisteredDevices(type);
        var device = devices.FirstOrDefault(e => e.DeviceId == $"ASCOM.Simulator.{type}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            device.ShouldNotBeNull();
            device.DeviceClass.ShouldBe(nameof(AscomDevice), StringCompareShould.IgnoreCase);
            device.DeviceId.ShouldNotBeNullOrEmpty();
            device.DeviceType.ShouldNotBeNullOrEmpty();
            device.DisplayName.ShouldNotBeNullOrEmpty();

            using var driver = new AscomDeviceDriver(device);

            Assert.Equal(type, driver.DriverType);
            Assert.False(driver.Connected);
        }
        else
        {
            Assert.Null(device);
        }
    }

    [Fact]
    public void GivenNoneDeviceItIsNoneClass()
    {
        var none = new NoneDevice();

        none.DeviceClass.ShouldBe(nameof(NoneDevice), StringCompareShould.IgnoreCase);
        none.DeviceId.ShouldBe("None");
        none.DisplayName.ShouldBe("");
        none.DeviceType.ShouldBe("None");
    }
}
