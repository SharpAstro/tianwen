using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using Astap.Lib.Devices.Builtin;
using CommunityToolkit.HighPerformance;
using Roydl.Text.BinaryToText;
using Shouldly;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

    [SkippableFact]
    public void TestWhenPlatformIsWindowsThatTelescopesCanBeFound()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        using var profile = new AscomProfile();
        var telescopes = profile.RegisteredDevices("Telescope");

        telescopes.ShouldNotBeEmpty();
    }

    [SkippableTheory]
    [InlineData("Camera")]
    [InlineData("CoverCalibrator")]
    [InlineData("Focuser")]
    [InlineData("Switch")]
    [InlineData("Telescope")]
    public void GivenSimulatorDeviceTypeVersionAndNameAreReturned(string type)
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        using var profile = new AscomProfile();
        var devices = profile.RegisteredDevices(type);
        var device = devices.FirstOrDefault(e => e.DeviceId == $"ASCOM.Simulator.{type}");

        device.ShouldNotBeNull();
        device.DeviceClass.ShouldBe(nameof(AscomDevice), StringCompareShould.IgnoreCase);
        device.DeviceId.ShouldNotBeNullOrEmpty();
        device.DeviceType.ShouldNotBeNullOrEmpty();
        device.DisplayName.ShouldNotBeNullOrEmpty();

        device.TryInstantiateDriver<IDeviceDriver>(out var driver).ShouldBeTrue();

        using (driver)
        {
            driver.DriverType.ShouldBe(type);
            driver.Connected.ShouldBeFalse();
        }
    }

    [SkippableFact]
    public void GivenAConnectedAscomSimulatorCameraWhenImageReadyThenItCanBeDownloaded()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // given
        const string Camera = nameof(Camera);

        using var profile = new AscomProfile();
        var allCameras = profile.RegisteredDevices(Camera);
        var simCameraDevice = allCameras.FirstOrDefault(e => e.DeviceId == "ASCOM.Simulator." + Camera);

        // when
        if (simCameraDevice?.TryInstantiateDriver(out ICameraDriver? driver) is true)
        {
            using (driver)
            {
                driver.Connected = true;
                driver.StartExposure(TimeSpan.FromSeconds(0.1), true);

                Thread.Sleep((int)TimeSpan.FromSeconds(0.5).TotalMilliseconds);
                driver.ImageReady.ShouldNotBeNull().ShouldBeTrue();
                var imgData = driver.ImageData;

                var image = driver.Image;

                // then
                var expectedMax = 0;
                foreach (var item in imgData.AsMemory().Span.Enumerate())
                {
                    expectedMax = Math.Max(item.Value, expectedMax);
                }

                driver.DriverType.ShouldBe(Camera);
                imgData.ShouldNotBeNull();
                image.ShouldNotBeNull();
                image.Width.ShouldBe(imgData.GetLength(0));
                image.Height.ShouldBe(imgData.GetLength(1));
                image.BitsPerPixel.ShouldBe(driver.BitDepth.ShouldNotBeNull());
                image.MaxValue.ShouldBeGreaterThan(0f);
                image.MaxValue.ShouldBe(expectedMax);
                var stars = image.FindStars(snr_min: 10);
                stars.ShouldNotBeEmpty();
            }
        }
        else
        {
            Assert.Fail($"Could not instantiate camera device {simCameraDevice}");
        }
    }

    [Theory]
    [InlineData(@"device://AscomDevice/EQMOD.Telescope?displayName=EQMOD ASCOM HEQ5/6#Telescope", "Telescope", "EQMOD.Telescope", "EQMOD ASCOM HEQ5/6")]
    [InlineData(@"device://ascomdevice/ASCOM.EAF.Focuser?displayName=ZWO Focuser (1)#Focuser", "Focuser", "ASCOM.EAF.Focuser", "ZWO Focuser (1)")]
    public void GivenAnUriDisplayNameDeviceTypeAndClassAreReturned(string uriString, string expectedType, string expectedId, string expectedDisplayName)
    {
        var uri = new Uri(uriString);
        DeviceBase.TryFromUri(uri, out var device).ShouldBeTrue();

        device.DeviceClass.ShouldBe(device.GetType().Name, StringCompareShould.IgnoreCase);

        device.DeviceType.ShouldBe(expectedType);
        device.DeviceId.ShouldBe(expectedId);
        device.DisplayName.ShouldBe(expectedDisplayName);
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
