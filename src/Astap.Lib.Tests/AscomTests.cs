using System.Runtime.InteropServices;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomTests
{
    [Fact]
    public void TestWhenPlatformIsWindowsThatDeviceTypesAreReturned()
    {
        var profile = new AscomProfile();
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
        var profile = new AscomProfile();
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
}
