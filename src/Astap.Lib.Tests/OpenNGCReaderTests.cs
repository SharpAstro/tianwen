using Astap.Lib.Astrometry;
using Shouldly;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests
{
    public class OpenNGCReaderTests
    {
        const CatalogIndex IC0715NW = (CatalogIndex)((ulong)'I' << 56 | (ulong)'0' << 48 | (ulong)'7' << 40 | (ulong)'1' << 32 | '5' << 24 | '_' << 16 | 'N' << 8 | 'W');

        [Theory]
        [InlineData("NGC7293", ObjectType.PlanetaryNebula, (CatalogIndex)((ulong)'N' << 32 | '7' << 24 | '2' << 16 | '9' << 8 | '3'), Constellation.Aquarius)]
        [InlineData("NGC0056", ObjectType.Other, (CatalogIndex)((ulong)'N' << 32 | '0' << 24 | '0' << 16 | '5' << 8 | '6'), Constellation.Pisces)]
        [InlineData("IC1000", ObjectType.Galaxy, (CatalogIndex)((ulong)'I' << 32 | '1' << 24 | '0' << 16 | '0' << 8 | '0'), Constellation.Bootes)]
        [InlineData("IC0715NW", ObjectType.Galaxy, IC0715NW, Constellation.Crater)]
        public async Task GivenObjectIdWhenLookingItUpThenAnEntryIsReturned(
            string indexEntry,
            ObjectType expectedObjType,
            CatalogIndex expectedCatalogIindex,
            Constellation expectedConstellation
        )
        {
            // given
            var reader = new OpenNGCReader();
            var (actualRead, actualFailed) = await reader.ReadEmbeddedDataAsync();

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
        [InlineData("U2")]
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
