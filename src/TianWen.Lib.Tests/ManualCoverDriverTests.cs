using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the manual (hand-switched) flat light panel modelled as a degenerate <see cref="ICoverDriver"/> —
/// mirroring <see cref="ManualFilterWheelTests"/>. It reports no cover flap (<see cref="CoverStatus.NotPresent"/>)
/// and a user-operated calibrator (Ready on demand, no analog brightness control), and — unlike the manual
/// filter wheel — round-trips through the keyed URI factory so a profile can reference it.
/// </summary>
[Collection("Device")]
public class ManualCoverDriverTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GivenManualCoverWhenConnectedThenReportsNoFlapAndFullBrightnessRange()
    {
        var device = new ManualCoverDevice();
        var external = new FakeExternal(output);
        var sp = external.BuildServiceProvider();
        device.TryInstantiateDriver<ICoverDriver>(sp, out var driver).ShouldBeTrue();

        await ((IDeviceDriver)driver!).ConnectAsync(TestContext.Current.CancellationToken);

        driver.Connected.ShouldBeTrue();
        driver.MaxBrightness.ShouldBe(255); // matches the Gemini FlatPanel Lite (0-255).
        (await driver.GetCoverStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CoverStatus.NotPresent);

        // A hand-switched panel is not electronically dimmable -> the flat routine must prompt the user.
        driver.CanControlBrightness.ShouldBeFalse();
    }

    [Fact]
    public async Task GivenNoFlapWhenOpenOrCloseThenCoverStaysNotPresent()
    {
        var device = new ManualCoverDevice();
        var external = new FakeExternal(output);
        var sp = external.BuildServiceProvider();
        device.TryInstantiateDriver<ICoverDriver>(sp, out var driver).ShouldBeTrue();
        await ((IDeviceDriver)driver!).ConnectAsync(TestContext.Current.CancellationToken);

        // No motorised flap: moving it is a graceful no-op (never wedges on Moving).
        await driver.BeginClose(TestContext.Current.CancellationToken);
        (await driver.GetCoverStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CoverStatus.NotPresent);
        await driver.BeginOpen(TestContext.Current.CancellationToken);
        (await driver.GetCoverStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CoverStatus.NotPresent);
    }

    [Fact]
    public async Task GivenManualCalibratorWhenTurnedOnThenReadyAndOffAgain()
    {
        var device = new ManualCoverDevice();
        var external = new FakeExternal(output);
        var sp = external.BuildServiceProvider();
        device.TryInstantiateDriver<ICoverDriver>(sp, out var driver).ShouldBeTrue();
        await ((IDeviceDriver)driver!).ConnectAsync(TestContext.Current.CancellationToken);

        (await driver.GetCalibratorStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CalibratorStatus.Off);

        // "On" trusts the user switched the analog panel on: reports Ready and records the requested level.
        await driver.BeginCalibratorOn(128, TestContext.Current.CancellationToken);
        (await driver.GetCalibratorStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CalibratorStatus.Ready);
        (await driver.GetBrightnessAsync(TestContext.Current.CancellationToken)).ShouldBe(128);
        (await driver.IsCalibrationReadyAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await driver.TurnOffCalibratorAndWaitAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await driver.GetCalibratorStateAsync(TestContext.Current.CancellationToken)).ShouldBe(CalibratorStatus.Off);
    }

    [Fact]
    public void ManualCoverDevice_roundtrips_through_the_keyed_uri_factory()
    {
        // The profile -> session path (SessionFactory.DeviceFromUri) resolves a stored cover URI via
        // IDeviceHub.TryGetDeviceFromUri, keyed on the URI host. AddDevices() registers the factory so a
        // ManualCoverDevice URI reconstructs as a ManualCoverDevice (and would otherwise throw).
        var sp = new ServiceCollection()
            .AddLogging()
            .AddDevices()
            .BuildServiceProvider();
        var hub = sp.GetRequiredService<IDeviceHub>();

        var stored = new ManualCoverDevice().DeviceUri;
        hub.TryGetDeviceFromUri(stored, out var device).ShouldBeTrue();
        var resolved = device.ShouldBeOfType<ManualCoverDevice>();
        resolved.DeviceType.ShouldBe(DeviceType.CoverCalibrator);
    }
}
