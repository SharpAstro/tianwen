using Astap.Lib.Astrometry;
using Shouldly;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class UtilsTests
{
    [Theory]
    [InlineData("05:23:34.5", 5.392916666666667d)]
    [InlineData("23:54:13.2", 23.903666666666666d)]
    [InlineData("23:59:59.9", 23.999972222222222d)]
    [InlineData("12:00:00", 12d)]
    [InlineData("0:0:0", 0d)]
    public void GivenHMSWHenConvertToHoursItReturnsHoursAsDouble(string hms, double expectedDegrees)
    {
        Utils.HMSToHours(hms).ShouldBe(expectedDegrees);
    }

    [Theory]
    [InlineData(5.392916666666667d, "05:23:34.500")]
    [InlineData(23.903666666666666d, "23:54:13.200")]
    [InlineData(23.999972222222222d, "23:59:59.900")]
    [InlineData(12d, "12:00:00")]
    [InlineData(0d, "00:00:00")]
    public void GivenHoursHenConvertToHMSItReturnsHMSAsString(double hours, string expectedHMS)
    {
        Utils.HoursToHMS(hours).ShouldBe(expectedHMS);
    }

    [Theory]
    [InlineData("05:23:34.5", 80.89375d)]
    [InlineData("23:54:13.2", 358.555d)]
    [InlineData("23:59:59.9", 359.9995833333333d)]
    [InlineData("12:00:00", 180.0d)]
    [InlineData("0:0:0", 0d)]
    public void GivenHMSWHenConvertToDegreesItReturnsDegreesAsDouble(string hms, double expectedDegrees)
    {
        Utils.HMSToDegree(hms).ShouldBe(expectedDegrees);
    }

    [Theory]
    [InlineData("+61:44:24", 61.74d)]
    [InlineData("-12:03:18", -12.055000000000001d)]
    [InlineData("+90:0:0", +90.0d)]
    [InlineData("-89:30:0", -89.5d)]
    [InlineData("-90:0:0", -90.0d)]
    [InlineData("0:0:0", 0.0d)]
    [InlineData("-08:11:11", -8.186388888888889d)]
    public void GivenDMSWHenConvertToDegreesItReturnsDegreesAsDouble(string dms, double expectedDegrees)
    {
        Utils.DMSToDegree(dms).ShouldBe(expectedDegrees);
    }

    [Theory]
    [InlineData(61.74d, "+61:44:24")]
    [InlineData(-12.055000000000001d, "-12:03:18")]
    [InlineData(+90.0d, "+90:00:00")]
    [InlineData(-89.5d, "-89:30:00")]
    [InlineData(-90.0d, "-90:00:00")]
    [InlineData(0.0d, "+00:00:00")]
    [InlineData(-8.186388888888889d, "-08:11:11")]
    [InlineData(-31.314123299999999, "-31:18:50.844")]
    [InlineData(+12.8d, "+12:48:00")]
    public void GivenDegreesWhenConvertToDMSItReturnsDMSAsString(double degrees, string expectedDMS)
    {
        Utils.DegreesToDMS(degrees).ShouldBe(expectedDMS);
    }


    [Theory]
    [InlineData("2MASS J11400198+3152397", "Ð63ATJ,yB", Catalog.TwoMass)]
    [InlineData("2MASS J12015301-1852034", "Ý#fRtuKOL", Catalog.TwoMass)]
    [InlineData("2MASX J00185316+1035410", "ò15|s1VwH", Catalog.TwoMassX)]
    [InlineData("2MASX J11380904-0936257", "ìY<7izouP", Catalog.TwoMassX)]
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
    [InlineData("Cr 399", "Cr399", Catalog.Collinder)]
    [InlineData("C041", "C041", Catalog.Caldwell)]
    [InlineData("C 40", "C040", Catalog.Caldwell)]
    [InlineData("Mel 025", "Mel025", Catalog.Melotte)]
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
    [InlineData("PSR J0002+6216", PSR_J0002_6216n_Enc, Catalog.PSR)]
    [InlineData("PSR J2144-3933", PSR_J2144_3933s_Enc, Catalog.PSR)]
    [InlineData("PSR B0633+17", PSR_B0633_17n_Enc, Catalog.PSR)]
    [InlineData("PSR J2400-9000", "ÁAQAo5{WD", Catalog.PSR)]
    [InlineData("PSR B2400-90", "ÁAQALA*4B", Catalog.PSR)]
    [InlineData("GJ 551", "GJ0551", Catalog.GJ)]
    [InlineData("Sh 2 - 6", "Sh2-006", Catalog.Sharpless)]
    [InlineData("Tres - 3", "TrES03", Catalog.TrES)]
    [InlineData("WASP-11", "WASP011", Catalog.WASP)]
    [InlineData("WDS J02583-4018", "ÁAg4}-8&G", Catalog.WDS)]
    [InlineData("WDS J23599-3112", "ÁA+i),N%G", Catalog.WDS)]
    public void GivenInputWhenCleaningItUpThenCatalogAndAbbreviationAreReturned(string input, string expectedAbbreviation, Catalog expectedCatalog)
    {
        var success = Utils.TryGetCleanedUpCatalogName(input, out var actualCatalogIndex);

        success.ShouldBeTrue();
        actualCatalogIndex.ShouldNotBe((CatalogIndex)0);
        actualCatalogIndex.ToAbbreviation().ShouldBe(expectedAbbreviation);
        actualCatalogIndex.ToCatalog().ShouldBe(expectedCatalog);
        actualCatalogIndex.ShouldBe(EnumHelper.AbbreviationToEnumMember<CatalogIndex>(expectedAbbreviation));
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
    public void GivenAnInvalidInputWhenCleaningUpThenNothingIsReturned(string input)
    {
        var success = Utils.TryGetCleanedUpCatalogName(input, out var actualCatalogIndex);

        success.ShouldBeFalse();
        actualCatalogIndex.ShouldBe((CatalogIndex)0);
    }
}
