using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using static Astap.Lib.Astrometry.Catalogs.CatalogUtils;

namespace Astap.Lib.Tests;

public class IAUNamedStarTests
{
    [Fact]
    public async Task ReadStarsTest()
    {
        var db = new IAUNamedStarDB();
        var (processed, failed) = await db.InitDBAsync();

        failed.ShouldBe(0);
        processed.ShouldBe(451);

        db.CommonNames.Count.ShouldBeGreaterThanOrEqualTo(processed);
    }

    [Theory]
    [InlineData("Achernar", "HR 472")]
    [InlineData("Geminga", "PSR B0633+17")]
    public async Task GivenAnIAUStarNameWhenGettingNamedStarThenItIsFound(string iauName, string expectedDesignation)
    {
        var db = new IAUNamedStarDB();
        _ = await db.InitDBAsync();
        TryGetCleanedUpCatalogName(expectedDesignation, out var designationCatIndex).ShouldBeTrue();

        var actualFound = db.TryResolveCommonName(iauName, out var indices);

        actualFound.ShouldBeTrue();
        indices.ShouldNotBeNull().Count.ShouldBe(1);

        indices[0].ShouldBe(designationCatIndex);
    }

    [Fact]
    public async Task GivenAllCatIdxWhenTryingToLookupThenTheyAreAllFound()
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
        processed.ShouldBeGreaterThan(0);
        failed.ShouldBe(0);
    }

    [Fact]
    public async Task GivenAllIAUNamedBrightestStarsPerConstellationWhenTryingToLookupByNameThenTheyAreAllFound()
    {
        // given
        var db = new IAUNamedStarDB();
        var (processed, failed) = await db.InitDBAsync();
        var constellations = Enum.GetValues<Constellation>();
        var foundStars = 0;

        // when
        Parallel.ForEach(constellations, constellation =>
        {
            var name = constellation.ToBrighestStarName();
            // a proper given name would not start with a greek letter
            if (char.IsAscii(name[0]))
            {
                db.TryResolveCommonName(name, out _).ShouldBeTrue($"Star {name} in {constellation} was not found!");

                Interlocked.Increment(ref foundStars);
            }
            else
            {
                var genitive = constellation.ToGenitive();
                name.Contains(genitive).ShouldBeTrue($"Star {name} with Bayer designation in {constellation} should use genetive form {genitive}!");
            }
        });

        // then
        foundStars.ShouldBeGreaterThan(0);
        processed.ShouldBeGreaterThan(0);
        failed.ShouldBe(0);
    }
}
