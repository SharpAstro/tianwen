using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins that the commands ending a pulse are VERIFIED, not merely sent.
/// </summary>
/// <remarks>
/// <para>A pulse leaves an axis in a state only its own <c>finally</c> undoes: RA at
/// <c>(1 +/- f) x sidereal</c> until <c>:I1</c> restores the sidereal step period, or an axis
/// running until <c>:K</c> stops it. Those commands used to be fire-and-forget -- a firmware
/// refusal reached <c>LogWarning</c> and a read timeout reached nothing at all -- so a mount could
/// spend a whole night tracking at up to twice sidereal with the driver believing it had cancelled
/// the pulse, and the only evidence was trailed subframes.</para>
///
/// <para>The fault is deliberately routed through the existing guider path rather than new
/// plumbing: it propagates out of <c>PulseGuideAsync</c>, <c>BuiltInGuiderDriver</c> turns it into a
/// <c>GuidingErrorEvent</c>, and the session drains that, logs it and restarts the guider.</para>
/// </remarks>
[Collection("Skywatcher")]
public class SkywatcherPulseRestoreTests(ITestOutputHelper output)
{
    private const double SiteLatDeg = 48.2;
    private const double SiteLonDeg = 16.3;

    private static readonly TimeSpan PulseDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>A firmware refusal: the board answered, and said no.</summary>
    private const string FirmwareRefusal = "!2\r";

    /// <summary>A read timeout: the board said nothing at all. Distinct from a refusal, and the
    /// case that used to reach no code path whatsoever.</summary>
    private const string? NoAnswer = null;

    private async Task<(IMountDriver Driver, FakeSkywatcherSerialDevice Serial)> ConnectTrackingMountAsync(
        CancellationToken cancellationToken)
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var sp = new FakeExternal(output, timeProvider).BuildServiceProvider();

        var mountDevice = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", SiteLatDeg.ToString(CultureInfo.InvariantCulture) },
            { "longitude", SiteLonDeg.ToString(CultureInfo.InvariantCulture) },
            { "elevation", "200" },
            { "port", "SkyWatcher" }
        });

        var driver = new Mount(mountDevice, sp).Driver;
        await driver.ConnectAsync(cancellationToken);

        // The RA branch is chosen from the LIVE tracking status, so a test that means to exercise
        // the live-:I pulse has to actually be tracking.
        await driver.SetTrackingAsync(true, cancellationToken);
        (await driver.IsTrackingAsync(cancellationToken)).ShouldBeTrue();

        var serial = ((SkywatcherMountDriverBase<FakeDevice>)driver).SerialConnection
            .ShouldBeOfType<FakeSkywatcherSerialDevice>();

        return (driver, serial);
    }

    private static int CountRaStepPeriodCommands(FakeSkywatcherSerialDevice serial)
        => serial.CommandLogSnapshot.Count(c => c.StartsWith(":I1", StringComparison.Ordinal));

    #region The RA rate restore

    [Theory]
    [InlineData(FirmwareRefusal)]
    [InlineData(NoAnswer)]
    public async Task AnUnacknowledgedRateRestoreIsAFaultAndNotALogLine(string? response)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectTrackingMountAsync(cancellationToken);

        // Let the pulse's own :I1 through, then refuse every restore attempt.
        serial.InjectCommandFault('I', '1', occurrences: 8, response: response, skipFirstMatches: 1);

        var ex = await Should.ThrowAsync<SkywatcherDriverException>(
            async () => await driver.PulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken));

        ex.Data["Command"].ShouldBe(":I1");
    }

    [Fact]
    public async Task ARestoreThatSucceedsOnARetryIsNotAFault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectTrackingMountAsync(cancellationToken);

        // One bad restore, then the board answers. Retrying is half the fix: a single serial hiccup
        // must not end the night, and only an exhausted budget is a genuine fault.
        var before = CountRaStepPeriodCommands(serial);
        serial.InjectCommandFault('I', '1', occurrences: 1, response: FirmwareRefusal, skipFirstMatches: 1);

        await driver.PulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken);

        // Three :I1 sends belong to the pulse: the pulse rate, a refused restore, and the retry that
        // was accepted. Counted as a DELTA because connecting and tracking send :I1 of their own.
        (CountRaStepPeriodCommands(serial) - before).ShouldBe(3);
    }

    [Fact]
    public async Task AnEastPulseVerifiesTheRestoreToo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectTrackingMountAsync(cancellationToken);

        serial.InjectCommandFault('I', '1', occurrences: 8, response: FirmwareRefusal, skipFirstMatches: 1);

        await Should.ThrowAsync<SkywatcherDriverException>(
            async () => await driver.PulseGuideAsync(GuideDirection.East, PulseDuration, cancellationToken));
    }

    #endregion

    #region The axis stops

    [Fact]
    public async Task AnUnacknowledgedDecStopIsAFault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectTrackingMountAsync(cancellationToken);

        // Dec has no tracking baseline, so its pulse is G/I/J and the :K2 in the finally is the only
        // thing that ends the motion. Same failure class as the RA restore, same treatment.
        serial.InjectCommandFault('K', '2', occurrences: 8, response: FirmwareRefusal);

        var ex = await Should.ThrowAsync<SkywatcherDriverException>(
            async () => await driver.PulseGuideAsync(GuideDirection.North, PulseDuration, cancellationToken));

        ex.Data["Command"].ShouldBe(":K2");
    }

    [Fact]
    public async Task AnUnacknowledgedRaStopIsAFaultWhenNotTracking()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectTrackingMountAsync(cancellationToken);

        await driver.SetTrackingAsync(false, cancellationToken);

        // Not tracking, so the RA pulse runs the axis outright and :K1 is what stops it again.
        serial.InjectCommandFault('K', '1', occurrences: 8, response: FirmwareRefusal);

        var ex = await Should.ThrowAsync<SkywatcherDriverException>(
            async () => await driver.PulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken));

        ex.Data["Command"].ShouldBe(":K1");
    }

    #endregion

    #region What stays best-effort

    [Fact]
    public async Task AnOrdinaryCommandStillFailsForward()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectTrackingMountAsync(cancellationToken);

        // The pulse's OWN :I1 (as opposed to the restore) costs one guide correction if the board
        // refuses it, and the next frame re-issues one. That stays a log line on purpose: making
        // every command fatal would turn a recoverable hiccup into a stopped guider.
        serial.InjectCommandFault('I', '1', occurrences: 1, response: FirmwareRefusal);

        await driver.PulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken);
    }

    [Fact]
    public async Task APulseBelowTheFloorTouchesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial) = await ConnectTrackingMountAsync(cancellationToken);

        var before = serial.CommandLogSnapshot.Length;
        serial.InjectCommandFault('I', '1', occurrences: 8, response: FirmwareRefusal);

        await driver.PulseGuideAsync(GuideDirection.West, TimeSpan.FromMilliseconds(5), cancellationToken);

        serial.CommandLogSnapshot.Length.ShouldBe(before);
    }

    #endregion
}
