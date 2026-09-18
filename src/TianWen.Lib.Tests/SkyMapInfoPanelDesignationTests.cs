using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The info panel names each designation ONCE, the best-known one first. The title is the common name,
/// else the Messier or Caldwell number, else the primary designation; the grey line carries whichever of
/// the popular number and the primary the title does not. It read "NGC 6523" for the Lagoon while the
/// overlay stacked M8, then "NGC 6523 (M8)", which for an unnamed object repeated the title: M21's panel
/// read "NGC 6531" over "NGC 6531 (M21)" (2026-09-18).
/// </summary>
public sealed class SkyMapInfoPanelDesignationTests
{
    [Theory]
    [InlineData("M8", "Lagoon Nebula", "M8 · NGC 6523")]
    [InlineData("NGC6523", "Lagoon Nebula", "M8 · NGC 6523")]
    [InlineData("M21", "M21", "NGC 6531")]
    [InlineData("NGC6531", "M21", "NGC 6531")]
    [InlineData("NGC6974", "NGC 6974", "")]
    public async Task EachDesignationAppearsOnceAndThePopularOneLeads(string lookup, string title, string line)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex(lookup, out var obj).ShouldBeTrue(lookup);

        SkyMapInfoPanelData.PanelTitle(obj, db).ShouldBe(title);
        SkyMapInfoPanelData.DesignationLine(obj, db).ShouldBe(line);
    }

    [Fact]
    public async Task ACaldwellNumberLeadsWhenThereIsNoMessierNumber()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("NGC7000", out var northAmerica).ShouldBeTrue();

        SkyMapInfoPanelData.DesignationLine(northAmerica, db).ShouldBe("C20 · NGC 7000");
    }

    [Fact]
    public async Task ThePanelPayloadAndItsSubtitleUseTheLine()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M21", out var m21).ShouldBeTrue();
        var now = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(-37.9, 145.0, now);

        var withDb = SkyMapInfoPanelData.FromCatalogObject(m21, -37.9, 145.0, now, site, null, db);
        withDb.Name.ShouldBe("M21");
        withDb.Canonical.ShouldBe("NGC 6531");
        ObjectInfoPanel.SubtitleLine(in withDb).ShouldBe("NGC 6531 · Sgr · Open Cluster");

        // Without a catalogue there is no cross-index to consult: the primary designation is the title and
        // the grey line has no designation left to add.
        var withoutDb = SkyMapInfoPanelData.FromCatalogObject(m21, -37.9, 145.0, now, site, null);
        withoutDb.Name.ShouldBe("NGC 6531");
        withoutDb.Canonical.ShouldBe("");
    }

    /// <summary>
    /// A link's <c>object=</c> token is the PRIMARY designation, never the display line: when the line grew
    /// the Messier number, every link built from it ("NGC 6523 (M8)") stopped being a designation the atlas
    /// could parse, the web address bar included.
    /// </summary>
    [Theory]
    [InlineData("M8", "NGC 6523")]
    [InlineData("M21", "NGC 6531")]
    [InlineData("NGC7000", "NGC 7000")]
    public async Task TheLinkTokenIsThePrimaryDesignationNotTheDisplayLine(string lookup, string token)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex(lookup, out var obj).ShouldBeTrue(lookup);
        var now = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(-37.9, 145.0, now);

        SkyMapInfoPanelData.FromCatalogObject(obj, -37.9, 145.0, now, site, null, db).LinkToken.ShouldBe(token);
    }
}
