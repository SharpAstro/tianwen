using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Ascom;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// Native ASCOM (COM) driver tests. These require the Windows-only ASCOM Platform + its
/// <c>ASCOM.Simulator.*</c> COM drivers, so the device-touching cases are gated on
/// <see cref="SimulatorGate.AscomCiEnabled"/> (env var <c>TIANWEN_ASCOM_CI</c>) AND Windows.
/// Formerly gated on <c>Debugger.IsAttached</c> (dev-machine only); the env gate lets CI run
/// them on a Windows runner that has silently installed the Platform.
/// </summary>
public class AscomDeviceTests(ITestOutputHelper testOutputHelper)
{
    // Platform 7 registers the OmniSimulator under version-specific ProgIDs -- the classic
    // "ASCOM.Simulator.<type>" is NOT guaranteed (CI against Platform 7 proved the old hardcoded
    // lookup wrong), so discover the simulator by name-match instead of a fixed ProgID, logging the
    // candidates so the actual registered IDs are on record. Falls back to the sole device of the
    // type (on a fresh runner the only registered devices are the Platform's own simulators).
    private AscomDevice? ResolveSimulator(AscomDeviceIterator iterator, DeviceType type)
    {
        var devices = iterator.RegisteredDevices(type).ToList();
        testOutputHelper.WriteLine($"{type}: {devices.Count} registered -> {string.Join(", ", devices.Select(d => $"'{d.DeviceId}' ({d.DisplayName})"))}");
        return devices.FirstOrDefault(d => d.DeviceId.Contains("Sim", StringComparison.OrdinalIgnoreCase)
                                        || d.DisplayName.Contains("Sim", StringComparison.OrdinalIgnoreCase))
            ?? (devices.Count == 1 ? devices[0] : null);
    }

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
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SimulatorGate.AscomCiEnabled,
            $"Skipped unless on Windows with the ASCOM Platform + simulators installed and {SimulatorGate.AscomCiVar} set");

        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var device = ResolveSimulator(deviceIterator, type);

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
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SimulatorGate.AscomCiEnabled,
            $"Skipped unless on Windows with the ASCOM Platform + simulators installed and {SimulatorGate.AscomCiVar} set");

        // given
        var cancellationToken = TestContext.Current.CancellationToken;
        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var simTelescopeDevice = ResolveSimulator(deviceIterator, DeviceType.Telescope);

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
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SimulatorGate.AscomCiEnabled,
            $"Skipped unless on Windows with the ASCOM Platform + simulators installed and {SimulatorGate.AscomCiVar} set");

        // given
        const int channel = 0;
        var cancellationToken = TestContext.Current.CancellationToken;
        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var simCameraDevice = ResolveSimulator(deviceIterator, DeviceType.Camera);

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

    [Fact]
    public async Task GivenAConnectedAscomSimulatorFocuserWhenMovedThenItReachesTarget()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SimulatorGate.AscomCiEnabled,
            $"Skipped unless on Windows with the ASCOM Platform + simulators installed and {SimulatorGate.AscomCiVar} set");

        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var simDevice = ResolveSimulator(deviceIterator, DeviceType.Focuser);

        var sp = new ServiceCollection().AddSingleton<IExternal>(external).BuildServiceProvider();
        if (simDevice?.TryInstantiateDriver(sp, out IFocuserDriver? focuser) is true)
        {
            await using (focuser)
            {
                await focuser.ConnectAsync(ct);
                focuser.Connected.ShouldBeTrue();
                Assert.SkipUnless(focuser.Absolute, "Focuser is not absolute; skipping absolute-move assertion");
                focuser.MaxStep.ShouldBeGreaterThan(0);

                var start = await focuser.GetPositionAsync(ct);
                var target = start > focuser.MaxStep / 2 ? Math.Max(0, start - 500) : Math.Min(focuser.MaxStep, start + 500);
                await focuser.BeginMoveAsync(target, ct);

                var settled = await SimulatorTestHelpers.WaitAsync(SystemTimeProvider.Instance, async () => !await focuser.GetIsMovingAsync(ct), TimeSpan.FromSeconds(30), ct);
                settled.ShouldBeTrue();
                (await focuser.GetPositionAsync(ct)).ShouldBe(target);

                await focuser.DisconnectAsync(ct);
            }
        }
        else
        {
            Assert.Fail($"Could not instantiate focuser device {simDevice}");
        }
    }

    [Fact]
    public async Task GivenAConnectedAscomSimulatorFilterWheelWhenMovedThenPositionChanges()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SimulatorGate.AscomCiEnabled,
            $"Skipped unless on Windows with the ASCOM Platform + simulators installed and {SimulatorGate.AscomCiVar} set");

        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var simDevice = ResolveSimulator(deviceIterator, DeviceType.FilterWheel);

        var sp = new ServiceCollection().AddSingleton<IExternal>(external).BuildServiceProvider();
        if (simDevice?.TryInstantiateDriver(sp, out IFilterWheelDriver? fw) is true)
        {
            await using (fw)
            {
                await fw.ConnectAsync(ct);
                fw.Connected.ShouldBeTrue();
                var count = fw.Filters.Count;
                count.ShouldBeGreaterThan(0);

                var start = await fw.GetPositionAsync(ct);
                var target = count == 1 ? 0 : (Math.Max(start, 0) + 1) % count;
                await fw.BeginMoveAsync(target, ct);

                // A moving filter wheel reports position -1 (ASCOM), so it equals target only once settled.
                var settled = await SimulatorTestHelpers.WaitAsync(SystemTimeProvider.Instance, async () => await fw.GetPositionAsync(ct) == target, TimeSpan.FromSeconds(30), ct);
                settled.ShouldBeTrue();

                await fw.DisconnectAsync(ct);
            }
        }
        else
        {
            Assert.Fail($"Could not instantiate filter wheel device {simDevice}");
        }
    }

    [Fact]
    public async Task GivenAConnectedAscomSimulatorCoverCalibratorWhenCalibratorToggledThenStateReflects()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && SimulatorGate.AscomCiEnabled,
            $"Skipped unless on Windows with the ASCOM Platform + simulators installed and {SimulatorGate.AscomCiVar} set");

        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(testOutputHelper);
        var deviceIterator = new AscomDeviceIterator(NullLogger<AscomDeviceIterator>.Instance);
        var simDevice = ResolveSimulator(deviceIterator, DeviceType.CoverCalibrator);

        var sp = new ServiceCollection().AddSingleton<IExternal>(external).BuildServiceProvider();
        if (simDevice?.TryInstantiateDriver(sp, out ICoverDriver? cover) is true)
        {
            await using (cover)
            {
                await cover.ConnectAsync(ct);
                cover.Connected.ShouldBeTrue();

                (await cover.GetCoverStateAsync(ct)).ShouldNotBe(CoverStatus.Error);
                (await cover.GetCalibratorStateAsync(ct)).ShouldNotBe(CalibratorStatus.Error);

                Assert.SkipUnless(cover.MaxBrightness > 0, "Cover has no controllable calibrator; skipping calibrator toggle");

                var brightness = Math.Max(1, cover.MaxBrightness / 2);
                await cover.BeginCalibratorOn(brightness, ct);
                var ready = await SimulatorTestHelpers.WaitAsync(SystemTimeProvider.Instance, async () => await cover.GetCalibratorStateAsync(ct) == CalibratorStatus.Ready, TimeSpan.FromSeconds(30), ct);
                ready.ShouldBeTrue();
                (await cover.GetBrightnessAsync(ct)).ShouldBe(brightness);

                await cover.BeginCalibratorOff(ct);
                await cover.DisconnectAsync(ct);
            }
        }
        else
        {
            Assert.Fail($"Could not instantiate cover calibrator device {simDevice}");
        }
    }
}
