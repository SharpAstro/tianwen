using System;
using System.Threading.Tasks;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The atlas's planets as A4 and finding 2 of docs/plans/atlas-planet-detail.md ask for them: one cache entry per body
/// carrying its position, physical aspect and live magnitude; the dot sized by that magnitude; and the selected planet's
/// panel stating the live magnitude at the viewing instant (time scrub included), its diameter, phase and ring tilt.
/// </summary>
[Collection("Astrometry")]
public sealed class SkyMapPlanetDetailTests
{
    private static readonly DateTimeOffset Instant = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Two reads inside the refresh window cost one rebuild, and the entry carries what the dot and the panel read: an
    /// aspect of its own body and the magnitude <see cref="PlanetaryMagnitude"/> gives for it.
    /// </summary>
    [Fact]
    public void ThePlanetCacheCarriesEachBodysAspectAndMagnitudeAndRebuildsOnce()
    {
        var state = new SkyMapState();

        var markers = state.GetPlanetPositionsCached(Instant).ToArray();
        state.GetPlanetPositionsCached(Instant.AddMilliseconds(500));

        state.PlanetCacheRebuilds.ShouldBe(1);
        markers.Length.ShouldBe(SkyMapRenderer.PlanetIndices.Length, "every body the atlas draws reduces");
        foreach (var marker in markers)
        {
            var aspect = marker.Aspect.ShouldNotBeNull(marker.Index.ToString());
            aspect.Planet.ShouldBe(marker.Index);
            marker.VMag.ShouldBe((float)PlanetaryMagnitude.Apparent(aspect), marker.Index.ToString());
            float.IsFinite(marker.VMag).ShouldBeTrue(marker.Index.ToString());
        }
    }

    /// <summary>
    /// The dot is sized by the live magnitude, not the type: on 2026-10-13 Venus (-4.4, near inferior conjunction) outweighs
    /// Jupiter (-1.9), which outweighs Mars (+1.0), and Uranus (+5.6) and Neptune (+7.7) are the smallest dot; the Sun and
    /// the Moon keep their fixed one. Under the old rule Jupiter outweighed Venus.
    /// </summary>
    [Fact]
    public void TheDotIsSizedByTheLiveMagnitude()
    {
        var state = new SkyMapState();
        float Radius(CatalogIndex body) => state.TryGetPlanetCached(body, Instant, out var m)
            ? SkyMapState.PlanetDotRadius(body, m.VMag)
            : throw new InvalidOperationException($"{body} not in the cache");

        Radius(CatalogIndex.Venus).ShouldBeGreaterThan(Radius(CatalogIndex.Jupiter));
        Radius(CatalogIndex.Jupiter).ShouldBeGreaterThan(Radius(CatalogIndex.Mars));
        Radius(CatalogIndex.Mars).ShouldBeGreaterThan(Radius(CatalogIndex.Uranus));
        Radius(CatalogIndex.Uranus).ShouldBe(1.5f);
        Radius(CatalogIndex.Neptune).ShouldBe(1.5f);
        Radius(CatalogIndex.Sol).ShouldBe(4f);
        Radius(CatalogIndex.Moon).ShouldBe(4f);
    }

    /// <summary>
    /// The selected planet's panel, driven through the REAL render: Mars selected at its 2025 January opposition reads its
    /// live magnitude there, and a time scrub of 358 days to its 2026 January conjunction moves the panel's magnitude with
    /// it, by more than two magnitudes. The catalogue's one static value, which the panel showed before, can be right at
    /// neither.
    /// </summary>
    [Fact]
    public async Task ThePanelsMagnitudeIsTheLiveOneAndFollowsTheTimeScrub()
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        using var renderer = new RgbaImageRenderer(400, 400);
        var tab = new SkyMapTab<RgbaImage>(renderer) { FontPath = FontResolver.ResolveSystemFont() };
        var opposition = new DateTimeOffset(2025, 1, 16, 22, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProviderWrapper(opposition);
        var planner = new PlannerState { ObjectDb = db, SiteLatitude = 48.0, SiteLongitude = 11.0, SiteTimeZone = TimeSpan.FromHours(1) };
        var content = new RectF32(0, 0, 400, 400);
        tab.State.TryGetPlanetCached(CatalogIndex.Mars, opposition, out var mars).ShouldBeTrue();
        tab.State.Search.InfoPanel = SkyMapSearchActions.PlanetInfoPanel(db, mars, 48.0, 11.0, opposition, SiteContext.Create(48.0, 11.0, opposition));

        tab.Render(planner, content, time);
        var atOpposition = tab.LastDrawnInfoPanel.ShouldNotBeNull();
        tab.State.TimeOffset = TimeSpan.FromDays(358);
        tab.Render(planner, content, time);
        var atConjunction = tab.LastDrawnInfoPanel.ShouldNotBeNull();

        atOpposition.VMag.ShouldBe((float)PlanetaryMagnitude.Apparent(PhysicalEphemeris.Compute(CatalogIndex.Mars, opposition)), 1e-3f);
        atOpposition.VMag.ShouldBeLessThan(-1f, "Mars at opposition");
        atConjunction.VMag.ShouldBe((float)PlanetaryMagnitude.Apparent(PhysicalEphemeris.Compute(CatalogIndex.Mars, opposition.AddDays(358))), 1e-3f);
        (atConjunction.VMag - atOpposition.VMag).ShouldBeGreaterThan(2f, "the scrub moves the magnitude");
        db.TryLookupByIndex(CatalogIndex.Mars, out var catalogued).ShouldBeTrue();
        Math.Abs((double)atOpposition.VMag - (double)catalogued.V_Mag).ShouldBeGreaterThan(0.5, "not the static catalogue value");
        atConjunction.Aspect.ShouldNotBeNull().Utc.ShouldBe(opposition.AddDays(358));
    }

    /// <summary>
    /// The panel's disk row: the diameter always, the lit fraction for a body whose lit share rounds below 100 %, the ring
    /// tilt for Saturn only. Expected from Horizons' row for 2026-10-13 in <c>horizons-bodies-2026-10-14.txt</c>: Venus a
    /// 5.03 % crescent of 56.86 arcsec; Jupiter 34.16 arcsec at 99.36 % (a gibbous phase, small but real); Saturn 19.68
    /// arcsec with its rings 7.2 degrees open to their south face (Horizons' planetographic -8.77 made planetocentric,
    /// tan B = (1 - f)^2 tan(-8.77), f = 0.098) and 99.99 % lit; the Moon 1807.6 arcsec, 30.1 arcmin, at 5.84 %.
    /// </summary>
    [Theory]
    [InlineData(CatalogIndex.Venus, "diam 56.9\"   lit 5%")]
    [InlineData(CatalogIndex.Jupiter, "diam 34.2\"   lit 99%")]
    [InlineData(CatalogIndex.Saturn, "diam 19.7\"   rings -7.2°")]
    [InlineData(CatalogIndex.Moon, "diam 30.1'   lit 6%")]
    public async Task ThePanelStatesTheDiskPhaseAndRings(CatalogIndex body, string expected)
    {
        var db = await SharedCatalogDB.InitAsync(TestContext.Current.CancellationToken);
        var state = new SkyMapState();
        state.TryGetPlanetCached(body, Instant, out var marker).ShouldBeTrue();

        var panel = SkyMapSearchActions.PlanetInfoPanel(db, marker, 48.0, 11.0, Instant, SiteContext.Create(48.0, 11.0, Instant));

        ObjectInfoPanel.DiskLine(panel).ShouldBe(expected);
        panel.VMag.ShouldBe(marker.VMag);
    }
}
