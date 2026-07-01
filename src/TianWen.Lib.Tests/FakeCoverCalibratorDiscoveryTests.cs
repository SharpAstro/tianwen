using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins that the fake device source surfaces a cover/calibrator through normal discovery (so a profile can be
/// wired against one with no live hardware), and that it models BOTH real hardware classes under the ASCOM
/// CoverCalibrator umbrella: a flip-flat (motorised cover flap + panel) and a driver-controlled light panel
/// with no flap (e.g. the Gemini FlatPanel Lite), which reports <see cref="CoverStatus.NotPresent"/>.
/// </summary>
public class FakeCoverCalibratorDiscoveryTests(ITestOutputHelper output)
{
    private static IServiceProvider BuildServiceProvider(ITestOutputHelper output)
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var external = new FakeExternal(output, timeProvider);
        return external.BuildServiceProvider();
    }

    [Fact]
    public void CoverCalibrator_is_a_discoverable_fake_device_type()
    {
        new FakeDeviceSource().RegisteredDeviceTypes.ShouldContain(DeviceType.CoverCalibrator);
    }

    [Fact]
    public async Task RegisteredDevices_surfaces_a_flip_flat_and_a_flapless_light_panel()
    {
        var ct = TestContext.Current.CancellationToken;
        var sp = BuildServiceProvider(output);

        var devices = new FakeDeviceSource().RegisteredDevices(DeviceType.CoverCalibrator).ToArray();
        devices.Length.ShouldBe(2);

        // 1. Flip-flat: has a motorised cover flap (starts Closed) and a calibrator panel.
        var flipFlat = new Cover(devices[0], sp).Driver;
        await flipFlat.ConnectAsync(ct);
        (await flipFlat.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Closed);
        (await flipFlat.GetCalibratorStateAsync(ct)).ShouldNotBe(CalibratorStatus.NotPresent);

        // 2. Bare light panel (Gemini FlatPanel Lite class): no flap -> NotPresent, but the calibrator works.
        var lightPanel = new Cover(devices[1], sp).Driver;
        await lightPanel.ConnectAsync(ct);
        (await lightPanel.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.NotPresent);
        (await lightPanel.GetCalibratorStateAsync(ct)).ShouldNotBe(CalibratorStatus.NotPresent);
    }

    [Fact]
    public async Task FlaplessLightPanel_cover_moves_are_a_graceful_noop_but_the_calibrator_still_cycles()
    {
        var ct = TestContext.Current.CancellationToken;
        var sp = BuildServiceProvider(output);

        // hasCover=false selects the flap-less light-panel behaviour.
        var panel = new FakeDevice(DeviceType.CoverCalibrator, 1, new System.Collections.Specialized.NameValueCollection
        {
            { DeviceQueryKey.HasCover.Key, "false" },
        });
        var driver = new Cover(panel, sp).Driver;
        await driver.ConnectAsync(ct);

        driver.MaxBrightness.ShouldBe(255); // matches the Gemini FlatPanel Lite (0-255).
        (await driver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.NotPresent);

        // Opening/closing a cover that isn't there leaves the state NotPresent -- never wedges on Moving.
        await driver.BeginClose(ct);
        (await driver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.NotPresent);
        await driver.BeginOpen(ct);
        (await driver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.NotPresent);

        // The brightness panel still cycles on -> ready -> off.
        await driver.BeginCalibratorOn(driver.MaxBrightness / 2, ct);
        (await driver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Ready);
        (await driver.GetBrightnessAsync(ct)).ShouldBe(driver.MaxBrightness / 2);
        (await driver.TurnOffCalibratorAndWaitAsync(ct)).ShouldBeTrue();
        (await driver.GetCalibratorStateAsync(ct)).ShouldBe(CalibratorStatus.Off);
    }

    [Fact]
    public async Task DefaultCover_without_hasCover_key_is_a_flip_flat_with_a_closed_cover()
    {
        var ct = TestContext.Current.CancellationToken;
        var sp = BuildServiceProvider(output);

        // No hasCover key -> defaults to a flip-flat WITH a motorised cover (existing test-helper behaviour).
        var driver = new Cover(new FakeDevice(DeviceType.CoverCalibrator, 1), sp).Driver;
        await driver.ConnectAsync(ct);

        (await driver.GetCoverStateAsync(ct)).ShouldBe(CoverStatus.Closed);
    }
}
