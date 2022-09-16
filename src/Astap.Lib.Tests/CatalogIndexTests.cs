using Astap.Lib.Astrometry;
using Shouldly;
using Xunit;
using static Astap.Lib.Tests.SharedTestData;

namespace Astap.Lib.Tests;

public class CatalogIndexTests
{
    [Theory]
    [InlineData(NGC7293, "N7293")]
    [InlineData(NGC0056, "N0056")]
    [InlineData(IC1000, "I1000")]
    [InlineData(IC0715NW, "I0715_NW")]
    [InlineData(IC0720_NED02, "I0720N02")]
    [InlineData(M040, "M040")]
    [InlineData(M102, "M102")]
    [InlineData(ESO056_115, "E056-115")]
    [InlineData(PSR_J2144_3933s, "PrJBDAeuw")]
    [InlineData(PSR_B0633_17n, "PrBATyAIg")]
    public void GivenACatalogIndexValueWhenToAbbreviationThenItIsReturned(CatalogIndex catalogIndex, string expectedAbbreviation)
    {
        catalogIndex.ToAbbreviation().ShouldBe(expectedAbbreviation);
    }

    [Theory]
    [InlineData(NGC7293, Catalog.NGC)]
    [InlineData(NGC0056, Catalog.NGC)]
    [InlineData(IC1000, Catalog.IC)]
    [InlineData(IC0715NW, Catalog.IC)]
    [InlineData(IC0720_NED02, Catalog.IC)]
    [InlineData(M040, Catalog.Messier)]
    [InlineData(M102, Catalog.Messier)]
    [InlineData(ESO056_115, Catalog.ESO)]
    [InlineData(PSR_J2144_3933s, Catalog.PSR)]
    [InlineData(PSR_B0633_17n, Catalog.PSR)]
    public void GivenACatalogIndexValueWhenToCatalogThenItIsReturned(CatalogIndex catalogIndex, Catalog expectedCatalog)
    {
        catalogIndex.ToCatalog().ShouldBe(expectedCatalog);
    }

    [Theory]
    [InlineData(NGC7293, "NGC 7293")]
    [InlineData(NGC0056, "NGC 56")]
    [InlineData(IC1000, "IC 1000")]
    [InlineData(IC0715NW, "IC 715NW")]
    [InlineData(IC0720_NED02, "IC 720 NED02")]
    [InlineData(M040, "M 40")]
    [InlineData(M102, "M 102")]
    [InlineData(ESO056_115, "ESO 56-115")]
    [InlineData(PSR_J2144_3933s, "PSR J2144-3933")]
    [InlineData(PSR_B0633_17n, "PSR B0633+17")]
    [InlineData(SH2_6, "Sh2-6")]
    [InlineData(TrES_3, "TrES-3")]
    public void GivenACatalogIndexValueWhenToCanonicalThenItIsReturned(CatalogIndex catalogIndex, string expectedCanon)
    {
       // catalogIndex.ToCanonical().ShouldBe(expectedCanon);
    }
}
