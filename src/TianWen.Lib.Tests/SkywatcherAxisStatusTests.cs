using Shouldly;
using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The axis status (<c>:f</c>) at connect and while connected.
/// <list type="bullet">
/// <item>A connect initialises each axis whose init bit is clear with <c>:F1</c> / <c>:F2</c>, one axis at a time, as
/// GSServer does. It used to send <c>:F3</c>, which a real AZ-GTi (firmware 3.40) answers <c>!0</c>, unknown command, so every
/// connect left both axes uninitialised and logged a warning (#1953). The fake board answers <c>:F3</c> as that mount does.</item>
/// <item>A connected board that gives no status is a fault, never "stopped" (#1956, the SkyWatcher case of #810): the made-up
/// "stopped" ended a slew-wait mid-slew and read a tracking mount as stopped.</item>
/// </list>
/// </summary>
[Collection("Skywatcher")]
public class SkywatcherAxisStatusTests(ITestOutputHelper output)
{
    private async Task<(IMountDriver Driver, FakeSkywatcherSerialDevice Board)> ConnectAsync(CancellationToken ct)
    {
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
        var sp = new FakeExternal(output, clock).BuildServiceProvider();
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", "-37.8769444" },
            { "longitude", "145.1775" },
            { "port", "SkyWatcher" },
        });
        var driver = new Mount(device, sp).Driver;
        await driver.ConnectAsync(ct);
        var board = ((SkywatcherMountDriverBase<FakeDevice>)driver).SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        return (driver, board);
    }

    [Fact(Timeout = 30_000)]
    public async Task AConnectInitialisesEachAxisOnItsOwnAndNeverAddressesBoth()
    {
        var (_, board) = await ConnectAsync(TestContext.Current.CancellationToken);

        var log = board.CommandLogSnapshot;
        log.ShouldNotContain(":F3");
        log.Count(c => c == ":F1").ShouldBe(1);
        log.Count(c => c == ":F2").ShouldBe(1);

        // Each :F is sent only after :f has said the axis is not initialised.
        Array.IndexOf(log, ":f1").ShouldBeLessThan(Array.IndexOf(log, ":F1"));
        Array.IndexOf(log, ":f2").ShouldBeLessThan(Array.IndexOf(log, ":F2"));
    }

    [Fact(Timeout = 30_000)]
    public async Task ASlewWhoseStatusGetsNoReplyThrowsInsteadOfReadingStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, board) = await ConnectAsync(ct);
        var lst = await driver.GetSiderealTimeAsync(ct);
        await driver.BeginSlewRaDecAsync(CoordinateUtils.ConditionRA(lst + 1.0), -60.0, ct);
        (await driver.IsSlewingAsync(ct)).ShouldBeTrue();

        board.InjectCommandFault('f', '1', occurrences: 1, response: null);

        await Should.ThrowAsync<IOException>(async () => await driver.IsSlewingAsync(ct));
        (await driver.IsSlewingAsync(ct)).ShouldBeTrue("the slew goes on, and the next answered read says so");
    }

    [Fact(Timeout = 30_000)]
    public async Task ATrackingMountWhoseStatusGetsAnUnparseableReplyThrowsInsteadOfReadingStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, board) = await ConnectAsync(ct);
        await driver.SetTrackingAsync(true, ct);
        (await driver.IsTrackingAsync(ct)).ShouldBeTrue();

        board.InjectCommandFault('f', '1', occurrences: 1, response: "!0\r");

        await Should.ThrowAsync<IOException>(async () => await driver.IsTrackingAsync(ct));
        (await driver.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task AReconnectStopsAGotoStillRunningBeforeItReadsThePosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, board) = await ConnectAsync(ct);
        var lst = await driver.GetSiderealTimeAsync(ct);
        await driver.BeginSlewRaDecAsync(CoordinateUtils.ConditionRA(lst + 1.0), -60.0, ct);
        board.RaMotion.Running.ShouldBeTrue();

        await driver.DisconnectAsync(ct);
        board.RaMotion.Running.ShouldBeTrue("a disconnect leaves the mount where it is, still slewing");
        var before = board.CommandLogSnapshot.Length;

        await driver.ConnectAsync(ct);

        board.RaMotion.Running.ShouldBeFalse();
        var sent = board.CommandLogSnapshot[before..];
        Array.IndexOf(sent, ":K1").ShouldBeGreaterThanOrEqualTo(0);
        Array.IndexOf(sent, ":K1").ShouldBeLessThan(Array.IndexOf(sent, ":j1"), "stopped before its position is read");
    }

    [Fact(Timeout = 30_000)]
    public async Task AReconnectLeavesATrackingMountTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, board) = await ConnectAsync(ct);
        await driver.SetTrackingAsync(true, ct);

        await driver.DisconnectAsync(ct);
        var before = board.CommandLogSnapshot.Length;
        await driver.ConnectAsync(ct);

        var sent = board.CommandLogSnapshot[before..];
        sent.ShouldNotContain(":K1");
        sent.ShouldNotContain(":F1", "the axes were initialised by the first connect");
        (await driver.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task ADeviceConnectedOnAnotherClockGetsAFreshBoard()
    {
        // A test class that keeps its device in a static field connects it once per test, each on a clock of its own. The
        // board must not carry over: on the previous test's stopped clock its motors never move again (NodeGuidingTests).
        var ct = TestContext.Current.CancellationToken;
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", "-37.8769444" },
            { "longitude", "145.1775" },
            { "port", "SkyWatcher" },
        });

        async Task<FakeSkywatcherSerialDevice> ConnectOnItsOwnClockAsync()
        {
            var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));
            var driver = new Mount(device, new FakeExternal(output, clock).BuildServiceProvider()).Driver;
            await driver.ConnectAsync(ct);
            var board = ((SkywatcherMountDriverBase<FakeDevice>)driver).SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
            await driver.DisconnectAsync(ct);
            return board;
        }

        var first = await ConnectOnItsOwnClockAsync();
        var second = await ConnectOnItsOwnClockAsync();

        second.ShouldNotBeSameAs(first);
    }
}
