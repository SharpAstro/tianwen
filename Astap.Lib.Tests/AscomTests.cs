using System.Runtime.InteropServices;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomTests
{
    [Fact]
    public void TestWhenPlatformIsWindowsThatDeviceTypesAreReturned()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var profile = new AscomProfile();
            var types = profile.RegisteredDeviceTypes;

            Assert.NotEmpty(types);
        }
    }

    [Fact]
    public void TestWhenPlatformIsWindowsThatTelescopesCanBeFound()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var profile = new AscomProfile();
            var telescopes = profile.RegisteredDevices("Telescope");

            Assert.NotEmpty(telescopes);
        }
    }
}
