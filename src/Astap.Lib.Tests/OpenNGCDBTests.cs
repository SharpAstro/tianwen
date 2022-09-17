using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class OpenNGCDBTests
{
    [Theory]
    [InlineData("ESO056-115", ObjectType.Galaxy, ESO056_115, Constellation.Dorado, 80.89375d, -69.75611111111111d)]
    [InlineData("IC0458", ObjectType.Galaxy, IC0458, Constellation.Lynx, 107.64229166666667d, 50.118916666666664d)]
    [InlineData("IC0715NW", ObjectType.Galaxy, IC0715NW, Constellation.Crater, 174.225875d, -8.375805555555557d)]
    [InlineData("IC1000", ObjectType.Galaxy, IC1000, Constellation.Bootes, 214.91795833333333d, 17.854694444444444d)]
    [InlineData("IC1577", ObjectType.Galaxy, IC0048, Constellation.Cetus, 10.893625d, -8.1865d)]
    [InlineData("M102", ObjectType.Galaxy, NGC5457, Constellation.UrsaMajor, 210.80225d, 54.34894444444445d)]
    [InlineData("M40", ObjectType.DoubleStar, M040, Constellation.UrsaMajor, 185.56708333333333d, 58.08444444444444d)]
    [InlineData("NGC0056", ObjectType.Other, NGC0056, Constellation.Pisces, 3.8360833333333333d, 12.444527777777777d)]
    [InlineData("NGC7293", ObjectType.PlanetaryNebula, NGC7293, Constellation.Aquarius, 337.41070833333333d, -20.837333333333333d)]
    public async Task GivenObjectIdWhenLookingItUpThenAnEntryIsReturned(
        string indexEntry,
        ObjectType expectedObjType,
        CatalogIndex expectedCatalogIindex,
        Constellation expectedConstellation,
        double expectedRaDeg,
        double expectedDecDeg
    )
    {
        // given
        var db = new OpenNGCDB();
        var (actualRead, actualFailed) = await db.ReadEmbeddedDataFilesAsync();

        // when
        var found = db.TryLookupByIndex(indexEntry, out var celestialObject);

        // then
        actualRead.ShouldBeGreaterThan(13000);
        actualFailed.ShouldBe(0);
        found.ShouldBeTrue();
        celestialObject.ShouldNotBeNull();
        celestialObject.Index.ShouldBe(expectedCatalogIindex);
        celestialObject.ObjectType.ShouldBe(expectedObjType);
        celestialObject.Constellation.ShouldBe(expectedConstellation);
        celestialObject.RA.ShouldBe(expectedRaDeg);
        celestialObject.Dec.ShouldBe(expectedDecDeg);
    }
}
