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
            Assert.NotEmpty(types);
        }
        else
        {
            Assert.Empty(types);
        }
    }

    [Fact]
    public void TestWhenPlatformIsWindowsThatTelescopesCanBeFound()
    {
        using var profile = new AscomProfile();
        var telescopes = profile.RegisteredDevices("Telescope");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.NotEmpty(telescopes);
        }
        else
        {
            Assert.Empty(telescopes);
        }
    }

    [Theory]
    [InlineData("Focuser")]
    [InlineData("CoverCalibrator")]
    [InlineData("Switch")]
    public void GivenSimulatorDeviceTypeVersionAndNameAreReturned(string type)
    {
        using var profile = new AscomProfile();
        var (progId, displayName) = profile.RegisteredDevices(type).FirstOrDefault(e => e.progId == $"ASCOM.Simulator.{type}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.NotNull(progId);
            Assert.NotNull(displayName);

            using var driver = new AscomDeviceDriver(new AscomDevice(progId, type, displayName));

            Assert.Equal(type, driver.DriverType);
            Assert.False(driver.Connected);
        }
        else
        {
            Assert.Null(progId);
            Assert.Null(displayName);
        }
    }
}
