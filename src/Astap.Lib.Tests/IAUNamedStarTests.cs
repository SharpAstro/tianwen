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
        var (processed, failed) = await new IAUNamedStarDB().InitDBAsync();

        processed.ShouldBe(451);
        failed.ShouldBe(0);
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
        indices.ShouldNotBeNull().Length.ShouldBe(1);

        indices[0].ShouldBe(designationCatIndex);
    }

    [Fact]
    public async Task GivenAllCatIdxWhenTryingToLookupThenItIsAlwaysFound()
    {
        // given
        var db = new IAUNamedStarDB();
        var (processed, failed) = await db.InitDBAsync();
        var idxs = db.ObjectIndices;

        // when
        Parallel.ForEach(idxs, idx =>
        {
            db.TryLookupByIndex(idx, out _).ShouldBeTrue($"{idx.ToAbbreviation()} [{idx}] was not found!");
        });

        // then
        processed.ShouldBeGreaterThanOrEqualTo(processed);
        failed.ShouldBe(0);
    }
}
