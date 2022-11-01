using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class IAUNamedStarTests
{
    [Fact]
    public async Task ReadStarsTest()
    {
        var db = new IAUNamedStarDB();
        var (processed, failed) = await db.InitDBAsync();

        processed.ShouldBe(451);
        failed.ShouldBe(0);

        db.CommonNames.Count.ShouldBe(processed);
    }

    [Theory]
    [InlineData("Achernar", "HR 472")]
    [InlineData("Geminga", "PSR B0633+17")]
    public async Task GivenAnIAUStarNameWhenGettingNamedStarThenItIsFound(string iauName, string expectedDesignation)
    {
        var db = new IAUNamedStarDB();
        _ = await db.InitDBAsync();
        Utils.TryGetCleanedUpCatalogName(expectedDesignation, out var designationCatIndex).ShouldBeTrue();

        var actualFound = db.TryResolveCommonName(iauName, out var indices);

        actualFound.ShouldBeTrue();
        indices.ShouldNotBeNull().Count.ShouldBe(1);

        indices[0].ShouldBe(designationCatIndex);
    }

    [Fact]
    public async Task GivenAllCatIdxWhenTryingToLookupThenItIsAlwaysFound()
    {
        // given
        var db = new IAUNamedStarDB();
        var (processed, failed) = await db.InitDBAsync();
        var idxs = db.ObjectIndices;
        var catalogs = db.Catalogs;

        // when
        Parallel.ForEach(idxs, idx =>
        {
            db.TryLookupByIndex(idx, out _).ShouldBeTrue($"{idx.ToAbbreviation()} [{idx}] was not found!");
            catalogs.ShouldContain(idx.ToCatalog());
        });

        // then
        processed.ShouldBeGreaterThanOrEqualTo(processed);
        failed.ShouldBe(0);
    }
}
