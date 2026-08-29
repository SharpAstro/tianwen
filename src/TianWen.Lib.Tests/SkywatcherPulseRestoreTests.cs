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
/// plumbing: it propagates out of <c>StartPulseGuideAsync</c>, <c>BuiltInGuiderDriver</c> turns it into a
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

    private async Task<(IMountDriver Driver, FakeSkywatcherSerialDevice Serial, FakeTimeProviderWrapper Clock)> ConnectTrackingMountAsync(
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

        return (driver, serial, timeProvider);
    }

    /// <summary>
    /// Runs the background hold to completion and returns the fault it parked, or null.
    /// </summary>
    /// <remarks>
    /// <b>This is deterministic despite looking like a poll race.</b> The hold parks its fault
    /// BEFORE it lowers the in-flight count, and <c>IsPulseGuidingAsync</c> checks for a parked
    /// fault BEFORE it reads that count -- so once the hold has failed, the very next poll throws,
    /// whichever side of the decrement it lands on. The loop is only waiting for the hold to get
    /// there, which under the auto-advancing fake clock costs no wall time.
    /// </remarks>
    private static async Task<SkywatcherDriverException?> DrainForFaultAsync(
        IMountDriver driver, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 500; i++)
        {
            try
            {
                if (!await driver.IsPulseGuidingAsync(cancellationToken))
                {
                    return null;
                }
            }
            catch (SkywatcherDriverException ex)
            {
                return ex;
            }
            await Task.Yield();
        }
        return null;
    }

    private static int CountRaStepPeriodCommands(FakeSkywatcherSerialDevice serial)
        => serial.CommandLogSnapshot.Count(c => c.StartsWith(":I1", StringComparison.Ordinal));

    #region The starter does not hold the duration

    /// <remarks>
    /// Bounded, because the regression this guards against does not fail -- it HANGS. A driver that
    /// went back to blocking would await its own hold, which parks in the pumped clock's sleep, and
    /// the advance that would release it lives after the starter returns. Without the bound that is
    /// a wedged suite and a multi-GB hang dump instead of one red test.
    /// </remarks>
    [Fact(Timeout = 30_000)]
    public async Task TheStarterReturnsOnceCommandedNotOnceFinished()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, clock) = await ConnectTrackingMountAsync(cancellationToken);

        // No auto-advance: the hold's SleepAsync parks until this test releases it, so "the starter
        // came back while the pulse was still running" is a fact rather than a race. With the clock
        // auto-advancing, the hold can finish before the assertion runs and the test would pass
        // against a blocking driver too.
        clock.ExternalTimePump = true;

        var before = clock.GetUtcNow();
        await driver.StartPulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken);

        // The whole point of the conversion: the caller is back, the mount is still moving.
        (clock.GetUtcNow() - before).ShouldBeLessThan(PulseDuration);
        (await driver.IsPulseGuidingAsync(cancellationToken)).ShouldBeTrue(
            "IsPulseGuiding must be observable BEFORE the starter returns (GSS #109)");

        // The restore has not gone out yet -- it belongs to the hold, which is parked in its sleep.
        var beforeRelease = CountRaStepPeriodCommands(serial);

        while (clock.WaiterCount == 0)
        {
            await Task.Yield();
        }
        clock.Advance(PulseDuration);

        // ExternalTimePump parks in a real 1 ms poll, so releasing it costs real time; yields alone
        // spin orders of magnitude faster than it can wake, which is why this loop delays.
        for (var i = 0; i < 500 && await driver.IsPulseGuidingAsync(cancellationToken); i++)
        {
            await Task.Delay(1, cancellationToken);
        }

        (await driver.IsPulseGuidingAsync(cancellationToken)).ShouldBeFalse();
        CountRaStepPeriodCommands(serial).ShouldBe(beforeRelease + 1, "the hold owns the :I1 restore");
    }

    #endregion

    #region The RA rate restore

    [Theory]
    [InlineData(FirmwareRefusal)]
    [InlineData(NoAnswer)]
    public async Task AnUnacknowledgedRateRestoreIsAFaultAndNotALogLine(string? response)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, _) = await ConnectTrackingMountAsync(cancellationToken);

        // Let the pulse's own :I1 through, then refuse every restore attempt.
        serial.InjectCommandFault('I', '1', occurrences: 8, response: response, skipFirstMatches: 1);

        // The starter returns once COMMANDED, so the restore -- and its failure -- happens on the
        // background hold. The fault must still reach whoever is waiting for the pulse.
        await driver.StartPulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken);

        var ex = (await DrainForFaultAsync(driver, cancellationToken)).ShouldNotBeNull();
        ex.Data["Command"].ShouldBe(":I1");
    }

    [Fact]
    public async Task ARestoreThatSucceedsOnARetryIsNotAFault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, _) = await ConnectTrackingMountAsync(cancellationToken);

        // One bad restore, then the board answers. Retrying is half the fix: a single serial hiccup
        // must not end the night, and only an exhausted budget is a genuine fault.
        var before = CountRaStepPeriodCommands(serial);
        serial.InjectCommandFault('I', '1', occurrences: 1, response: FirmwareRefusal, skipFirstMatches: 1);

        await driver.StartPulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken);
        (await DrainForFaultAsync(driver, cancellationToken)).ShouldBeNull();

        // Three :I1 sends belong to the pulse: the pulse rate, a refused restore, and the retry that
        // was accepted. Counted as a DELTA because connecting and tracking send :I1 of their own.
        // Drained first: the restore now happens on the background hold, so counting straight after
        // the starter returns would count whatever the hold had happened to reach.
        (CountRaStepPeriodCommands(serial) - before).ShouldBe(3);
    }

    [Fact]
    public async Task AnEastPulseVerifiesTheRestoreToo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, _) = await ConnectTrackingMountAsync(cancellationToken);

        serial.InjectCommandFault('I', '1', occurrences: 8, response: FirmwareRefusal, skipFirstMatches: 1);

        await driver.StartPulseGuideAsync(GuideDirection.East, PulseDuration, cancellationToken);

        (await DrainForFaultAsync(driver, cancellationToken)).ShouldNotBeNull();
    }

    #endregion

    #region The axis stops

    [Fact]
    public async Task AnUnacknowledgedDecStopIsAFault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, _) = await ConnectTrackingMountAsync(cancellationToken);

        // Dec has no tracking baseline, so its pulse is G/I/J and the :K2 in the finally is the only
        // thing that ends the motion. Same failure class as the RA restore, same treatment.
        serial.InjectCommandFault('K', '2', occurrences: 8, response: FirmwareRefusal);

        await driver.StartPulseGuideAsync(GuideDirection.North, PulseDuration, cancellationToken);

        var ex = (await DrainForFaultAsync(driver, cancellationToken)).ShouldNotBeNull();
        ex.Data["Command"].ShouldBe(":K2");
    }

    [Fact]
    public async Task AnUnacknowledgedRaStopIsAFaultWhenNotTracking()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, _) = await ConnectTrackingMountAsync(cancellationToken);

        await driver.SetTrackingAsync(false, cancellationToken);

        // Not tracking, so the RA pulse runs the axis outright and :K1 is what stops it again.
        serial.InjectCommandFault('K', '1', occurrences: 8, response: FirmwareRefusal);

        await driver.StartPulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken);

        var ex = (await DrainForFaultAsync(driver, cancellationToken)).ShouldNotBeNull();
        ex.Data["Command"].ShouldBe(":K1");
    }

    #endregion

    [Fact(Timeout = 30_000)]
    public async Task ACancelledPulseIsNotParkedAsAFault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, clock) = await ConnectTrackingMountAsync(cancellationToken);
        clock.ExternalTimePump = true;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await driver.StartPulseGuideAsync(GuideDirection.West, PulseDuration, cts.Token);

        while (clock.WaiterCount == 0)
        {
            await Task.Yield();
        }

        var beforeRestore = CountRaStepPeriodCommands(serial);
        await cts.CancelAsync();

        for (var i = 0; i < 500 && await driver.IsPulseGuidingAsync(cancellationToken); i++)
        {
            await Task.Delay(1, cancellationToken);
        }

        // Cancelling a run must not leave a booby-trapped driver: parking the OperationCanceled-
        // Exception would re-throw it from some later, unrelated IsPulseGuiding.
        (await driver.IsPulseGuidingAsync(cancellationToken)).ShouldBeFalse();
        (await DrainForFaultAsync(driver, cancellationToken)).ShouldBeNull();

        // And the axis is still put back: the restore runs in a finally under None, so an abandoned
        // pulse cannot leave RA at (1 +/- f) x sidereal.
        CountRaStepPeriodCommands(serial).ShouldBe(beforeRestore + 1);
    }

    #region What stays best-effort

    [Fact]
    public async Task AnOrdinaryCommandStillFailsForward()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, _) = await ConnectTrackingMountAsync(cancellationToken);

        // The pulse's OWN :I1 (as opposed to the restore) costs one guide correction if the board
        // refuses it, and the next frame re-issues one. That stays a log line on purpose: making
        // every command fatal would turn a recoverable hiccup into a stopped guider.
        serial.InjectCommandFault('I', '1', occurrences: 1, response: FirmwareRefusal);

        await driver.StartPulseGuideAsync(GuideDirection.West, PulseDuration, cancellationToken);
    }

    [Fact]
    public async Task APulseBelowTheFloorTouchesNothing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (driver, serial, _) = await ConnectTrackingMountAsync(cancellationToken);

        var before = serial.CommandLogSnapshot.Length;
        serial.InjectCommandFault('I', '1', occurrences: 8, response: FirmwareRefusal);

        await driver.StartPulseGuideAsync(GuideDirection.West, TimeSpan.FromMilliseconds(5), cancellationToken);

        serial.CommandLogSnapshot.Length.ShouldBe(before);
    }

    #endregion
}
