using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <c>object_articles.gs.gz</c>, the verified Wikipedia articles <c>tools/bake-object-imagery</c> writes:
/// the format both ways, and what the embedded table answers through <see cref="ICelestialObjectDB.TryGetArticle"/>.
/// </summary>
public class ObjectArticleTableTests
{
    private static CatalogIndex Index(string name)
    {
        CatalogUtils.TryGetCleanedUpCatalogName(name, out var index).ShouldBeTrue(name);
        return index;
    }

    private static MemoryStream Written(params ObjectArticleRow[] rows)
    {
        var stream = new MemoryStream();
        ObjectArticleTable.Write(stream, rows);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void ARowReadsBackUnderEveryIndexItNames()
    {
        var carina = new ObjectArticle(50042, "Carina Nebula",
            new ObjectArticleImage("Carina Nebula by ESO.jpg", "2c", "CC BY 4.0", "ESO/T. Preibisch", "ESO", AttributionRequired: true, Width: 6000, Height: 4000));
        var noImage = new ObjectArticle(14273, "Coalsack Nebula", Image: null);

        using var stream = Written(
            new ObjectArticleRow(carina, [Index("NGC3372"), Index("C92")]),
            new ObjectArticleRow(noImage, [Index("C99")]));
        var table = ObjectArticleTable.Read(stream);

        table.Count.ShouldBe(3);
        table[Index("NGC3372")].ShouldBe(carina);
        table[Index("C92")].ShouldBe(carina);
        table[Index("C99")].ShouldBe(noImage);
        table[Index("C99")].Image.ShouldBeNull();
    }

    [Fact]
    public void ASeparatorByteInsideAValueCannotSplitTheRecord()
    {
        // Upstream text can carry anything. Were a record or group separator written through verbatim,
        // the fields after it would shift and the next article would parse as garbage.
        var odd = new ObjectArticle(1, "Odd\u001DTitle",
            new ObjectArticleImage("File\u001E.jpg", "ab", "CC0", "An\u001Fartist", "Line\nbreak", AttributionRequired: false, Width: 10, Height: 20));
        var next = new ObjectArticle(2, "Next", Image: null);

        using var stream = Written(new ObjectArticleRow(odd, [Index("M1")]), new ObjectArticleRow(next, [Index("M2")]));
        var table = ObjectArticleTable.Read(stream);

        table[Index("M1")].Title.ShouldBe("Odd Title");
        table[Index("M1")].Image.ShouldNotBeNull().Artist.ShouldBe("An artist");
        table[Index("M1")].Image.ShouldNotBeNull().Width.ShouldBe(10);
        table[Index("M2")].ShouldBe(next);
    }

    [Fact]
    public void AStreamWithoutTheHeaderIsRefused()
    {
        using var stream = new MemoryStream();
        using (var gz = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(Encoding.UTF8.GetBytes("SomethingElse\u001E1\u001D"));
        }
        stream.Position = 0;

        Should.Throw<InvalidDataException>(() => ObjectArticleTable.Read(stream));
    }

    [Theory]
    [InlineData("Andromeda Galaxy")]
    [InlineData("Hyades (star cluster)")]
    [InlineData("NGC 4567 and NGC 4568")]
    [InlineData("Barnard's Star")]
    public void TheUrlDecodesBackToTheTitle(string title)
    {
        var url = new ObjectArticle(1, title, Image: null).Url;

        url.ShouldStartWith("https://en.wikipedia.org/wiki/");
        url.ShouldNotContain(" ");
        Uri.UnescapeDataString(url["https://en.wikipedia.org/wiki/".Length..]).Replace('_', ' ').ShouldBe(title);
    }

    [Fact]
    public async Task TheEmbeddedTableAnswersForAnObjectByAnyOfItsNames()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);

        // The bake keys the catalogue's main entries. M 92 is not one (NGC 6341 is), so the article has to be
        // reached through the cross-indices, which is the path a planner pin or a search result on the
        // Messier name takes. M 42, by contrast, is a key of its own and would pass without that path.
        db.TryGetArticle(Index("NGC6341"), out var viaNgc).ShouldBeTrue();
        db.TryGetArticle(Index("M92"), out var viaMessier).ShouldBeTrue("M 92 should resolve through its cross-indices");
        viaMessier.ShouldBe(viaNgc);
        viaMessier.Title.ShouldBe("Messier 92");

        db.TryGetArticle(Index("M42"), out var orion).ShouldBeTrue();
        orion.Title.ShouldBe("Orion Nebula");
        orion.Image.ShouldNotBeNull().Licence.ShouldNotBeNullOrEmpty("an image must carry the licence its credit line needs");
    }
}
