using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.OpenPHD2;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests;

public class GuiderDeviceTests
{
    [Theory]
    [InlineData("guider://openphd2guiderdevice/localhost/1#Profile", DeviceType.Guider, "localhost/1", "Profile")]
    [InlineData("guider://openphd2guiderdevice/localhost/1/Profile#Display", DeviceType.Guider, "localhost/1/Profile", "Display")]
    public void GivenAnUriDisplayNameDeviceTypeAndClassAreReturned(string uriString, DeviceType expectedType, string expectedId, string expectedDisplayName)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddPHD2();
        serviceCollection.AddDevices();

        var provider = serviceCollection.BuildServiceProvider();
        var registry = provider.GetRequiredService<IDeviceUriRegistry>();

        var uri = new Uri(uriString);
        registry.TryGetDeviceFromUri(uri, out var device).ShouldBeTrue();

        device.DeviceClass.ShouldBe(device.GetType().Name, StringCompareShould.IgnoreCase);
        device.DeviceType.ShouldBe(expectedType);
        device.DeviceId.ShouldBe(expectedId);
        device.DisplayName.ShouldBe(expectedDisplayName);
    }

    [Theory]
    [InlineData("guider://OpenPHD2GuiderDevice/127.0.0.1/2/Profile/With/Slash#Slash Profile", 2, "127.0.0.1", "Profile/With/Slash", "Slash Profile")]
    public void GivenGuiderWithSlashInProfileNameWhenParsedThenProfileNameIsCorrect(string uriString, uint instanceId, string host, string profileName, string displayName)
    {
        var device = new OpenPHD2GuiderDevice(new Uri(uriString));
        device.DeviceType.ShouldBe(DeviceType.Guider);
        device.DeviceId.ShouldBe(OpenPHD2GuiderDevice.MakeDeviceId(host, instanceId, profileName));
        device.Host.ShouldBe(host);
        device.ProfileName.ShouldBe(profileName);
        device.DisplayName.ShouldBe(displayName);
    }
}