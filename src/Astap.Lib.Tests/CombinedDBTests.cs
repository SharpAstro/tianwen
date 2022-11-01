using Astap.Lib.Astrometry;
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
