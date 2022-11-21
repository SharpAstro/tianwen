using Astap.Lib.Astrometry.Catalogs;
using Astap.Lib.Astrometry.NOVA;
using Shouldly;
using System;
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
    [InlineData(GUM033, "Car Nebula", "Carina Nebula", "eta Car Nebula", "Keyhole", "Keyhole Nebula")]
    [InlineData(NGC6302, "Bug Nebula", "Butterfly Nebula")]
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
    [InlineData(WDS_02583_4018s, HR0897)]
    [InlineData(C099)]
    [InlineData(M042, NGC1976)]
    [InlineData(NGC1976, M042)]
    [InlineData(M051, NGC5194)]
    [InlineData(M054, NGC6715)]
    [InlineData(NGC6715, M054)]
    [InlineData(GUM020, RCW_0036)]
    [InlineData(NGC3372, C092, GUM033, RCW_0053)]
    [InlineData(C092, NGC3372)]
    [InlineData(GUM033, NGC3372)]
    [InlineData(RCW_0053, NGC3372)]
    [InlineData(NGC6302, C069, GUM060, RCW_0124, Sh2_006)]
    [InlineData(C069, NGC6302)]
    [InlineData(GUM060, NGC6302)]
    [InlineData(RCW_0124, NGC6302)]
    [InlineData(Sh2_006, NGC6302)]
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
            db.TryLookupByIndex(idx, out var obj).ShouldBeTrue($"{idx.ToAbbreviation()} [{idx}] was not found!");
            catalogs.ShouldContain(idx.ToCatalog());
            var couldCalculate = ConstellationBoundary.TryFindConstellation(obj.RA, obj.Dec, out var calculatedConstellation);

            if (obj.ObjectType != ObjectType.Inexistent || obj.Constellation != 0)
            {
                couldCalculate.ShouldBeTrue();
                obj.Constellation.ShouldNotBe((Constellation)0, $"{idx.ToAbbreviation()} [{idx}]: Constellation should not be 0");
                calculatedConstellation.ShouldNotBe((Constellation)0, $"{idx.ToAbbreviation()} [{idx}]: Calculated constellation should not be 0");

                if (!obj.Constellation.IsContainedWithin(calculatedConstellation))
                {
                    var ra_s = CoordinateUtils.ConditionRA(obj.RA - 0.001);
                    var ra_l = CoordinateUtils.ConditionRA(obj.RA + 0.001);

                    var isBordering =
                        ConstellationBoundary.TryFindConstellation(ra_s, obj.Dec, out var const_s)
                        && ConstellationBoundary.TryFindConstellation(ra_l, obj.Dec, out var const_l)
                        && (obj.Constellation.IsContainedWithin(const_s) || const_l.IsContainedWithin(const_l));

                    isBordering.ShouldBeTrue($"{idx.ToAbbreviation()} [{idx}]: {obj.Constellation} is not contained within {calculatedConstellation} or any bordering");
                }
            }
        });

        // then
        processed.ShouldBeGreaterThanOrEqualTo(processed);
        failed.ShouldBe(0);
    }
}
