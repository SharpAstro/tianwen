using TianWen.Lib.Astrometry.Catalogs;
using Shouldly;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Catalog")]
public class CometDesignationTests
{
    [Theory]
    // Provisional designations: type-letter / year half-month order, optional fragment.
    [InlineData("C/2024 A1", "C/2024 A1", "C2024A1")]
    [InlineData("C/2023 A3", "C/2023 A3", "C2023A3")]
    [InlineData("P/2023 X1", "P/2023 X1", "P2023X1")]
    [InlineData("C/2019 Y4-D", "C/2019 Y4-D", "C2019Y4D")]
    [InlineData("C/1995 O1", "C/1995 O1", "C1995O1")]
    // Numbered periodic / interstellar, optional /Name tail dropped.
    [InlineData("13P", "13P", "13P")]
    [InlineData("12P/Pons-Brooks", "12P", "12P")]
    [InlineData("73P-C", "73P-C", "73PC")]
    [InlineData("1I/'Oumuamua", "1I", "1I")]
    // Space-stripped and compact inputs parse to the same thing.
    [InlineData("C/2024A1", "C/2024 A1", "C2024A1")]
    [InlineData("C2024A1", "C/2024 A1", "C2024A1")]
    [InlineData("73PC", "73P-C", "73PC")]
    // Lower-case tolerated.
    [InlineData("c/2024 a1", "C/2024 A1", "C2024A1")]
    public void GivenACometDesignationWhenParsedThenCanonicalAndCompactRoundTrip(string input, string expectedCanonical, string expectedCompact)
    {
        CometDesignation.TryParse(input, out var designation).ShouldBeTrue();
        designation.ToCanonical().ShouldBe(expectedCanonical);
        designation.ToCompact().ShouldBe(expectedCompact);
    }

    [Theory]
    [InlineData("C/2024 A1")]
    [InlineData("C/2023 A3")]
    [InlineData("C/2019 Y4-D")]
    [InlineData("13P")]
    [InlineData("73P-C")]
    [InlineData("1I")]
    [InlineData("P/2023 X1")]
    public void GivenACometDesignationWhenPackedToCatalogIndexThenItRoundTripsBackToCanonical(string canonical)
    {
        CometDesignation.TryParse(canonical, out var designation).ShouldBeTrue();
        designation.TryToCatalogIndex(out var catalogIndex).ShouldBeTrue();

        // The packed value is a Catalog.Comet index and expands back to the same canonical string.
        catalogIndex.ToCatalog().ShouldBe(Catalog.Comet);
        catalogIndex.IsSolarSystemObject.ShouldBeTrue();
        catalogIndex.ToCanonical().ShouldBe(canonical);
    }

    [Theory]
    // The free-text catalog-name cleanup (the F3 search / autocomplete path) must produce the SAME
    // packed CatalogIndex as CometDesignation.TryToCatalogIndex -- never a divergent value like the
    // historical Pl-Sol free-text-vs-literal mismatch.
    [InlineData("C/2024 A1")]
    [InlineData("C/2024A1")]
    [InlineData("13P")]
    [InlineData("12P/Pons-Brooks")]
    [InlineData("73P-C")]
    public void GivenCometInputWhenCleanedUpThenItMatchesTheDesignationPackedIndex(string input)
    {
        CatalogUtils.TryGetCleanedUpCatalogName(input, out var viaCleanup).ShouldBeTrue();

        CometDesignation.TryParse(input, out var designation).ShouldBeTrue();
        designation.TryToCatalogIndex(out var viaDesignation).ShouldBeTrue();

        viaCleanup.ShouldBe(viaDesignation);
    }

    [Theory]
    [InlineData("NGC 7293")]
    [InlineData("M42")]
    [InlineData("Caldwell 41")]  // 'C' + digit must stay Caldwell, not a comet
    [InlineData("HR 1142")]
    [InlineData("not a comet")]
    public void GivenNonCometInputWhenParsedAsCometThenItIsRejected(string input)
    {
        CometDesignation.TryParse(input, out _).ShouldBeFalse();
    }

    [Fact]
    public void GivenCaldwellInputWhenGuessingCatalogFormatThenItIsNotAComet()
    {
        // "C41" is Caldwell 41 -- the comet arm must not swallow a bare 'C' + digits (no slash).
        CatalogUtils.TryGuessCatalogFormat("C41", out _, out _, out _, out var catalog).ShouldBeTrue();
        catalog.ShouldBe(Catalog.Caldwell);
    }
}
