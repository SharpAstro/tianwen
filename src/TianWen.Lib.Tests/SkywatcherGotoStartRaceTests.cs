using Shouldly;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A poll that lands while a goto's first pass is still stopping the axes and reading the encoders answers "slewing" and
/// sends nothing. It used to find the goto armed and the axes idle, take it for a goto that ended short, and start a
/// refinement pass beside the first: a real AZ-GTi answered the second <c>:G1</c> with <c>!2</c> and the goto stalled for
/// 20 s on unanswered commands (2026-11-02). On the fake the window is held open by a stop-wait the fake clock never ends.
/// </summary>
[Collection("Skywatcher")]
public class SkywatcherGotoStartRaceTests(ITestOutputHelper output)
{
    [Fact(Timeout = 30_000)]
    public async Task APollWhileTheFirstPassStopsTheAxesSaysSlewingAndSendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var sp = new FakeExternal(output, clock).BuildServiceProvider();
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", "48.2" },
            { "longitude", "16.3" },
            { "port", "SkyWatcher" },
        });
        var driver = new Mount(device, sp).Driver;
        await driver.ConnectAsync(ct);
        var board = ((SkywatcherMountDriverBase<FakeDevice>)driver).SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        var lst = await driver.GetSiderealTimeAsync(ct);

        // The goto's own :f1 check passes; the stop-wait's :f1 after :K1 then answers "still running a goto", so the pass
        // sleeps 25 ms on a clock that only this test advances.
        clock.ExternalTimePump = true;
        board.InjectCommandFault('f', '1', occurrences: 1, response: "=011\r", skipFirstMatches: 1);
        var gotoPass = driver.BeginSlewRaDecAsync(CoordinateUtils.ConditionRA(lst - 1.0), 45.0, ct).AsTask();
        gotoPass.IsCompleted.ShouldBeFalse("premise: the first pass is held in its stop-wait");

        var before = board.CommandLogSnapshot.Length;
        (await driver.IsSlewingAsync(ct)).ShouldBeTrue("a goto is under way");
        board.CommandLogSnapshot[before..].ShouldNotContain(c => c.StartsWith(":G") || c.StartsWith(":K"),
            "the poll started no pass of its own");

        while (!gotoPass.IsCompleted)
        {
            clock.Advance(TimeSpan.FromMilliseconds(25));
            await Task.Yield();
        }
        await gotoPass;
        board.CommandLogSnapshot.Count(c => c == ":J1").ShouldBe(1, "one pass started the RA axis");
    }
}
