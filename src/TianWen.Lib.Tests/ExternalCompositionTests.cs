using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Linq;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.OpenPHD2;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>AddExternal()</c> composes on its own (#1985): <see cref="IExternal"/> holds no catalogue, so a host that needs
/// only a device driver (openphd2-multiscope's PHD2 proxy) does not have to register astrometry for it.
/// </summary>
public class ExternalCompositionTests
{
    [Fact]
    public void AddExternalAndAddPHD2ResolveWithoutAstrometry()
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddExternal()
            .AddPHD2()
            .BuildServiceProvider();

        provider.GetRequiredService<IExternal>().ShouldNotBeNull();
        provider.GetServices<IDeviceSource<DeviceBase>>().OfType<OpenPHD2GuiderDriver>().ShouldHaveSingleItem();

        var device = new OpenPHD2GuiderDevice(new Uri("guider://openphd2guiderdevice/localhost/1#Profile"));
        device.TryInstantiateDriver<IGuider>(provider, out var driver).ShouldBeTrue();
        driver.ShouldBeOfType<OpenPHD2GuiderDriver>();
    }
}
