using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

// CometSearchKeys is the single source of the searchable key spellings shared by the sky-map F3
// search and the planner-tab search. These pin the four accepted forms + the case-insensitive resolve.
public class CometSearchKeysTests
{
    private static CometElements MakeComet(string designation, string? commonName)
        => StubCometRepository.Comet(designation, commonName);

    [Fact]
    public void GivenAPeriodicCometWithCommonNameEnumerateYieldsAllFourKeyForms()
    {
        var repo = new StubCometRepository(MakeComet("10P", "Tempel"));

        var keys = CometSearchKeys.Enumerate(repo).ToList();

        // canonical / common / parenthetical / slash -- all mapping to the same index + display.
        keys.Select(k => k.Key).ShouldBe(["10P", "Tempel", "10P (Tempel)", "10P/Tempel"], ignoreOrder: true);
        keys.Select(k => k.Display).Distinct().ShouldHaveSingleItem().ShouldBe("10P/Tempel");
        keys.Select(k => k.Index).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void GivenACometWithNoCommonNameEnumerateYieldsOnlyTheCanonical()
    {
        var repo = new StubCometRepository(MakeComet("10P", null));

        var keys = CometSearchKeys.Enumerate(repo).ToList();

        keys.ShouldHaveSingleItem();
        keys[0].Key.ShouldBe("10P");
        keys[0].Display.ShouldBe("10P"); // no common name -> DisplayName is the bare canonical
    }

    [Fact]
    public void GivenAProvisionalCometDisplayUsesTheParentheticalForm()
    {
        var repo = new StubCometRepository(MakeComet("C/2026 A1", "PANSTARRS"));

        var keys = CometSearchKeys.Enumerate(repo).ToList();

        // A provisional designation already contains '/', so DisplayName is "C/2026 A1 (PANSTARRS)".
        keys.Select(k => k.Display).Distinct().ShouldHaveSingleItem().ShouldBe("C/2026 A1 (PANSTARRS)");
        keys.Select(k => k.Key).ShouldContain("C/2026 A1 (PANSTARRS)");
        keys.Select(k => k.Key).ShouldContain("PANSTARRS");
    }

    [Theory]
    [InlineData("10P")]
    [InlineData("10p")]            // case-insensitive
    [InlineData("Tempel")]
    [InlineData("tempel")]
    [InlineData("10P/Tempel")]
    [InlineData("  10P/Tempel  ")] // trimmed
    [InlineData("10P (Tempel)")]
    public void TryResolveMatchesEveryKeyFormCaseInsensitively(string query)
    {
        var repo = new StubCometRepository(MakeComet("10P", "Tempel"));

        CometSearchKeys.TryResolve(repo, query, out var index, out var display).ShouldBeTrue();
        display.ShouldBe("10P/Tempel");
        (repo.All[0].CatalogIndex is { } idx && idx == index).ShouldBeTrue();
    }

    [Theory]
    [InlineData("M31")]
    [InlineData("12P")]     // a different comet not in the repo
    [InlineData("")]
    public void TryResolveReturnsFalseForNonComets(string query)
    {
        var repo = new StubCometRepository(MakeComet("10P", "Tempel"));

        CometSearchKeys.TryResolve(repo, query, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolveOnNullRepositoryReturnsFalse()
        => CometSearchKeys.TryResolve(null, "10P", out _, out _).ShouldBeFalse();

    // SBDB's common name is the DISCOVERER, so it is shared by construction: eight comets are called
    // "Tempel" and 1,465 are called "SOHO" (3,563 of 4,069 share a name with something). Every naming
    // surface has to survive that, and the reported symptom was a search for "Tempel" returning a
    // column of a dozen rows all reading "Tempel".
    private static StubCometRepository TempelFamily() => new(
        MakeComet("9P", "Tempel"),
        MakeComet("10P", "Tempel"),
        MakeComet("C/1864 N1", "Tempel"),
        MakeComet("55P", "Tempel-Tuttle"));

    [Fact]
    public void SuggestionsAreOnePerCometAndEachCarriesItsDesignation()
    {
        var suggestions = CometSearchKeys.EnumerateSuggestions(TempelFamily()).ToList();

        // One row per comet (NOT four key forms each), all distinct, all naming which Tempel they are.
        suggestions.Select(s => s.Display).ShouldBe(
            ["9P/Tempel", "10P/Tempel", "C/1864 N1 (Tempel)", "55P/Tempel-Tuttle"], ignoreOrder: true);
        suggestions.Select(s => s.Display).Distinct().Count().ShouldBe(suggestions.Count);
        suggestions.Select(s => s.Index).Distinct().Count().ShouldBe(suggestions.Count);
    }

    [Fact]
    public void EverySuggestionResolvesBackToItsOwnComet()
    {
        // The round-trip is what makes a display name safe to put in a picker: the user selects the row
        // they can see, and the resolve returns THAT comet rather than the first one sharing its name.
        var repo = TempelFamily();

        foreach (var (display, index) in CometSearchKeys.EnumerateSuggestions(repo))
        {
            CometSearchKeys.TryResolve(repo, display, out var resolved, out var resolvedDisplay).ShouldBeTrue();
            resolved.ShouldBe(index);
            resolvedDisplay.ShouldBe(display);
        }
    }

    [Fact]
    public void AnUnambiguousSpellingBeatsABareSharedNameWhereverItSitsInTheSet()
    {
        // The two-pass resolve, and the reason it is two passes: a single pass in catalog order matches
        // whichever spelling comes first, so a comet whose bare common name happens to equal another
        // comet's full display form would answer for it. Here "10P/Tempel" is 10P's display AND could
        // be read as an alias of the earlier 9P if the bare-name pass ran first.
        var repo = new StubCometRepository(
            MakeComet("9P", "10P/Tempel"),   // pathological, but the shape the ordering must survive
            MakeComet("10P", "Tempel"));

        CometSearchKeys.TryResolve(repo, "10P/Tempel", out var index, out var display).ShouldBeTrue();
        display.ShouldBe("10P/Tempel");
        (repo.All[1].CatalogIndex is { } tenP && tenP == index).ShouldBeTrue();
    }

    [Fact]
    public void ABareSharedNameStillResolves_ToTheFirstCometCarryingIt()
    {
        // Genuinely ambiguous input, so first-wins is the only answer available. Recorded rather than
        // "fixed": the fix for the user-facing case is that a picker offers display names, never this.
        var repo = TempelFamily();

        CometSearchKeys.TryResolve(repo, "Tempel", out var index, out var display).ShouldBeTrue();
        display.ShouldBe("9P/Tempel");
        (repo.All[0].CatalogIndex is { } nineP && nineP == index).ShouldBeTrue();
    }

    [Theory]
    [InlineData("10P", "Tempel", true)]           // canonical
    [InlineData("10P/Tempel", "Tempel", true)]    // slash form
    [InlineData("10P (Tempel)", "Tempel", true)]  // parenthetical
    [InlineData("Tempel", "Tempel", false)]       // the bare discoverer, shared by design
    [InlineData("10P", null, true)]               // no common name at all
    public void IsUniqueFormSeparatesTheIdentifyingSpellingsFromTheSharedOne(string key, string? commonName, bool unique)
        => CometSearchKeys.IsUniqueForm(key, commonName).ShouldBe(unique);
}
