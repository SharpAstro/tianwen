using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class CombinedDBTests
{
    [Theory]
    [InlineData("Acamar")]
    public async Task GivenANameWhenTryingToResolveItIsFound(string name)
    {
        var db = new CombinedDB();
        _ = await db.InitDBAsync();

        var found = db.TryResolveCommonName(name, out var matches);

        found.ShouldBeTrue();
        matches.ShouldNotBeNull().Length.ShouldBeGreaterThan(0);
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
