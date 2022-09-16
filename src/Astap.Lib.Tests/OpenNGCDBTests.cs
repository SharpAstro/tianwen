using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class OpenNGCDBTests
{

    [Theory]
    [InlineData("ESO056-115", ObjectType.Galaxy, ESO056_115, Constellation.Dorado, 80.89375d, -57.65833333333333d)]
    [InlineData("IC1000", ObjectType.Galaxy, IC1000, Constellation.Bootes, 214.91795833333333d, 29.820416666666667d)]
    [InlineData("IC0715NW", ObjectType.Galaxy, IC0715NW, Constellation.Crater, 174.225875d, -2.3629166666666666d)]
    [InlineData("M102", ObjectType.Duplicate, M102, Constellation.UrsaMajor, 210.80225d, 59.23416666666667d)]
    [InlineData("M40", ObjectType.DoubleStar, M040, Constellation.UrsaMajor, 185.56708333333333d, 59.266666666666666d)]
    [InlineData("NGC0056", ObjectType.Other, NGC0056, Constellation.Pisces, 3.8360833333333333d, 18.667916666666667d)]
    [InlineData("NGC7293", ObjectType.PlanetaryNebula, NGC7293, Constellation.Aquarius, 337.41070833333333d, -7.44d)]
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
        var reader = new OpenNGCDB();
        var (actualRead, actualFailed) = await reader.ReadEmbeddedDataFilesAsync();

        // when
        var found = reader.TryLookupByIndex(indexEntry, out var celestialObject);

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
