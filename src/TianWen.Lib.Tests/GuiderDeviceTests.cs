using TianWen.Lib.Devices;
using Shouldly;
using System;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Tests
{
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
    }
}
