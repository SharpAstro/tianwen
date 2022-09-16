using Astap.Lib.Astrometry;
using Shouldly;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class CatalogTests
{
    [Theory]
    [InlineData(Catalog.Abell, "ACO")]
    [InlineData(Catalog.Barnard, "Barnard")]
    [InlineData(Catalog.Caldwell, "C")]
    [InlineData(Catalog.Collinder, "Cr")]
    [InlineData(Catalog.ESO, "ESO")]
    [InlineData(Catalog.GJ, "GJ")]
    [InlineData(Catalog.GUM, "GUM")]
    [InlineData(Catalog.H, "H")]
    [InlineData(Catalog.HAT_P, "HAT-P")]
    [InlineData(Catalog.HATS, "HATS")]
    [InlineData(Catalog.HD, "HD")]
    [InlineData(Catalog.HIP, "HIP")]
    [InlineData(Catalog.HR, "HR")]
    [InlineData(Catalog.HCG, "HCG")]
    [InlineData(Catalog.IC, "IC")]
    [InlineData(Catalog.Melotte, "Mel")]
    [InlineData(Catalog.Messier, "M")]
    [InlineData(Catalog.NGC, "NGC")]
    [InlineData(Catalog.PSR, "PSR")]
    [InlineData(Catalog.Sharpless, "Sh2")]
    [InlineData(Catalog.UGC, "UGC")]
    [InlineData(Catalog.WASP, "WASP")]
    [InlineData(Catalog.XO, "XO")]
    public void GivenCatalogWhenToCanonicalThenItIsReturned(Catalog catalog, string expectedCanon)
    {
        catalog.ToCanonical().ShouldBe(expectedCanon);
    }


    [Theory]
    [InlineData(NGC7293, "N7293", Catalog.NGC)]
    [InlineData(NGC0056, "N0056", Catalog.NGC)]
    [InlineData(IC1000, "I1000", Catalog.IC)]
    [InlineData(IC0715NW, "I0715_NW", Catalog.IC)]
    [InlineData(IC0720_NED02, "I0720N02", Catalog.IC)]
    [InlineData(M040, "M040", Catalog.Messier)]
    [InlineData(M102, "M102", Catalog.Messier)]
    [InlineData(ESO056_115, "E056-115", Catalog.ESO)]
    [InlineData(PSR_J2144_3933s, "PrJAAIABw", Catalog.PSR)]
    public void GivenACatalogIndexValueWhenGettingAbbreviationThenItIsReturned(CatalogIndex catalogIndex, string expectedAbbreviation, Catalog expectedCatalog)
    {
        catalogIndex.ToAbbreviation().ShouldBe(expectedAbbreviation);
        catalogIndex.ToCatalog().ShouldBe(expectedCatalog);
    }
}
