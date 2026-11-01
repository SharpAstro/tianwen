using Shouldly;
using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Devices.Skywatcher;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A connect initialises each axis whose <c>:f</c> init bit is clear with <c>:F1</c> / <c>:F2</c>, one axis at a time, as
/// GSServer does. It used to send <c>:F3</c>, which a real AZ-GTi (firmware 3.40) answers <c>!0</c>, unknown command, so every
/// connect left both axes uninitialised and logged a warning (#1953). The fake board answers <c>:F3</c> as that mount does.
/// </summary>
[Collection("Skywatcher")]
public class SkywatcherAxisInitTests(ITestOutputHelper output)
{
    [Fact(Timeout = 30_000)]
    public async Task AConnectInitialisesEachAxisOnItsOwnAndNeverAddressesBoth()
    {
        var ct = TestContext.Current.CancellationToken;
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
        var log = board.CommandLogSnapshot;
        log.ShouldNotContain(":F3");
        log.Count(c => c == ":F1").ShouldBe(1);
        log.Count(c => c == ":F2").ShouldBe(1);

        // Each :F is sent only after :f has said the axis is not initialised.
        Array.IndexOf(log, ":f1").ShouldBeLessThan(Array.IndexOf(log, ":F1"));
        Array.IndexOf(log, ":f2").ShouldBeLessThan(Array.IndexOf(log, ":F2"));
    }
}
