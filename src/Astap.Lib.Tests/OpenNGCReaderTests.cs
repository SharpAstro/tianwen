using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests
{
    public class OpenNGCReaderTests
    {
        const CatalogIndex NGC7293 = (CatalogIndex)((ulong)'N' << 32 | '7' << 24 | '2' << 16 | '9' << 8 | '3');
        const CatalogIndex NGC0056 = (CatalogIndex)((ulong)'N' << 32 | '0' << 24 | '0' << 16 | '5' << 8 | '6');
        const CatalogIndex IC1000 = (CatalogIndex)((ulong)'I' << 32 | '1' << 24 | '0' << 16 | '0' << 8 | '0');
        const CatalogIndex IC0715NW = (CatalogIndex)((ulong)'I' << 56 | (ulong)'0' << 48 | (ulong)'7' << 40 | (ulong)'1' << 32 | '5' << 24 | '_' << 16 | 'N' << 8 | 'W');
        const CatalogIndex IC0720_NED02 = (CatalogIndex)((ulong)'I' << 56 | (ulong)'0' << 48 | (ulong)'7' << 40 | (ulong)'2' << 32 | '0' << 24 | 'N' << 16 | '0' << 8 | '2');
        const CatalogIndex M040 = (CatalogIndex)('M' << 24 | '0' << 16 | '4' << 8 | '0');
        const CatalogIndex M102 = (CatalogIndex)('M' << 24 | '1' << 16 | '0' << 8 | '2');
        const CatalogIndex ESO056_115 = (CatalogIndex)((ulong)'E' << 56 | (ulong)'0' << 48 | (ulong)'5' << 40 | (ulong)'6' << 32 | '-' << 24 | '1' << 16 | '1' << 8 | '5');

        [Theory]
        [InlineData(NGC7293, "N7293")]
        [InlineData(NGC0056, "N0056")]
        [InlineData(IC1000, "I1000")]
        [InlineData(IC0715NW, "I0715_NW")]
        [InlineData(IC0720_NED02, "I0720N02")]
        [InlineData(M040, "M040")]
        [InlineData(M102, "M102")]
        [InlineData(ESO056_115, "E056-115")]
        public void GivenACatalogIndexValueWhenGettingAbbreviationThenItIsReturned(CatalogIndex catalogIndex, string expectedAbbreviation)
        {
            catalogIndex.ToAbbreviation().ShouldBe(expectedAbbreviation);
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
            var found = reader.TryLookupByIndex(indexEntry, out var deepSkyObject);

            // then
            actualRead.ShouldBeGreaterThan(13000);
            actualFailed.ShouldBe(0);
            found.ShouldBeTrue();
            deepSkyObject.ShouldNotBeNull();
            deepSkyObject.Index.ShouldBe(expectedCatalogIindex);
            deepSkyObject.ObjType.ShouldBe(expectedObjType);
            deepSkyObject.Constellation.ShouldBe(expectedConstellation);
            deepSkyObject.RA.ShouldBe(expectedRaDeg);
            deepSkyObject.Dec.ShouldBe(expectedDecDeg);
        }

        [Theory]
        [InlineData("N11", "N0011")]
        [InlineData("NGC0011", "N0011")]
        [InlineData("NC 120", "N0120")]
        [InlineData("NCG00055", "N0055")]
        [InlineData("NCGX999", "N0999")]
        [InlineData("I 999", "I0999")]
        [InlineData("M12", "M012")]
        [InlineData(" M12", "M012")]
        [InlineData("M00013", "M013")]
        [InlineData("Messier 120", "M120")]
        [InlineData("IC4473 NED01", "I4473N01")]
        [InlineData("ESO 56-115", "E056-115")]
        [InlineData("ESO351-030", "E351-030")]
        [InlineData("ESO356 - 004", "E356-004")]
        [InlineData("Cl 399", "Cl399")]
        [InlineData("C041", "C041")]
        [InlineData("C 40", "C040")]
        [InlineData("NGC0526A", "N0526_A")]
        [InlineData("NGC 0526 B", "N0526_B")]
        [InlineData("N 0526_C", "N0526_C")]
        [InlineData("IC0715NW", "I0715_NW")]
        [InlineData("IC0133S", "I0133_S")]
        public void GivenAUserInputWhenCleaningItUpThenACleanedupEntryIsReturned(string input, string expectedOutput)
        {
            var success = OpenNGCReader.TryGetCleanedUpCatalogName(input, out var actualCleanedUp);

            success.ShouldBeTrue();
            actualCleanedUp.ShouldNotBeNull();
            actualCleanedUp.ShouldBe(expectedOutput);
        }

        [Theory]
        [InlineData("Not an index")]
        [InlineData("   ")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("I am not the index you are looking for")]
        [InlineData("Maybe not")]
        [InlineData("4Sq")]
        [InlineData("N 0526__")]
        [InlineData("N 0526 ABC01")]
        public void GivenAnInvalidUserInputWhenCleaningUpThenNothingIsReturned(string input)
        {
            var success = OpenNGCReader.TryGetCleanedUpCatalogName(input, out var actualCleanedUp);

            success.ShouldBeFalse();
            actualCleanedUp.ShouldBeNull();
        }
    }
}
