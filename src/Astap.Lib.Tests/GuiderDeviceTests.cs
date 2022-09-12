﻿using Astap.Lib.Devices;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests
{
    public class GuiderDeviceTests
    {
        [Theory]

        [InlineData("device://guiderdevice/localhost/1?displayName=Profile#PHD2", "PHD2", "localhost/1", "Profile")]
        [InlineData("device://guiderdevice/localhost/1/Profile?displayName=Display#PHD2", "PHD2", "localhost/1/Profile", "Display")]
        public void GivenAnUriDisplayNameDeviceTypeAndClassAreReturned(string uriString, string expectedType, string expectedId, string expectedDisplayName)
        {
            var uri = new Uri(uriString);
            DeviceBase.TryFromUri(uri, out var device).ShouldBeTrue();

            device.DeviceClass.ShouldBe(device.GetType().Name, StringCompareShould.IgnoreCase);

            device.DeviceType.ShouldBe(expectedType);
            device.DeviceId.ShouldBe(expectedId);
            device.DisplayName.ShouldBe(expectedDisplayName);
        }
    }
}