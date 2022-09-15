using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests
{
    public class OpenNGCReaderTests
    {
        const CatalogIndex NGC7293 = (CatalogIndex)((ulong)'N' << 28 | '7' << 21 | '2' << 14 | '9' << 7 | '3');
        const CatalogIndex NGC0056 = (CatalogIndex)((ulong)'N' << 28 | '0' << 21 | '0' << 14 | '5' << 7 | '6');
        const CatalogIndex IC1000 = (CatalogIndex)((ulong)'I' << 28 | '1' << 21 | '0' << 14 | '0' << 7 | '0');
        const CatalogIndex IC0715NW = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'1' << 28 | '5' << 21 | '_' << 14 | 'N' << 7 | 'W');
        const CatalogIndex IC0720_NED02 = (CatalogIndex)((ulong)'I' << 49 | (ulong)'0' << 42 | (ulong)'7' << 35 | (ulong)'2' << 28 | '0' << 21 | 'N' << 14 | '0' << 7 | '2');
        const CatalogIndex M040 = (CatalogIndex)('M' << 21 | '0' << 14 | '4' << 7 | '0');
        const CatalogIndex M102 = (CatalogIndex)('M' << 21 | '1' << 14 | '0' << 7 | '2');
        const CatalogIndex ESO056_115 = (CatalogIndex)((ulong)'E' << 49 | (ulong)'0' << 42 | (ulong)'5' << 35 | (ulong)'6' << 28 | '-' << 21 | '1' << 14 | '1' << 7 | '5');

        [Theory]
        [InlineData(NGC7293, "N7293", Catalog.NGC)]
        [InlineData(NGC0056, "N0056", Catalog.NGC)]
        [InlineData(IC1000, "I1000", Catalog.IC)]
        [InlineData(IC0715NW, "I0715_NW", Catalog.IC)]
        [InlineData(IC0720_NED02, "I0720N02", Catalog.IC)]
        [InlineData(M040, "M040", Catalog.Messier)]
        [InlineData(M102, "M102", Catalog.Messier)]
        [InlineData(ESO056_115, "E056-115", Catalog.ESO)]
        public void GivenACatalogIndexValueWhenGettingAbbreviationThenItIsReturned(CatalogIndex catalogIndex, string expectedAbbreviation, Catalog expectedCatalog)
        {
            catalogIndex.ToAbbreviation().ShouldBe(expectedAbbreviation);
            catalogIndex.ToCatalog().ShouldBe(expectedCatalog);
        }

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
            var reader = new OpenNGCReader();
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
}
