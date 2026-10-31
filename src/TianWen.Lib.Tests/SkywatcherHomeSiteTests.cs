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

    [Fact(Timeout = 30_000)]
    public async Task ASiteInTheOtherHemisphereIsRefusedForAMountAwayFromHome()
    {
        // Away from home the same axes would read as the mirror of where the tube points, and the next goto would go there.
        var ct = TestContext.Current.CancellationToken;
        var (driver, board, _) = await ConnectAsync(Vienna, 16.3, ct);
        await driver.SyncRaDecAsync(5.5, 30.0, ct);
        var before = board.Steps;

        var refused = await Should.ThrowAsync<MountSiteHemisphereException>(
            async () => await driver.SetSiteAsync(new SiteCoordinates(Melbourne, 145.1775), ct));

        refused.Message.ShouldContain("not at home");
        (await driver.GetSiteLatitudeAsync(ct)).ShouldBe(Vienna, "the mount keeps its site");
        board.Steps.ShouldBe(before);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASiteInTheSameHemisphereGoesToAMountAnywhere()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, _, _) = await ConnectAsync(Vienna, 16.3, ct);
        await driver.SyncRaDecAsync(5.5, 30.0, ct);

        await driver.SetSiteAsync(new SiteCoordinates(52.5, 13.4), ct);

        (await driver.GetSiteLatitudeAsync(ct)).ShouldBe(52.5);
    }

    [Fact(Timeout = 30_000)]
    public async Task ASiteInTheOtherHemisphereGoesToAMountAtHome()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, _, _) = await ConnectAsync(Vienna, 16.3, ct);

        await driver.SetSiteAsync(new SiteCoordinates(Melbourne, 145.1775), ct);

        (await driver.GetSiteLatitudeAsync(ct)).ShouldBe(Melbourne);
        (await driver.GetDeclinationAsync(ct)).ShouldBe(-90.0, 1e-6);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(Melbourne, 145.1775)]
    [InlineData(Vienna, 16.3)]
    public async Task HomeSlewsBothAxesBackToThePowerOnPositionAndStops(double latitude, double longitude)
    {
        // A Synta board has no home sensor: home is where it was switched on, encoder zero, and Home goes back there.
        var ct = TestContext.Current.CancellationToken;
        var (driver, board, clock) = await ConnectAsync(latitude, longitude, ct);
        driver.CanFindHome.ShouldBeTrue();
        var lst = await driver.GetSiderealTimeAsync(ct);
        await driver.SetTrackingAsync(true, ct);
        await driver.BeginSlewRaDecAsync(CoordinateUtils.ConditionRA(lst + 1.1), 0.0, ct);
        for (var i = 0; i < 60 && await driver.IsSlewingAsync(ct); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        board.Steps.ShouldNotBe((0, 0), "premise: the goto took it away from home");

        var home = driver.FindHomeAsync(ct).AsTask();
        while (!home.IsCompleted)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }
        await home;

        board.Steps.ShouldBe((0, 0));
        (await driver.AtHomeAsync(ct)).ShouldBeTrue();
        board.RaMotion.Running.ShouldBeFalse("stopped at home, tracking included");
        (await driver.GetDeclinationAsync(ct)).ShouldBe(Math.Sign(latitude) * 90.0, 1e-6);
    }

    /// <summary>
    /// GSServer's reading of a SkyWatcher GEM's encoders (Axes.AxesMountToApp, then AxesXyToRaDec), the oracle: mount axes are
    /// home (90, 90) plus the turns; the app axes are (x, 180 - y) north and (-x, 180 - y) south; past 90 in Y the solution is
    /// through the pole. Independent of the driver's own mapping, which the fake board can only read back.
    /// </summary>
    private static (double Ha, double Dec) GssRead(int raSteps, int decSteps, int cpr, bool south)
    {
        var mx = 90.0 + raSteps * 360.0 / cpr;
        var my = 90.0 + decSteps * 360.0 / cpr;
        var (ax, ay) = south ? (-mx, 180.0 - my) : (mx, 180.0 - my);
        if (ay > 90.0)
        {
            ax += 180.0;
            ay = 180.0 - ay;
        }
        return (CoordinateUtils.ConditionHA(ax / 15.0), south ? -ay : ay);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(Melbourne, 145.1775, 0.3, Melbourne)]   // just west of the zenith (2026-10-31: pointed 14 degrees below the horizon)
    [InlineData(Melbourne, 145.1775, -0.3, Melbourne)]  // just east of the zenith
    [InlineData(Melbourne, 145.1775, -1.1, 0.0)]
    [InlineData(Melbourne, 145.1775, 2.5, -60.0)]
    [InlineData(Melbourne, 145.1775, -4.0, 10.0)]
    [InlineData(Vienna, 16.3, 0.3, Vienna)]
    [InlineData(Vienna, 16.3, -1.1, 0.0)]
    [InlineData(Vienna, 16.3, 2.5, 60.0)]
    [InlineData(Vienna, 16.3, -4.0, -10.0)]
    public async Task AGotoLeavesTheEncodersWhereGssReadsTheTarget(double latitude, double longitude, double hourAngle, double dec)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, board, clock) = await ConnectAsync(latitude, longitude, ct);
        var lst = await driver.GetSiderealTimeAsync(ct);

        await driver.BeginSlewRaDecAsync(CoordinateUtils.ConditionRA(lst - hourAngle), dec, ct);
        for (var i = 0; i < 60 && await driver.IsSlewingAsync(ct); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        await driver.SetTrackingAsync(false, ct);

        var (raSteps, decSteps) = board.Steps;
        var lstNow = await driver.GetSiderealTimeAsync(ct);
        var (gssHa, gssDec) = GssRead(raSteps, decSteps, 9024000, latitude < 0);
        var expectedHa = CoordinateUtils.ConditionHA(lstNow - CoordinateUtils.ConditionRA(lst - hourAngle));
        CoordinateUtils.ConditionHA(gssHa - expectedHa).ShouldBe(0.0, 0.02, $"GSS reads HA {gssHa:F3} h from {raSteps}/{decSteps} steps");
        gssDec.ShouldBe(dec, 0.05, "GSS reads the target's declination");
        Math.Abs(raSteps * 360.0 / 9024000).ShouldBeLessThanOrEqualTo(90.5, "counterweight at or below horizontal");
    }
}
