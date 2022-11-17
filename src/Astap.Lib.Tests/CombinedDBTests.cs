using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using System.Threading.Tasks;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class CombinedDBTests
{
    [Theory]
    [InlineData("Acamar", HR0897)]
    [InlineData("Coalsack Nebula", C099)]
    public async Task GivenANameWhenTryingToResolveItIsFound(string name, params CatalogIndex[] expectedIndices)
    {
        var db = new CombinedDB();
        _ = await db.InitDBAsync();

        var found = db.TryResolveCommonName(name, out var matches);

        found.ShouldBeTrue();
        matches.ShouldNotBeNull().Count.ShouldBe(expectedIndices.Length);
        matches.ShouldBe(expectedIndices);
    }

    [Theory]
    [InlineData(HR0897, "Acamar", "θ1 Eri A")]
    [InlineData(HR1084, "Ran", "ε Eri")]
    [InlineData(C099, "Coalsack Nebula")]
    [InlineData(M042, "Great Orion Nebula", "Orion Nebula")]
    [InlineData(NGC1976, "Great Orion Nebula", "Orion Nebula")]
    [InlineData(M051, "Whirlpool Galaxy")]
    [InlineData(NGC3372, "Car Nebula", "Carina Nebula", "eta Car Nebula", "Keyhole", "Keyhole Nebula")]
    public async Task GivenACatalogIndexWhenTryingToGetCommonNamesThenTheyAreFound(CatalogIndex catalogIndex, params string[] expectedNames)
    {
        var db = new CombinedDB();
        _ = await db.InitDBAsync();

        var found = db.TryLookupByIndex(catalogIndex, out var match);

        found.ShouldBeTrue();
        match.CommonNames.Count.ShouldBe(expectedNames.Length);
        match.CommonNames.ShouldBe(expectedNames, ignoreOrder: true);
    }

    [Theory]
    [InlineData(HR0897, WDS_02583_4018s)]
    [InlineData(C099)]
    [InlineData(M042, NGC1976)]
    [InlineData(NGC1976, M042)]
    [InlineData(M051, NGC5194)]
    [InlineData(M054, NGC6715)]
    [InlineData(NGC6715, M054)]
    public async Task GivenACatalogIndexWhenTryingToGetCrossIndicesThenTheyAreFound(CatalogIndex catalogIndex, params CatalogIndex[] expectedCrossIndices)
    {
        var db = new CombinedDB();
        _ = await db.InitDBAsync();

        var found = db.TryGetCrossIndices(catalogIndex, out var matches);

        found.ShouldBe(expectedCrossIndices.Length > 0);
        matches.Count.ShouldBe(expectedCrossIndices.Length);
        matches.ShouldBe(expectedCrossIndices);
    }

    [Fact]
    public async Task GivenAllCatIdxWhenTryingToLookupThenItIsAlwaysFound()
    {
        // given
        var db = new CombinedDB();
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
