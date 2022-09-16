using Astap.Lib.Astrometry;
using Shouldly;
using Xunit;

namespace Astap.Lib.Tests;

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
    [InlineData("N11", "N0011", Catalog.NGC)]
    [InlineData("NGC0011", "N0011", Catalog.NGC)]
    [InlineData("NC 120", "N0120", Catalog.NGC)]
    [InlineData("NCG00055", "N0055", Catalog.NGC)]
    [InlineData("NCGX999", "N0999", Catalog.NGC)]
    [InlineData("I 999", "I0999", Catalog.IC)]
    [InlineData("M12", "M012", Catalog.Messier)]
    [InlineData(" M12", "M012", Catalog.Messier)]
    [InlineData("M00013", "M013", Catalog.Messier)]
    [InlineData("Messier 120", "M120", Catalog.Messier)]
    [InlineData("IC4473 NED01", "I4473N01", Catalog.IC)]
    [InlineData("ESO 56-115", "E056-115", Catalog.ESO)]
    [InlineData("ESO351-030", "E351-030", Catalog.ESO)]
    [InlineData("ESO356 - 004", "E356-004", Catalog.ESO)]
    [InlineData("Cl 399", "Cr399", Catalog.Collinder)]
    [InlineData("C041", "C041", Catalog.Caldwell)]
    [InlineData("C 40", "C040", Catalog.Caldwell)]
    [InlineData("NGC0526A", "N0526_A", Catalog.NGC)]
    [InlineData("NGC 0526 B", "N0526_B", Catalog.NGC)]
    [InlineData("N 0526_C", "N0526_C", Catalog.NGC)]
    [InlineData("IC0715NW", "I0715_NW", Catalog.IC)]
    [InlineData("IC0133S", "I0133_S", Catalog.IC)]
    [InlineData("HR 4730", "HR4730", Catalog.HR)]
    [InlineData("XO 1", "XO0001", Catalog.XO)]
    [InlineData("XO-2S", "XO002S", Catalog.XO)]
    [InlineData("XO - 2N", "XO002N", Catalog.XO)]
    [InlineData("HAT-P-23", "HAT-P023", Catalog.HAT_P)]
    [InlineData("HATS 23", "HATS023", Catalog.HATS)]
    [InlineData("PSR J2144-3933", "PrJBDAeuw", Catalog.PSR)]
    [InlineData("PSR B0633+17", "PrBATyAIg", Catalog.PSR)]
    [InlineData("PSR J2400-9000", "PrJBLBGUQ", Catalog.PSR)]
    [InlineData("PSR B2400-90", "PrBBLAAtQ", Catalog.PSR)]
    [InlineData("GJ 551", "GJ0551", Catalog.GJ)]
    [InlineData("Sh 2 - 6", "Sh2-006", Catalog.Sharpless)]
    [InlineData("Tres - 3", "TrES03", Catalog.TrES)]
    [InlineData("WASP-11", "WASP011", Catalog.WASP)]
    public void GivenAUserInputWhenCleaningItUpThenACleanedupEntryIsReturned(string input, string expectedOutput, Catalog expectedCatalog)
    {
        var success = Utils.TryGetCleanedUpCatalogName(input, out var actualCatalogIndex);

        success.ShouldBeTrue();
        actualCatalogIndex.ShouldNotBe((CatalogIndex)0);
        var actualAbbreviation = EnumHelper.EnumValueToAbbreviation((ulong)actualCatalogIndex);
        actualAbbreviation.ShouldBe(expectedOutput);
        actualCatalogIndex.ToCatalog().ShouldBe(expectedCatalog);
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
