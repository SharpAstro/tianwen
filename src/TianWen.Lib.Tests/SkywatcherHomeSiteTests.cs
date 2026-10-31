using Shouldly;
using System;
using System.Collections.Specialized;
using System.Globalization;
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
/// A Skywatcher's home is encoder zero (counterweight down, the scope at the pole), read as HA 6 h and the pole of the site's
/// hemisphere, and nothing writes the encoders to say so. A "parked at the pole" sync to (LST, +-90) used to declare HA 0 h
/// there, a quarter turn of RA off: on 2026-10-31 an AZ-GTi in Melbourne sent 1.1 h east of the meridian turned 164 degrees
/// of RA for 74 and finished counterweight-up. Before that the same sync had left it pointing at the north pole after the
/// site was corrected from Vienna to Melbourne.
/// </summary>
[Collection("Skywatcher")]
public class SkywatcherHomeSiteTests(ITestOutputHelper output)
{
    private const double Vienna = 48.2;
    private const double Melbourne = -37.8769444;

    private async Task<(IMountDriver Driver, FakeSkywatcherSerialDevice Board, FakeTimeProviderWrapper Clock)> ConnectAsync(
        double latitude, double longitude, CancellationToken ct)
    {
        var clock = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero));
        var sp = new FakeExternal(output, clock).BuildServiceProvider();
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", latitude.ToString(CultureInfo.InvariantCulture) },
            { "longitude", longitude.ToString(CultureInfo.InvariantCulture) },
            { "elevation", "100" },
            { "port", "SkyWatcher" },
        });
        var driver = new Mount(device, sp).Driver;
        await driver.ConnectAsync(ct);
        await driver.SetSiteLatitudeAsync(latitude, ct);
        await driver.SetSiteLongitudeAsync(longitude, ct);
        var board = ((SkywatcherMountDriverBase<FakeDevice>)driver).SerialConnection.ShouldBeOfType<FakeSkywatcherSerialDevice>();
        return (driver, board, clock);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Melbourne, 145.1775)]
    [InlineData(Vienna, 16.3)]
    public async Task AtHomeTheEncodersStayAtZeroAndReadAsTheSitesPole(double latitude, double longitude)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, board, _) = await ConnectAsync(latitude, longitude, ct);

        board.Steps.ShouldBe((0, 0), "connecting at home writes nothing to the encoders");
        (await driver.AtHomeAsync(ct)).ShouldBeTrue();
        (await driver.GetDeclinationAsync(ct)).ShouldBe(Math.Sign(latitude) * 90.0, 1e-6);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Melbourne, 145.1775)]
    [InlineData(Vienna, 16.3)]
    public async Task AGotoEastOfTheMeridianTurnsRaByTheHourAngleItNeeds(double latitude, double longitude)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, board, clock) = await ConnectAsync(latitude, longitude, ct);
        var lst = await driver.GetSiderealTimeAsync(ct);

        // 1.1 h east of the meridian on the equator, as the AZ-GTi was sent: through the pole, the RA axis turns
        // 6 h - 1.1 h = 4.9 h (73.5 degrees) from home, never 90 degrees more.
        await driver.BeginSlewRaDecAsync(CoordinateUtils.ConditionRA(lst + 1.1), 0.0, ct);
        for (var i = 0; i < 60 && await driver.IsSlewingAsync(ct); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        var raDegrees = Math.Abs(board.Steps.Ra) / 9024000.0 * 360.0;
        raDegrees.ShouldBe(73.5, 1.0, "the RA axis turns 73.5 degrees, not 163.5");
        (Math.Abs(board.Steps.Dec) / 9024000.0 * 360.0).ShouldBe(90.0, 0.5, "pole to equator");
    }

    [Fact(Timeout = 30_000)]
    public async Task ASiteInTheOtherHemisphereReadsTheNewPoleAtHome()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, board, _) = await ConnectAsync(Vienna, 16.3, ct);
        (await driver.GetDeclinationAsync(ct)).ShouldBe(90.0, 1e-6, "premise: at home under Vienna, at the north pole");

        await driver.SetSiteLatitudeAsync(Melbourne, ct);
        await driver.SetSiteLongitudeAsync(145.1775, ct);

        (await driver.GetDeclinationAsync(ct)).ShouldBe(-90.0, 1e-6, "at home in Melbourne the mount points at the south pole");
        (await driver.AtHomeAsync(ct)).ShouldBeTrue();
        board.Steps.ShouldBe((0, 0));
    }
}
