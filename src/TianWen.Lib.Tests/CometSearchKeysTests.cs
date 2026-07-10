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
    {
        CometDesignation.TryParse(designation, out var d).ShouldBeTrue();
        // Only the designation + common name matter here; orbital elements are placeholders.
        return new CometElements(d, commonName, 0.9, 0.9, 60.0, 100.0, 100.0, 2460000.0, 2460000.0, 8.0, 10.0);
    }

    [Fact]
    public void GivenAPeriodicCometWithCommonNameEnumerateYieldsAllFourKeyForms()
    {
        var repo = new StubRepo(MakeComet("10P", "Tempel"));

        var keys = CometSearchKeys.Enumerate(repo).ToList();

        // canonical / common / parenthetical / slash -- all mapping to the same index + display.
        keys.Select(k => k.Key).ShouldBe(["10P", "Tempel", "10P (Tempel)", "10P/Tempel"], ignoreOrder: true);
        keys.Select(k => k.Display).Distinct().ShouldHaveSingleItem().ShouldBe("10P/Tempel");
        keys.Select(k => k.Index).Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void GivenACometWithNoCommonNameEnumerateYieldsOnlyTheCanonical()
    {
        var repo = new StubRepo(MakeComet("10P", null));

        var keys = CometSearchKeys.Enumerate(repo).ToList();

        keys.ShouldHaveSingleItem();
        keys[0].Key.ShouldBe("10P");
        keys[0].Display.ShouldBe("10P"); // no common name -> DisplayName is the bare canonical
    }

    [Fact]
    public void GivenAProvisionalCometDisplayUsesTheParentheticalForm()
    {
        var repo = new StubRepo(MakeComet("C/2026 A1", "PANSTARRS"));

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
        var repo = new StubRepo(MakeComet("10P", "Tempel"));

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
        var repo = new StubRepo(MakeComet("10P", "Tempel"));

        CometSearchKeys.TryResolve(repo, query, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryResolveOnNullRepositoryReturnsFalse()
        => CometSearchKeys.TryResolve(null, "10P", out _, out _).ShouldBeFalse();

    private sealed class StubRepo(params CometElements[] comets) : ICometRepository
    {
        public ImmutableArray<CometElements> All => [.. comets];

        public bool TryGet(CatalogIndex index, out CometElements elements)
        {
            foreach (var c in comets)
            {
                if (c.CatalogIndex is { } idx && idx == index) { elements = c; return true; }
            }
            elements = default;
            return false;
        }

        public bool TryGetPosition(CatalogIndex index, DateTimeOffset time, out double raJ2000Hours, out double decJ2000Deg, out double magnitude)
        {
            raJ2000Hours = decJ2000Deg = magnitude = double.NaN;
            return false;
        }

        public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RefreshAsync(bool forceRefetch = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
