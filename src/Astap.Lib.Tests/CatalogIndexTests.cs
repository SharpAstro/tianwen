using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class CatalogIndexTests
{
    [Theory]
    [InlineData(CatalogIndex.BD_16_1591s, BD_16_1591s_Enc)]
    [InlineData(CatalogIndex.CG0004, "CG0004")]
    [InlineData(CatalogIndex.CG22B1, "CG22B1")]
    [InlineData(CatalogIndex.DOBASHI_0222, "Do00222")]
    [InlineData(CatalogIndex.NGC7293, "N7293")]
    [InlineData(CatalogIndex.NGC0056, "N0056")]
    [InlineData(CatalogIndex.NGC0526_B, "N0526_B")]
    [InlineData(CatalogIndex.IC1000, "I1000")]
    [InlineData(CatalogIndex.IC0715NW, "I0715_NW")]
    [InlineData(CatalogIndex.IC0720_NED02, "I0720N02")]
    [InlineData(CatalogIndex.M040, "M040")]
    [InlineData(CatalogIndex.M102, "M102")]
    [InlineData(CatalogIndex.ESO056_115, "E056-115")]
    [InlineData(CatalogIndex.PSR_J0002_6216n, PSR_J0002_6216n_Enc)]
    [InlineData(CatalogIndex.PSR_J2144_3933s, PSR_J2144_3933s_Enc)]
    [InlineData(CatalogIndex.PSR_B0633_17n, PSR_B0633_17n_Enc)]
    [InlineData(CatalogIndex.Sh2_0006, "Sh2-0006")]
    [InlineData(CatalogIndex.Sh2_0155, "Sh2-0155")]
    [InlineData(CatalogIndex.TrES03, "TrES03")]
    [InlineData(CatalogIndex.vdB0005, "vdB0005")]
    [InlineData(CatalogIndex.WDS_02583_4018s, "\u00C1Ag4}-8&G")]
    [InlineData(CatalogIndex.WDS_23599_3112s, "\u00C1A+i),N%G")]
    public void GivenACatalogIndexValueWhenToAbbreviationThenItIsReturned(CatalogIndex catalogIndex, string expectedAbbreviation)
    {
        catalogIndex.ToAbbreviation().ShouldBe(expectedAbbreviation);
    }

    [Theory]
    [InlineData(CatalogIndex.BD_16_1591s, Catalog.BonnerDurchmusterung)]
    [InlineData(CatalogIndex.CG0004, Catalog.CG)]
    [InlineData(CatalogIndex.CG22B1, Catalog.CG)]
    [InlineData(CatalogIndex.DOBASHI_0222, Catalog.Dobashi)]
    [InlineData(CatalogIndex.HR1142, Catalog.HR)]
    [InlineData(CatalogIndex.NGC7293, Catalog.NGC)]
    [InlineData(CatalogIndex.NGC0056, Catalog.NGC)]
    [InlineData(CatalogIndex.NGC0526_B, Catalog.NGC)]
    [InlineData(CatalogIndex.IC1000, Catalog.IC)]
    [InlineData(CatalogIndex.IC0715NW, Catalog.IC)]
    [InlineData(CatalogIndex.IC0720_NED02, Catalog.IC)]
    [InlineData(CatalogIndex.M040, Catalog.Messier)]
    [InlineData(CatalogIndex.M102, Catalog.Messier)]
    [InlineData(CatalogIndex.ESO056_115, Catalog.ESO)]
    [InlineData(CatalogIndex.PSR_J2144_3933s, Catalog.PSR)]
    [InlineData(CatalogIndex.PSR_B0633_17n, Catalog.PSR)]
    [InlineData(CatalogIndex.Sh2_0006, Catalog.Sharpless)]
    [InlineData(CatalogIndex.TrES03, Catalog.TrES)]
    [InlineData(CatalogIndex.WDS_23599_3112s, Catalog.WDS)]
    public void GivenACatalogIndexValueWhenToCatalogThenItIsReturned(CatalogIndex catalogIndex, Catalog expectedCatalog)
    {
        catalogIndex.ToCatalog().ShouldBe(expectedCatalog);
    }

    [Theory]
    [InlineData(CatalogIndex.Barnard_22, "Barnard 22")]
    [InlineData(CatalogIndex.BD_16_1591s, "BD-16 1591")]
    [InlineData(CatalogIndex.C041, "C41")]
    [InlineData(CatalogIndex.Cr399, "Cr 399")]
    [InlineData(CatalogIndex.DOBASHI_0222, "Dobashi 222")]
    [InlineData(CatalogIndex.NGC7293, "NGC 7293")]
    [InlineData(CatalogIndex.NGC0056, "NGC 56")]
    [InlineData(CatalogIndex.HR1084, "HR 1084")]
    [InlineData(CatalogIndex.IC1000, "IC 1000")]
    [InlineData(CatalogIndex.IC0715NW, "IC 715NW")]
    [InlineData(CatalogIndex.IC0720_NED02, "IC 720 NED02")]
    [InlineData(CatalogIndex.M040, "M40")]
    [InlineData(CatalogIndex.M102, "M102")]
    [InlineData(CatalogIndex.CG0004, "CG 4")]
    [InlineData(CatalogIndex.CG22B1, "CG 22B1")]
    [InlineData(CatalogIndex.NGC0526_B, "NGC 526B")]
    [InlineData(CatalogIndex.ESO056_115, "ESO 56-115")]
    [InlineData(CatalogIndex.PSR_J0002_6216n, "PSR J0002+6216")]
    [InlineData(CatalogIndex.PSR_J2144_3933s, "PSR J2144-3933")]
    [InlineData(CatalogIndex.PSR_B0633_17n, "PSR B0633+17")]
    [InlineData(CatalogIndex.TwoM_J11400198_3152397n, "2MASS J11400198+3152397")]
    [InlineData(CatalogIndex.TwoM_J12015301_1852034s, "2MASS J12015301-1852034")]
    [InlineData(CatalogIndex.TwoMX_J00185316_1035410n, "2MASX J00185316+1035410")]
    [InlineData(CatalogIndex.TwoMX_J11380904_0936257s, "2MASX J11380904-0936257")]
    [InlineData(CatalogIndex.Sh2_0006, "Sh2-6")]
    [InlineData(CatalogIndex.TrES03, "TrES-3")]
    [InlineData(CatalogIndex.vdB0005, "vdB 5")]
    [InlineData(CatalogIndex.WDS_23599_3112s, "WDS J23599-3112")]
    [InlineData(CatalogIndex.XO0003, "XO-3")]
    [InlineData(CatalogIndex.XO002N, "XO-2N")]
    public void GivenACatalogIndexValueWhenToCanonicalThenItIsReturnedInNormalForm(CatalogIndex catalogIndex, string expectedCanon)
    {
       catalogIndex.ToCanonical().ShouldBe(expectedCanon);
    }

    [Theory]
    [InlineData(CatalogIndex.Barnard_22, "B22")]
    [InlineData(CatalogIndex.BD_16_1591s, "BD-16 1591")]
    [InlineData(CatalogIndex.C041, "Caldwell 41")]
    [InlineData(CatalogIndex.Cr399, "Collinder 399")]
    [InlineData(CatalogIndex.DOBASHI_0222, "Dobashi 222")]
    [InlineData(CatalogIndex.NGC7293, "NGC 7293")]
    [InlineData(CatalogIndex.NGC0056, "NGC 56")]
    [InlineData(CatalogIndex.HR1084, "HR 1084")]
    [InlineData(CatalogIndex.IC1000, "IC 1000")]
    [InlineData(CatalogIndex.IC0715NW, "IC 715NW")]
    [InlineData(CatalogIndex.IC0720_NED02, "IC 720 NED02")]
    [InlineData(CatalogIndex.M040, "Messier 40")]
    [InlineData(CatalogIndex.M102, "Messier 102")]
    [InlineData(CatalogIndex.CG0004, "CG 4")]
    [InlineData(CatalogIndex.CG22B1, "CG 22B1")]
    [InlineData(CatalogIndex.NGC0526_B, "NGC 526B")]
    [InlineData(CatalogIndex.ESO056_115, "ESO 56-115")]
    [InlineData(CatalogIndex.PSR_J0002_6216n, "PSR J0002+6216")]
    [InlineData(CatalogIndex.PSR_J2144_3933s, "PSR J2144-3933")]
    [InlineData(CatalogIndex.PSR_B0633_17n, "PSR B0633+17")]
    [InlineData(CatalogIndex.TwoM_J11400198_3152397n, "2MASS J11400198+3152397")]
    [InlineData(CatalogIndex.TwoM_J12015301_1852034s, "2MASS J12015301-1852034")]
    [InlineData(CatalogIndex.TwoMX_J00185316_1035410n, "2MASX J00185316+1035410")]
    [InlineData(CatalogIndex.TwoMX_J11380904_0936257s, "2MASX J11380904-0936257")]
    [InlineData(CatalogIndex.Sh2_0006, "Sharpless 6")]
    [InlineData(CatalogIndex.TrES03, "TrES-3")]
    [InlineData(CatalogIndex.vdB0005, "vdB 5")]
    [InlineData(CatalogIndex.WDS_23599_3112s, "WDS J23599-3112")]
    [InlineData(CatalogIndex.XO0003, "XO-3")]
    [InlineData(CatalogIndex.XO002N, "XO-2N")]
    public void GivenACatalogIndexValueWhenToCanonicalAlternativeFormatThenItIsReturnedInAlternativeForm(CatalogIndex catalogIndex, string expectedCanon)
    {
        catalogIndex.ToCanonical(CanonicalFormat.Alternative).ShouldBe(expectedCanon);
    }

    [Theory]
    [InlineData(Catalog.Messier, 42, CatalogIndex.M042)]
    [InlineData(Catalog.HIP, 120404, CatalogIndex.HIP120404)]
    public void GivenACatalogAndNumericalValueWhenConvertingToCatalogIndexThenTheyAreIdentical(Catalog catalog, int value, CatalogIndex expectedCatalogIndex)
    {
        var num = catalog.GetNumericalIndexSize();
        num.ShouldBeGreaterThan(0);

        var actual = EnumHelper.PrefixedNumericToASCIIPackedInt<CatalogIndex>((ulong)catalog, value, num);
        actual.ShouldBe(expectedCatalogIndex);
    }
}
