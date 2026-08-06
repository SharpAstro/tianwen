using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The "?object=" deep link, end to end through the real catalog.
///
/// <para>This exists because the reported failure was silent in the worst way: the address bar showed
/// <c>?view=sky&amp;object=M42</c>, the Sky Atlas opened, and the view simply sat at its default
/// pointing with no selection and no error. A resolve that answers false has nothing to report and
/// nobody to report it to, so only a test that asserts the object was actually SELECTED can tell the
/// difference between "resolved" and "quietly did nothing".</para>
///
/// <para>The web host's own ordering bug (it retried the resolve before <c>InitDBAsync</c> had run, so
/// the catalog was still empty) is fixed in <c>Planner.razor</c>. These pin the other half: that the
/// spellings a human actually types into a URL do resolve once the catalog IS up.</para>
/// </summary>
public class SkyMapDeepLinkTests
{
    private static readonly DateTimeOffset ViewingUtc = new(2026, 8, 6, 14, 0, 0, TimeSpan.Zero);
    private const double SiteLat = -37.8769, SiteLon = 145.1774;

    private static async Task<ICelestialObjectDB> LoadedDbAsync()
    {
        var db = new CelestialObjectDB();
        await db.InitDBAsync();
        return db;
    }

    private static bool Select(ICelestialObjectDB db, SkyMapState state, string token)
        => SkyMapSearchActions.TrySelectByToken(
            state.Search, state, db, token,
            SiteLat, SiteLon, ViewingUtc,
            SiteContext.Create(SiteLat, SiteLon, ViewingUtc));

    [Theory]
    [InlineData("M42")]     // exactly what a person types, and what the report used
    [InlineData("M 42")]    // the catalog's own spacing
    [InlineData("m42")]     // address bars are not case-preserving in practice
    [InlineData("NGC1976")] // the same object under its other catalogue
    [InlineData("NGC 1976")]
    public async Task AMessierDeepLinkSelectsTheObjectAndOpensTheInfoPanel(string token)
    {
        var db = await LoadedDbAsync();
        var state = new SkyMapState { Mode = SkyMapMode.Equatorial };

        Select(db, state, token).ShouldBeTrue($"'{token}' is a spelling a deep link will really carry");

        var panel = state.Search.InfoPanel.ShouldNotBeNull("the deep link must OPEN the detail view, not just resolve");
        panel.Index.ShouldNotBeNull();

        // M42 is at roughly RA 5h35m, Dec -5.4 deg. Assert the view actually went there -- resolving
        // without centring is the same failure from the user's side.
        state.CenterRA.ShouldBe(5.588, 0.05);
        state.CenterDec.ShouldBe(-5.39, 0.1);
    }

    [Fact]
    public async Task AnUnknownTokenIsRejectedAndChangesNothing()
    {
        // A typo must leave the view alone rather than centring on some near-miss.
        var db = await LoadedDbAsync();
        var state = new SkyMapState { Mode = SkyMapMode.Equatorial, CenterRA = 12.0, CenterDec = 30.0 };

        Select(db, state, "M42x").ShouldBeFalse();

        state.Search.InfoPanel.ShouldBeNull();
        state.CenterRA.ShouldBe(12.0);
        state.CenterDec.ShouldBe(30.0);
    }

    [Fact]
    public async Task ACometDeepLinkResolvesFromEitherSpelling()
    {
        // The URL now carries the comet's display name ("10P/Tempel") rather than the bare designation,
        // because "10P" alone says nothing about which comet it is. Both must still resolve, or a
        // shared link from an older build breaks.
        var db = await LoadedDbAsync();
        var tenP = StubCometRepository.Comet("10P", "Tempel");
        var comets = new StubCometRepository(tenP);
        comets.Positions[tenP.CatalogIndex.ShouldNotBeNull()] = (21.514, -27.47, 12.75);

        foreach (var token in new[] { "10P/Tempel", "10P" })
        {
            var state = new SkyMapState { Mode = SkyMapMode.Equatorial };
            SkyMapSearchActions.TrySelectByToken(
                state.Search, state, db, token,
                SiteLat, SiteLon, ViewingUtc,
                SiteContext.Create(SiteLat, SiteLon, ViewingUtc), comets).ShouldBeTrue($"'{token}' must resolve");

            var panel = state.Search.InfoPanel.ShouldNotBeNull();
            panel.Name.ShouldBe("10P/Tempel", "the panel always shows the full form, whichever spelling arrived");
        }
    }
}
