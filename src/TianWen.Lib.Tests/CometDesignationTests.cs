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
    // Asteroid-style two-letter half-months + 3-digit order (compact > 8 chars): these overflow a plain
    // ASCII index and only round-trip because of the Base91 bit-packing.
    [InlineData("C/2001 OG108")]
    [InlineData("P/1999 XN120")]
    [InlineData("C/2014 UN271")]
    // No-order provisional, BC year, and a letter+digit fragment (SL9 sub-fragment).
    [InlineData("C/1942 EA")]
    [InlineData("C/-146 P1")]
    [InlineData("D/1993 F2-P1")]
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
    // The space-separated name tail ("10P Tempel 2") must not drag these in with it. A numbered comet
    // is digits-LETTER-space; the first two here are digits-SPACE-letter, so the orbit letter sitting
    // immediately against the number is the whole of what separates them. 3C 273 has the adjacency but
    // 'C' is not an orbit letter (P/D/I only), which is the other half of the guard.
    [InlineData("30 Doradus")]   // the 'D' that would mean a defunct comet is one space too late
    [InlineData("47 Tuc")]
    [InlineData("3C 273")]
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

    [Theory]
    // A designation typed by a human separates the name with a space, and that is what lands in a
    // FITS OBJECT card: N.I.N.A. writes whatever the target was called in the sequence. Requiring the
    // slash meant a frame of 10P/Tempel 2 announced its target in a form its own catalog could not
    // read back, which is the only reason this parser is consulted on header text at all.
    [InlineData("10P Tempel 2", "10P")]
    [InlineData("10P Tempel", "10P")]
    [InlineData("12P Pons-Brooks", "12P")]
    [InlineData("1I Oumuamua", "1I")]
    [InlineData("10P (2026)", "10P")]
    // The slash and bare forms are unaffected, and a fragment still survives a name tail.
    [InlineData("10P/Tempel 2", "10P")]
    [InlineData("10P/Tempel (2026)", "10P")]
    [InlineData("73P-C Schwassmann-Wachmann", "73P-C")]
    [InlineData("10p tempel 2", "10P")]
    public void GivenANumberedCometNamedWithASpaceWhenParsedThenTheDesignationIsRead(string input, string expected)
    {
        CometDesignation.TryParse(input, out var designation).ShouldBeTrue();
        designation.ToCanonical().ShouldBe(expected);
    }

    [Fact]
    public void GivenANameTailWhenProbingNumberedShapeThenOnlyTheSpacedFormIsAccepted()
    {
        // The orbit letter sitting immediately against the number is the ONLY thing separating a named
        // comet from a catalogue object that merely starts with digits, and stripping spaces destroys
        // it. This is why the guesser probes both forms and why the probe must never accept a bare
        // letter tail: "10PTEMPEL" and "30DORADUS" are the same shape, so admitting one admits both.
        CometDesignation.IsNumberedShape("10P Tempel").ShouldBeTrue();
        CometDesignation.IsNumberedShape("30 Doradus").ShouldBeFalse();
        CometDesignation.IsNumberedShape("10PTEMPEL").ShouldBeFalse();
        CometDesignation.IsNumberedShape("30DORADUS").ShouldBeFalse();

        // What the probe was already for is unaffected.
        CometDesignation.IsNumberedShape("13P").ShouldBeTrue();
        CometDesignation.IsNumberedShape("73P-C").ShouldBeTrue();
        CometDesignation.IsNumberedShape("12P/Pons-Brooks").ShouldBeTrue();
    }

    [Theory]
    // The end-to-end consequence: a target typed the way a human types it has to REACH the comet
    // catalog, not merely parse once something else has decided it is a comet. Routing is a separate
    // function from TryParse, so a leniency that stopped at the parser would look fixed and still fail
    // in the search box.
    [InlineData("10P Tempel 2")]
    [InlineData("10P Tempel")]
    [InlineData("12P Pons-Brooks")]
    [InlineData("10P/Tempel 2")]
    [InlineData("13P")]
    public void GivenANamedNumberedCometWhenGuessingCatalogFormatThenItRoutesToTheCometCatalog(string input)
    {
        CatalogUtils.TryGuessCatalogFormat(input, out _, out _, out _, out var catalog).ShouldBeTrue();
        catalog.ShouldBe(Catalog.Comet);
    }

    [Theory]
    // The other side of the same routing change: a digit-leading object whose letter is one space too
    // late must keep its own catalog. 30 Doradus is the case that made this worth a test.
    [InlineData("30 Doradus")]
    [InlineData("47 Tuc")]
    public void GivenADigitLeadingNonCometWhenGuessingCatalogFormatThenItIsNotAComet(string input)
    {
        if (CatalogUtils.TryGuessCatalogFormat(input, out _, out _, out _, out var catalog))
        {
            catalog.ShouldNotBe(Catalog.Comet);
        }
    }
}
