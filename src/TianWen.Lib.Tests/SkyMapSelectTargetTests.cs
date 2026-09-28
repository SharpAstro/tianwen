using System;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The planner's Show in atlas lands through <see cref="SkyMapSearchActions.SelectTarget"/>, one path for the
/// desktop and the web: the object centred, its info panel open, and a view the atlas has not homed yet kept
/// where it was put rather than turned to the pole by the first frame.
/// </summary>
[Collection("Astrometry")]
public class SkyMapSelectTargetTests
{
    private static readonly DateTimeOffset Evening = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private const double Lat = -37.9, Lon = 145.2;

    [Fact]
    public async Task ACataloguedTargetIsCentredAndItsPanelOpens()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex(CatalogIndex.NGC1976, out var m42).ShouldBeTrue();
        var skyMap = new SkyMapState();
        var site = SiteContext.Create(Lat, Lon, Evening);

        SkyMapSearchActions.SelectTarget(skyMap.Search, skyMap, db, "M 42", m42.RA, m42.Dec, m42.Index, m42.ObjectType,
            new TextInputFocus(), Lat, Lon, Evening, site).ShouldBeTrue();

        skyMap.Search.InfoPanel.ShouldNotBeNull().Index.ShouldBe(CatalogIndex.NGC1976);
        skyMap.CenterRA.ShouldBe(m42.RA, 1e-9);
        skyMap.CenterDec.ShouldBe(m42.Dec, 1e-9);
        skyMap.ExternalViewPending.ShouldBeTrue("an atlas not yet homed keeps the view it was given");
    }

    [Fact]
    public async Task ABarePositionIsCentredUnderAPositionPanel()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var skyMap = new SkyMapState();
        var site = SiteContext.Create(Lat, Lon, Evening);

        SkyMapSearchActions.SelectTarget(skyMap.Search, skyMap, db, "Mosaic panel 2", 5.5, -69.0, null, ObjectType.Unknown,
            new TextInputFocus(), Lat, Lon, Evening, site).ShouldBeTrue();

        var panel = skyMap.Search.InfoPanel.ShouldNotBeNull();
        panel.Name.ShouldBe("Mosaic panel 2");
        panel.Index.ShouldBeNull();
        skyMap.CenterRA.ShouldBe(5.5, 1e-9);
        skyMap.CenterDec.ShouldBe(-69.0, 1e-9);
    }
}
