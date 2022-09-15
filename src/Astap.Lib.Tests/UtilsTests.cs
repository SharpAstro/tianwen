using Astap.Lib.Astrometry;
using Shouldly;
using Xunit;

namespace Astap.Lib.Tests
{
    public class UtilsTests
    {

        [Theory]
        [InlineData("05:23:34.5", 80.89375d)]
        [InlineData("23:54:13.2", 358.555d)]
        [InlineData("23:59:59.9", 359.9995833333333d)]
        [InlineData("0:0:0", 0d)]
        public void GivenHMSWHenConvertToDegreesItReturnsDegreesAsDouble(string hms, double expectedDegrees)
        {
            Utils.HMSToDegree(hms).ShouldBe(expectedDegrees);
        }

        [Theory]
        [InlineData("+61:44:24", 72.1d)]
        [InlineData("-12:03:18", -11.175d)]
        [InlineData("+90:0:0", +90.0d)]
        [InlineData("-90:0:0", -90.0d)]
        [InlineData("0:0:0", 0.0d)]
        public void GivenDMSWHenConvertToDegreesItReturnsDegreesAsDouble(string dms, double expectedDegrees)
        {
            Utils.DMSToDegree(dms).ShouldBe(expectedDegrees);
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
        [InlineData("Cl 399", "Cr399")]
        [InlineData("C041", "C041")]
        [InlineData("C 40", "C040")]
        [InlineData("NGC0526A", "N0526_A")]
        [InlineData("NGC 0526 B", "N0526_B")]
        [InlineData("N 0526_C", "N0526_C")]
        [InlineData("IC0715NW", "I0715_NW")]
        [InlineData("IC0133S", "I0133_S")]
        [InlineData("HR 4730", "HR4730")]
        public void GivenAUserInputWhenCleaningItUpThenACleanedupEntryIsReturned(string input, string expectedOutput)
        {
            var success = Utils.TryGetCleanedUpCatalogName(input, out var actualCatalogIndex);

            success.ShouldBeTrue();
            actualCatalogIndex.ShouldNotBe((CatalogIndex)0);
            var actualAbbreviation = EnumHelper.EnumValueToAbbreviation((ulong)actualCatalogIndex);
            actualAbbreviation.ShouldBe(expectedOutput);
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
            var success = Utils.TryGetCleanedUpCatalogName(input, out var actualCleanedUp);

            success.ShouldBeFalse();
            actualCleanedUp.ShouldBe((CatalogIndex)0);
        }
    }
}
