using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The info panel's designation line carries the popular number beside the primary designation: the
/// Lagoon read "NGC 6523" while its overlay label stacked M8 (2026-09-18), and M8 is what people know.
/// </summary>
public sealed class SkyMapInfoPanelDesignationTests
{
    [Theory]
    [InlineData("M8", "NGC 6523 (M8)")]
    [InlineData("NGC6523", "NGC 6523 (M8)")]
    [InlineData("NGC7000", "NGC 7000 (C20)")]
    [InlineData("NGC2023", "NGC 2023")]
    public async Task ThePrimaryDesignationCarriesTheMessierOrCaldwellNumber(string lookup, string expected)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex(lookup, out var obj).ShouldBeTrue(lookup);

        SkyMapInfoPanelData.DesignationLine(obj.Index, db).ShouldBe(expected);
    }

    [Fact]
    public async Task ThePanelPayloadAndItsSubtitleUseTheLine()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        db.TryLookupByIndex("M8", out var lagoon).ShouldBeTrue();
        var now = new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(-37.9, 145.0, now);

        var withDb = SkyMapInfoPanelData.FromCatalogObject(lagoon, -37.9, 145.0, now, site, null, db);
        withDb.Canonical.ShouldBe("NGC 6523 (M8)");
        ObjectInfoPanel.SubtitleLine(in withDb).ShouldStartWith("NGC 6523 (M8)  Sgr");

        // Without a catalogue there is no cross-index to consult, and the line is the primary one alone.
        var withoutDb = SkyMapInfoPanelData.FromCatalogObject(lagoon, -37.9, 145.0, now, site, null);
        withoutDb.Canonical.ShouldBe("NGC 6523");
    }
}
