using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Ascom;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.OpenPHD2;
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
    [InlineData(@"guider://OpenPHD2GuiderDevice/127.0.0.1/1/Test Profile#Test Profile", typeof(OpenPHD2GuiderDevice), DeviceType.Guider, "127.0.0.1/1/Test%20Profile", "Test Profile")]
    [InlineData(@"guider://OpenPHD2GuiderDevice/127.0.0.1/2/Profile/With/Slash#Slash Profile", typeof(OpenPHD2GuiderDevice), DeviceType.Guider, "127.0.0.1/2/Profile/With/Slash", "Slash Profile")]
    [InlineData(@"guider://OpenPHD2GuiderDevice/some.test/3/Profile#Host name profile", typeof(OpenPHD2GuiderDevice), DeviceType.Guider, "some.test/3/Profile", "Host name profile")]
    [InlineData(@"none://NoneDevice/None", typeof(NoneDevice), DeviceType.None, "None", "")]
    public void GivenAnUriDisplayNameDeviceTypeAndClassAreReturned(string uriString, Type containerType, DeviceType expectedDeviceType, string expectedId, string expectedDisplayName)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddAscom();
        serviceCollection.AddFake();
        serviceCollection.AddPHD2();
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