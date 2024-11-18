using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Ascom;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests;

public class DeviceTests
{
    [Theory]
    [InlineData(@"telescope://AscomDevice/EQMOD.Telescope#EQMOD ASCOM HEQ5/6", typeof(AscomDevice), DeviceType.Telescope, "EQMOD.Telescope", "EQMOD ASCOM HEQ5/6")]
    [InlineData(@"Focuser://ascomdevice/ASCOM.EAF.Focuser#ZWO Focuser (1)", typeof(AscomDevice), DeviceType.Focuser, "ASCOM.EAF.Focuser", "ZWO Focuser (1)")]
    [InlineData(@"filterWheel://ascomDevice/ASCOM.EFW.FilterWheel#ZWO Filter Wheel #1", typeof(AscomDevice), DeviceType.FilterWheel, "ASCOM.EFW.FilterWheel", "ZWO Filter Wheel #1")]
    [InlineData(@"focuser://FakeDevice/FakeFocuser1#Fake Focuser 1", typeof(FakeDevice), DeviceType.Focuser, "FakeFocuser1", "Fake Focuser 1")]
    [InlineData(@"none://NoneDevice/None", typeof(NoneDevice), DeviceType.None, "None", "")]
    public void GivenAnUriDisplayNameDeviceTypeAndClassAreReturned(string uriString, Type containerType, DeviceType expectedDeviceType, string expectedId, string expectedDisplayName)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddAscom();
        serviceCollection.AddFake();
        serviceCollection.AddDevices();

        var provider = serviceCollection.BuildServiceProvider();
        var registry = provider.GetRequiredService<IDeviceUriRegistry>();

        var uri = new Uri(uriString);
        registry.TryGetDeviceFromUri(uri, out var device).ShouldBeTrue();

        device.ShouldNotBeNull();
        device.GetType().ShouldBe(containerType);
        device.DeviceClass.ShouldBe(device.GetType().Name, StringCompareShould.IgnoreCase);

        device.DeviceType.ShouldBe(expectedDeviceType);
        device.DeviceId.ShouldBe(expectedId);
        device.DisplayName.ShouldBe(expectedDisplayName);
    }
}
