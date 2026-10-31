using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A Skywatcher at home reports the pole of its site's hemisphere, and keeps doing so when the site changes before it has
/// moved (2026-10-31: an AZ-GTi connected under a profile still holding Vienna kept pointing at the north pole after the
/// site was corrected to Melbourne, physically pointing south, so any goto would have gone wrong).
/// </summary>
[Collection("Skywatcher")]
public class SkywatcherHomeSiteTests(ITestOutputHelper output)
{
    private const double Vienna = 48.2;
    private const double Melbourne = -37.8769444;

    private async Task<IMountDriver> ConnectAtViennaAsync(CancellationToken ct)
    {
        var timeProvider = new FakeTimeProviderWrapper(new DateTimeOffset(2026, 10, 31, 1, 30, 0, TimeSpan.Zero));
        var sp = new FakeExternal(output, timeProvider).BuildServiceProvider();
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "latitude", "48.2" },
            { "longitude", "16.3" },
            { "elevation", "200" },
            { "port", "SkyWatcher" },
        });
        var driver = new Mount(device, sp).Driver;
        await driver.ConnectAsync(ct);
        await driver.SetSiteLatitudeAsync(Vienna, ct);
        await driver.SetSiteLongitudeAsync(16.3, ct);
        return driver;
    }

    [Fact(Timeout = 30_000)]
    public async Task ASiteInTheOtherHemisphereReHomesAMountThatHasNotMoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var driver = await ConnectAtViennaAsync(ct);
        (await driver.GetDeclinationAsync(ct)).ShouldBe(90.0, 1e-6, "premise: at home under Vienna, at the north pole");
        (await driver.AtHomeAsync(ct)).ShouldBeTrue("the home sync does not take the mount away from home");

        await driver.SetSiteLatitudeAsync(Melbourne, ct);
        await driver.SetSiteLongitudeAsync(145.1775, ct);

        (await driver.GetDeclinationAsync(ct)).ShouldBe(-90.0, 1e-6, "at home in Melbourne the mount points at the south pole");
        (await driver.AtHomeAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task AMountSyncedElsewhereIsNotReHomedByANewSite()
    {
        var ct = TestContext.Current.CancellationToken;
        var driver = await ConnectAtViennaAsync(ct);
        await driver.SyncRaDecAsync(5.5, 30.0, ct);

        await driver.SetSiteLatitudeAsync(Melbourne, ct);
        await driver.SetSiteLongitudeAsync(145.1775, ct);

        (await driver.AtHomeAsync(ct)).ShouldBeFalse("a sync said where the mount is; a site change must not undo it");
        Math.Abs(await driver.GetDeclinationAsync(ct) + 90.0).ShouldBeGreaterThan(1.0, "not sent back to the pole");
    }
}
