using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Ascom;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class AscomDeviceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task TestWhenPlatformIsWindowsThatDeviceTypesAreReturned()
    {
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var types = deviceIterator.RegisteredDeviceTypes;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && await deviceIterator.CheckSupportAsync(TestContext.Current.CancellationToken))
        {
            types.ShouldNotBeEmpty();
        }
        else
        {
            types.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(DeviceType.Camera)]
    [InlineData(DeviceType.CoverCalibrator)]
    [InlineData(DeviceType.Focuser)]
    [InlineData(DeviceType.Switch)]
    public async Task GivenSimulatorDeviceTypeVersionAndNameAreReturned(DeviceType type)
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Debugger.IsAttached, "Skipped as this test is only run when on Windows and debugger is attached");

        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var devices = deviceIterator.RegisteredDevices(type);
        var device = devices.FirstOrDefault(e => e.DeviceId == $"ASCOM.Simulator.{type}");

        device.ShouldNotBeNull();
        device.DeviceClass.ShouldBe(nameof(AscomDevice), StringCompareShould.IgnoreCase);
        device.DeviceId.ShouldNotBeNullOrEmpty();
        device.DeviceType.ShouldBe(type);
        device.DisplayName.ShouldNotBeNullOrEmpty();

        var sp = new ServiceCollection().AddSingleton<IExternal>(external).BuildServiceProvider();
        device.TryInstantiateDriver<IDeviceDriver>(sp, out var driver).ShouldBeTrue();

        await using (driver)
        {
            driver.DriverType.ShouldBe(type);
            driver.Connected.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task GivenAConnectedAscomSimulatorTelescopeWhenConnectedThenTrackingRatesArePopulated()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Debugger.IsAttached, "Skipped as this test is only run when on Windows and debugger is attached");

        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var allTelescopes = deviceIterator.RegisteredDevices(DeviceType.Telescope);
        var simTelescopeDevice = allTelescopes.FirstOrDefault(e => e.DeviceId == "ASCOM.Simulator." + DeviceType.Telescope);

        // when
        var sp = new ServiceCollection().AddSingleton<IExternal>(external).BuildServiceProvider();
        if (simTelescopeDevice?.TryInstantiateDriver(sp, out IMountDriver? driver) is true)
        {
            await using (driver)
            {
                await driver.DisconnectAsync(cancellationToken);
            }
        }
    }

    [Fact]
    public async Task GivenAConnectedAscomSimulatorCameraWhenImageReadyThenItCanBeDownloaded()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Debugger.IsAttached, "Skipped as this test is only run when on Windows and debugger is attached");

        // given
        const int channel = 0;
        var cancellationToken = TestContext.Current.CancellationToken;
        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var allCameras = deviceIterator.RegisteredDevices(DeviceType.Camera);
        var simCameraDevice = allCameras.FirstOrDefault(e => e.DeviceId == "ASCOM.Simulator." + DeviceType.Camera);

        // when / then
        var sp2 = new ServiceCollection().AddSingleton<IExternal>(external).BuildServiceProvider();
        if (simCameraDevice?.TryInstantiateDriver(sp2, out ICameraDriver? driver) is true)
        {
            await using (driver)
            {
                await driver.ConnectAsync(cancellationToken);
                var startExposure = await driver.StartExposureAsync(TimeSpan.FromSeconds(0.1), cancellationToken: cancellationToken);

                Thread.Sleep((int)TimeSpan.FromSeconds(0.5).TotalMilliseconds);
                (await driver.GetImageReadyAsync(cancellationToken)).ShouldBeTrue();
                var ch = driver.ImageData.ShouldNotBeNull(); var data = ch.Data; var expectedMax = ch.MaxValue; var expectedMin = ch.MinValue;

                var image = (await driver.GetImageAsync(cancellationToken)).ShouldNotBeNull();

                driver.DriverType.ShouldBe(DeviceType.Camera);
                image.ImageMeta.ExposureStartTime.ShouldBe(startExposure);
                image.Width.ShouldBe(data.GetLength(1));
                image.Height.ShouldBe(data.GetLength(0));
                image.BitDepth.ShouldBe((await driver.GetBitDepthAsync(cancellationToken)).ShouldNotBeNull());
                image.MaxValue.ShouldBeGreaterThan(0f);
                image.MaxValue.ShouldBe(expectedMax);
                image.MinValue.ShouldBe(expectedMin);
                var stars = await image.FindStarsAsync(channel, snrMin: 10, cancellationToken: cancellationToken);
                stars.Count.ShouldBeGreaterThan(0);
            }
        }
        else
        {
            Assert.Fail($"Could not instantiate camera device {simCameraDevice}");
        }
    }
}
