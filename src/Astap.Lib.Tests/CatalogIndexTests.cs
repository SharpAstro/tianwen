using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class CatalogIndexTests
{
    [Theory]
    [InlineData(BD_16_1591s, BD_16_1591s_Enc)]
    [InlineData(CG0004, "CG0004")]
    [InlineData(CG22B1, "CG22B1")]
    [InlineData(DOBASHI_0222, "Do00222")]
    [InlineData(NGC7293, "N7293")]
    [InlineData(NGC0056, "N0056")]
    [InlineData(NGC0526_B, "N0526_B")]
    [InlineData(IC1000, "I1000")]
    [InlineData(IC0715NW, "I0715_NW")]
    [InlineData(IC0720_NED02, "I0720N02")]
    [InlineData(M040, "M040")]
    [InlineData(M102, "M102")]
    [InlineData(ESO056_115, "E056-115")]
    [InlineData(PSR_J0002_6216n, PSR_J0002_6216n_Enc)]
    [InlineData(PSR_J2144_3933s, PSR_J2144_3933s_Enc)]
    [InlineData(PSR_B0633_17n, PSR_B0633_17n_Enc)]
    [InlineData(Sh2_0006, "Sh2-0006")]
    [InlineData(Sh2_0155, "Sh2-0155")]
    [InlineData(TrES03, "TrES03")]
    [InlineData(vdB0005, "vdB0005")]
    [InlineData(WDS_02583_4018s, "ÁAg4}-8&G")]
    [InlineData(WDS_23599_3112s, "ÁA+i),N%G")]
    public void GivenACatalogIndexValueWhenToAbbreviationThenItIsReturned(CatalogIndex catalogIndex, string expectedAbbreviation)
    {
        catalogIndex.ToAbbreviation().ShouldBe(expectedAbbreviation);
    }

    [Theory]
    [InlineData(BD_16_1591s, Catalog.BonnerDurchmusterung)]
    [InlineData(CG0004, Catalog.CG)]
    [InlineData(CG22B1, Catalog.CG)]
    [InlineData(DOBASHI_0222, Catalog.Dobashi)]
    [InlineData(HR1142, Catalog.HR)]
    [InlineData(NGC7293, Catalog.NGC)]
    [InlineData(NGC0056, Catalog.NGC)]
    [InlineData(NGC0526_B, Catalog.NGC)]
    [InlineData(IC1000, Catalog.IC)]
    [InlineData(IC0715NW, Catalog.IC)]
    [InlineData(IC0720_NED02, Catalog.IC)]
    [InlineData(M040, Catalog.Messier)]
    [InlineData(M102, Catalog.Messier)]
    [InlineData(ESO056_115, Catalog.ESO)]
    [InlineData(PSR_J2144_3933s, Catalog.PSR)]
    [InlineData(PSR_B0633_17n, Catalog.PSR)]
    [InlineData(Sh2_0006, Catalog.Sharpless)]
    [InlineData(TrES03, Catalog.TrES)]
    [InlineData(WDS_23599_3112s, Catalog.WDS)]
    public void GivenACatalogIndexValueWhenToCatalogThenItIsReturned(CatalogIndex catalogIndex, Catalog expectedCatalog)
    {
        catalogIndex.ToCatalog().ShouldBe(expectedCatalog);
    }

    [Theory]
    [InlineData(BD_16_1591s, "BD-16 1591")]
    [InlineData(C041, "C41")]
    [InlineData(Cr399, "Cr 399")]
    [InlineData(DOBASHI_0222, "Dobashi 222")]
    [InlineData(NGC7293, "NGC 7293")]
    [InlineData(NGC0056, "NGC 56")]
    [InlineData(HR1084, "HR 1084")]
    [InlineData(IC1000, "IC 1000")]
    [InlineData(IC0715NW, "IC 715NW")]
    [InlineData(IC0720_NED02, "IC 720 NED02")]
    [InlineData(M040, "M40")]
    [InlineData(M102, "M102")]
    [InlineData(CG0004, "CG 4")]
    [InlineData(CG22B1, "CG 22B1")]
    [InlineData(NGC0526_B, "NGC 526B")]
    [InlineData(ESO056_115, "ESO 56-115")]
    [InlineData(PSR_J0002_6216n, "PSR J0002+6216")]
    [InlineData(PSR_J2144_3933s, "PSR J2144-3933")]
    [InlineData(PSR_B0633_17n, "PSR B0633+17")]
    [InlineData(TwoM_J11400198_3152397n, "2MASS J11400198+3152397")]
    [InlineData(TwoM_J12015301_1852034s, "2MASS J12015301-1852034")]
    [InlineData(TwoMX_J00185316_1035410n, "2MASX J00185316+1035410")]
    [InlineData(TwoMX_J11380904_0936257s, "2MASX J11380904-0936257")]
    [InlineData(Sh2_0006, "Sh2-6")]
    [InlineData(TrES03, "TrES-3")]
    [InlineData(vdB0005, "vdB 5")]
    [InlineData(WDS_23599_3112s, "WDS J23599-3112")]
    [InlineData(XO0003, "XO-3")]
    [InlineData(XO002N, "XO-2N")]
    public void GivenACatalogIndexValueWhenToCanonicalThenItIsReturnedInNormalForm(CatalogIndex catalogIndex, string expectedCanon)
    {
       catalogIndex.ToCanonical().ShouldBe(expectedCanon);
    }

    [Theory]
    [InlineData(BD_16_1591s, "BD-16 1591")]
    [InlineData(C041, "Caldwell 41")]
    [InlineData(Cr399, "Collinder 399")]
    [InlineData(DOBASHI_0222, "Dobashi 222")]
    [InlineData(NGC7293, "NGC 7293")]
    [InlineData(NGC0056, "NGC 56")]
    [InlineData(HR1084, "HR 1084")]
    [InlineData(IC1000, "IC 1000")]
    [InlineData(IC0715NW, "IC 715NW")]
    [InlineData(IC0720_NED02, "IC 720 NED02")]
    [InlineData(M040, "Messier 40")]
    [InlineData(M102, "Messier 102")]
    [InlineData(CG0004, "CG 4")]
    [InlineData(CG22B1, "CG 22B1")]
    [InlineData(NGC0526_B, "NGC 526B")]
    [InlineData(ESO056_115, "ESO 56-115")]
    [InlineData(PSR_J0002_6216n, "PSR J0002+6216")]
    [InlineData(PSR_J2144_3933s, "PSR J2144-3933")]
    [InlineData(PSR_B0633_17n, "PSR B0633+17")]
    [InlineData(TwoM_J11400198_3152397n, "2MASS J11400198+3152397")]
    [InlineData(TwoM_J12015301_1852034s, "2MASS J12015301-1852034")]
    [InlineData(TwoMX_J00185316_1035410n, "2MASX J00185316+1035410")]
    [InlineData(TwoMX_J11380904_0936257s, "2MASX J11380904-0936257")]
    [InlineData(Sh2_0006, "Sharpless 6")]
    [InlineData(TrES03, "TrES-3")]
    [InlineData(vdB0005, "vdB 5")]
    [InlineData(WDS_23599_3112s, "WDS J23599-3112")]
    [InlineData(XO0003, "XO-3")]
    [InlineData(XO002N, "XO-2N")]
    public void GivenACatalogIndexValueWhenToCanonicalLongFormatThenItIsReturnedInLongForm(CatalogIndex catalogIndex, string expectedCanon)
    {
        catalogIndex.ToCanonical(CanonicalFormat.Long).ShouldBe(expectedCanon);
    }
}
