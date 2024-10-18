using TianWen.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;
using static TianWen.Lib.Tests.SharedTestData;

namespace TianWen.Lib.Tests;

public class CatalogUtilTests
{
    [Theory]
    [InlineData("2MASS J11400198+3152397", "\u00D063ATJ,yB", Catalog.TwoMass)]
    [InlineData("2MASS J12015301-1852034", "\u00DD#fRtuKOL", Catalog.TwoMass)]
    [InlineData("2MASX J00185316+1035410", "\u00F215|s1VwH", Catalog.TwoMassX)]
    [InlineData("2MASX J11380904-0936257", "\u00ECY<7izouP", Catalog.TwoMassX)]
    [InlineData("BD-16 1591", BD_16_1591s_Enc, Catalog.BonnerDurchmusterung)]
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
    [InlineData("M51a", "M51a", Catalog.Messier)]
    [InlineData("IC4473 NED01", "I4473N01", Catalog.IC)]
    [InlineData("ESO 56-115", "E056-115", Catalog.ESO)]
    [InlineData("ESO351-030", "E351-030", Catalog.ESO)]
    [InlineData("ESO356 - 004", "E356-004", Catalog.ESO)]
    [InlineData("Cl 399", "Cr399", Catalog.Collinder)]
    [InlineData("Cr 399", "Cr399", Catalog.Collinder)]
    [InlineData("Ced 176e", "Ced176e", Catalog.Ced)]
    [InlineData("C041", "C041", Catalog.Caldwell)]
    [InlineData("C 40", "C040", Catalog.Caldwell)]
    [InlineData("C 069", "C069", Catalog.Caldwell)]
    [InlineData("CG 4", "CG0004", Catalog.CG)]
    [InlineData("CG 22B1", "CG22B1", Catalog.CG)]
    [InlineData("CG 587", "CG0587", Catalog.CG)]
    [InlineData("DG 11", "DG0011", Catalog.DG)]
    [InlineData("LDN 1002A", "LDN1002A", Catalog.LDN)]
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
    [InlineData("Pl-Sol", "Pl-Sol", Catalog.Pl)]
    [InlineData("Pl-E", "Pl-E", Catalog.Pl)]
    [InlineData("Pl-E I", "Pl-EI", Catalog.Pl)]
    [InlineData("Pl-MaI", "Pl-MaI", Catalog.Pl)]
    [InlineData("Pl-EIB", "Pl-EIB", Catalog.Pl)]
    [InlineData("Pl-S IV", "Pl-SIV", Catalog.Pl)]
    [InlineData("PSR J0002+6216", PSR_J0002_6216n_Enc, Catalog.PSR)]
    [InlineData("PSR J2144-3933", PSR_J2144_3933s_Enc, Catalog.PSR)]
    [InlineData("PSR B0633+17", PSR_B0633_17n_Enc, Catalog.PSR)]
    [InlineData("PSR J2400-9000", "\u00C1AQAo5{WD", Catalog.PSR)]
    [InlineData("PSR B2400-90", "\u00C1AQALA*4B", Catalog.PSR)]
    [InlineData("GJ 551", "GJ0551", Catalog.GJ)]
    [InlineData("Sh 2 - 6", "Sh2-0006", Catalog.Sharpless)]
    [InlineData("Tres - 3", "TrES03", Catalog.TrES)]      
    [InlineData("TYC 9537-12121-3", "\u00C1A*ZVhb-H", Catalog.Tycho2)]
    [InlineData("WASP-11", "WASP011", Catalog.WASP)]
    [InlineData("WDS J02583-4018", "\u00C1Ag4}-8&G", Catalog.WDS)]
    [InlineData("WDS J23599-3112", "\u00C1A+i),N%G", Catalog.WDS)]
    public void GivenInputWhenCleaningItUpThenCatalogAndAbbreviationAreReturned(string input, string expectedAbbreviation, Catalog expectedCatalog)
    {
        var success = CatalogUtils.TryGetCleanedUpCatalogName(input, out var actualCatalogIndex);

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
    [InlineData("MM  3")]
    [InlineData("Gaia DR2 5975481144442221696")]
    public void GivenAnInvalidInputWhenCleaningUpThenNothingIsReturned(string? input)
    {
        var success = CatalogUtils.TryGetCleanedUpCatalogName(input, out var actualCatalogIndex);

        success.ShouldBeFalse();
        actualCatalogIndex.ShouldBe((CatalogIndex)0);
    }
}
