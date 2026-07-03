using Meziantou.Extensions.Logging.Xunit.v3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Alpaca;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests.Simulators;

/// <summary>
/// End-to-end tests that drive the Alpaca device drivers against a live ASCOM Alpaca ("OmniSim")
/// server over HTTP. Opt-in via <c>TIANWEN_ALPACA_SIM</c> (see <see cref="SimulatorGate"/>); a bare
/// <c>dotnet test</c> without the env var skips every case. Devices are resolved through the real
/// management API (<c>/management/v1/configureddevices</c>) rather than UDP discovery, which is
/// unreliable on CI runners -- this still exercises the client, the drivers, and (for the camera)
/// the binary ImageBytes transfer, which was previously unit-pinned but never verified against a
/// real server.
/// </summary>
public class AlpacaSimulatorTests(ITestOutputHelper testOutputHelper)
{
    private static string RequireSim()
    {
        var baseUrl = SimulatorGate.AlpacaBaseUrl;
        Assert.SkipUnless(baseUrl is not null,
            $"Set {SimulatorGate.AlpacaBaseUrlVar} to a running Alpaca simulator base URL (e.g. http://localhost:11111)");
        return baseUrl!;
    }

    /// <summary>Builds a service provider wired exactly like the app (<see cref="AlpacaServiceCollectionExtensions.AddAlpaca"/>),
    /// so the resolved <see cref="AlpacaClient"/> + drivers are the production types. Uses <see cref="FakeExternal"/>
    /// for the IExternal/ITimeProvider/logging trio the driver base resolves from DI.</summary>
    private (ServiceProvider Sp, AlpacaClient Client) BuildAlpaca()
    {
        var external = new FakeExternal(testOutputHelper);
        var sp = new ServiceCollection()
            .AddSingleton<IExternal>(external)
            .AddSingleton<ITimeProvider>(external.TimeProvider)
            .AddLogging(b => b.AddProvider(new XUnitLoggerProvider(testOutputHelper, false)))
            .AddAlpaca()
            .BuildServiceProvider();
        return (sp, sp.GetRequiredService<AlpacaClient>());
    }

    /// <summary>Resolves the first configured device of <paramref name="type"/> via the management API and
    /// builds a directly-addressed <see cref="AlpacaDevice"/> for it (no UDP discovery). Skips the test if
    /// the simulator exposes no device of that type.</summary>
    private static async Task<AlpacaDevice> ResolveDeviceAsync(AlpacaClient client, string baseUrl, DeviceType type, CancellationToken ct)
    {
        var configured = await client.GetConfiguredDevicesAsync(baseUrl, ct);
        configured.ShouldNotBeNull();
        var match = configured.FirstOrDefault(d => DeviceTypeHelper.TryParseDeviceType(d.DeviceType) == type);
        Assert.SkipUnless(match is not null, $"Alpaca simulator at {baseUrl} exposes no {type} device");

        var uri = new Uri(baseUrl);
        return new AlpacaDevice(type, match!.UniqueID, uri.Host, uri.Port, match.DeviceNumber, match.DeviceName) { Client = client };
    }

    /// <summary>Polls <paramref name="condition"/> against a real clock (the fake IExternal clock would
    /// busy-spin) until it returns true or the timeout elapses. The driver calls used here do not sleep
    /// internally, so the caller owns the wait.</summary>
    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition()) return true;
            await Task.Delay(200, ct);
        }
        return await condition();
    }

    [Fact]
    public async Task ManagementApi_ListsConfiguredDevices()
    {
        var baseUrl = RequireSim();
        var ct = TestContext.Current.CancellationToken;
        var (sp, client) = BuildAlpaca();
        await using (sp)
        {
            var devices = await client.GetConfiguredDevicesAsync(baseUrl, ct);
            devices.ShouldNotBeNull();
            devices.ShouldNotBeEmpty();
            testOutputHelper.WriteLine($"OmniSim at {baseUrl} exposes {devices.Count} device(s): " +
                string.Join(", ", devices.Select(d => $"{d.DeviceType}#{d.DeviceNumber} '{d.DeviceName}'")));
            devices.Select(d => DeviceTypeHelper.TryParseDeviceType(d.DeviceType)).ShouldContain(DeviceType.Camera);
        }
    }

    [Fact]
    public async Task Camera_ExposesAndDownloadsViaImageBytes()
    {
        var baseUrl = RequireSim();
        var ct = TestContext.Current.CancellationToken;
        var (sp, client) = BuildAlpaca();
        await using (sp)
        {
            var device = await ResolveDeviceAsync(client, baseUrl, DeviceType.Camera, ct);
            device.TryInstantiateDriver<ICameraDriver>(sp, out var cam).ShouldBeTrue();
            await using (cam)
            {
                await cam.ConnectAsync(ct);
                cam.Connected.ShouldBeTrue();

                // Full frame at the simulator's default ROI/binning (StartExposureAsync sends neither).
                await cam.StartExposureAsync(TimeSpan.FromSeconds(1), cancellationToken: ct);

                var ready = await WaitAsync(async () => await cam.GetImageReadyAsync(ct), TimeSpan.FromSeconds(30), ct);
                ready.ShouldBeTrue();

                // The payoff: GetImageAsync downloads via GetImageArrayBytesAsync (Accept:
                // application/imagebytes) and decodes with AlpacaImageBytes.DecodeChannel -- the
                // binary transfer that was unit-pinned but never round-tripped against a live server.
                var image = (await cam.GetImageAsync(ct)).ShouldNotBeNull();
                image.Width.ShouldBeGreaterThan(0);
                image.Height.ShouldBeGreaterThan(0);
                image.MaxValue.ShouldBeGreaterThan(0f);

                var channel = cam.ImageData.ShouldNotBeNull();
                channel.Data.GetLength(1).ShouldBe(image.Width);
                channel.Data.GetLength(0).ShouldBe(image.Height);

                await cam.DisconnectAsync(ct);
            }
        }
    }

    [Fact]
    public async Task Telescope_ConnectsReadsCoordinatesAndTracking()
    {
        var baseUrl = RequireSim();
        var ct = TestContext.Current.CancellationToken;
        var (sp, client) = BuildAlpaca();
        await using (sp)
        {
            var device = await ResolveDeviceAsync(client, baseUrl, DeviceType.Telescope, ct);
            device.TryInstantiateDriver<IMountDriver>(sp, out var mount).ShouldBeTrue();
            await using (mount)
            {
                await mount.ConnectAsync(ct);
                mount.Connected.ShouldBeTrue();

                // Tracking cannot be enabled while parked; unpark first if the sim starts parked.
                if (mount.CanUnpark && await mount.AtParkAsync(ct))
                {
                    await mount.UnparkAsync(ct);
                }

                var ra = await mount.GetRightAscensionAsync(ct);
                var dec = await mount.GetDeclinationAsync(ct);
                ra.ShouldBeInRange(0.0, 24.0);
                dec.ShouldBeInRange(-90.0, 90.0);

                await mount.SetTrackingAsync(true, ct);
                (await mount.IsTrackingAsync(ct)).ShouldBeTrue();
                await mount.SetTrackingAsync(false, ct);

                await mount.DisconnectAsync(ct);
            }
        }
    }

    [Fact]
    public async Task Focuser_MovesToAbsolutePosition()
    {
        var baseUrl = RequireSim();
        var ct = TestContext.Current.CancellationToken;
        var (sp, client) = BuildAlpaca();
        await using (sp)
        {
            var device = await ResolveDeviceAsync(client, baseUrl, DeviceType.Focuser, ct);
            device.TryInstantiateDriver<IFocuserDriver>(sp, out var focuser).ShouldBeTrue();
            await using (focuser)
            {
                await focuser.ConnectAsync(ct);
                focuser.Connected.ShouldBeTrue();
                Assert.SkipUnless(focuser.Absolute, "Focuser is not absolute; skipping absolute-move assertion");
                focuser.MaxStep.ShouldBeGreaterThan(0);

                var start = await focuser.GetPositionAsync(ct);
                // Move a bounded amount toward the middle of travel so the target is always in range.
                var target = start > focuser.MaxStep / 2
                    ? Math.Max(0, start - 500)
                    : Math.Min(focuser.MaxStep, start + 500);
                await focuser.BeginMoveAsync(target, ct);

                var settled = await WaitAsync(async () => !await focuser.GetIsMovingAsync(ct), TimeSpan.FromSeconds(30), ct);
                settled.ShouldBeTrue();
                (await focuser.GetPositionAsync(ct)).ShouldBe(target);

                await focuser.DisconnectAsync(ct);
            }
        }
    }

    [Fact]
    public async Task FilterWheel_MovesToPosition()
    {
        var baseUrl = RequireSim();
        var ct = TestContext.Current.CancellationToken;
        var (sp, client) = BuildAlpaca();
        await using (sp)
        {
            var device = await ResolveDeviceAsync(client, baseUrl, DeviceType.FilterWheel, ct);
            device.TryInstantiateDriver<IFilterWheelDriver>(sp, out var fw).ShouldBeTrue();
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
                var settled = await WaitAsync(async () => await fw.GetPositionAsync(ct) == target, TimeSpan.FromSeconds(30), ct);
                settled.ShouldBeTrue();

                await fw.DisconnectAsync(ct);
            }
        }
    }

    [Fact]
    public async Task CoverCalibrator_ReadsStateAndTogglesCalibrator()
    {
        var baseUrl = RequireSim();
        var ct = TestContext.Current.CancellationToken;
        var (sp, client) = BuildAlpaca();
        await using (sp)
        {
            var device = await ResolveDeviceAsync(client, baseUrl, DeviceType.CoverCalibrator, ct);
            device.TryInstantiateDriver<ICoverDriver>(sp, out var cover).ShouldBeTrue();
            await using (cover)
            {
                await cover.ConnectAsync(ct);
                cover.Connected.ShouldBeTrue();

                (await cover.GetCoverStateAsync(ct)).ShouldNotBe(CoverStatus.Error);
                (await cover.GetCalibratorStateAsync(ct)).ShouldNotBe(CalibratorStatus.Error);

                Assert.SkipUnless(cover.MaxBrightness > 0, "Cover has no controllable calibrator; skipping calibrator toggle");

                var brightness = Math.Max(1, cover.MaxBrightness / 2);
                await cover.BeginCalibratorOn(brightness, ct);
                var ready = await WaitAsync(async () => await cover.GetCalibratorStateAsync(ct) == CalibratorStatus.Ready, TimeSpan.FromSeconds(30), ct);
                ready.ShouldBeTrue();
                (await cover.GetBrightnessAsync(ct)).ShouldBe(brightness);

                await cover.BeginCalibratorOff(ct);
                await cover.DisconnectAsync(ct);
            }
        }
    }
}
