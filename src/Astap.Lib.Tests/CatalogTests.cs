using Astap.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;

namespace Astap.Lib.Tests;

public class CatalogTests
{
    [Theory]
    [InlineData(Catalog.Abell, "ACO")]
    [InlineData(Catalog.Barnard, "Barnard")]
    [InlineData(Catalog.BonnerDurchmusterung, "BD")]
    [InlineData(Catalog.Caldwell, "C")]
    [InlineData(Catalog.Ced, "Ced")]
    [InlineData(Catalog.Collinder, "Cr")]
    [InlineData(Catalog.DG, "DG")]
    [InlineData(Catalog.Dobashi, "Dobashi")]
    [InlineData(Catalog.CG, "CG")]
    [InlineData(Catalog.ESO, "ESO")]
    [InlineData(Catalog.GJ, "GJ")]
    [InlineData(Catalog.GUM, "GUM")]
    [InlineData(Catalog.H, "H")]
    [InlineData(Catalog.HAT_P, "HAT-P")]
    [InlineData(Catalog.HATS, "HATS")]
    [InlineData(Catalog.HD, "HD")]
    [InlineData(Catalog.HH, "HH")]
    [InlineData(Catalog.HIP, "HIP")]
    [InlineData(Catalog.HR, "HR")]
    [InlineData(Catalog.HCG, "HCG")]
    [InlineData(Catalog.IC, "IC")]
    [InlineData(Catalog.LDN, "LDN")]
    [InlineData(Catalog.Melotte, "Mel")]
    [InlineData(Catalog.Messier, "M")]
    [InlineData(Catalog.NGC, "NGC")]
    [InlineData(Catalog.Pl, "Pl")]
    [InlineData(Catalog.PSR, "PSR")]
    [InlineData(Catalog.RCW, "RCW")]
    [InlineData(Catalog.Sharpless2, "Sh2")]
    [InlineData(Catalog.TrES, "TrES")]
    [InlineData(Catalog.UGC, "UGC")]
    [InlineData(Catalog.WASP, "WASP")]
    [InlineData(Catalog.WDS, "WDS")]
    [InlineData(Catalog.XO, "XO")]
    public void GivenCatalogWhenToCanonicalThenItIsReturned(Catalog catalog, string expectedCanon)
    {
        catalog.ToCanonical().ShouldBe(expectedCanon);
    }
}