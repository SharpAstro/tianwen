using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class SkyMapSearchActionsTests
{
    // SkyMapSearchActions relies on ICelestialObjectDB for the real index. These
    // tests only cover the parts that don't need a loaded catalog — filter-with-empty
    // index and state plumbing. End-to-end filtering is exercised via the GUI.

    [Fact]
    public void FilterResultsWithEmptyIndexReturnsEmpty()
    {
        var search = new SkyMapSearchState();
        search.SearchInput.Text = "M31";
        // No SearchIndex set — simulating "catalog not loaded yet".
        search.SearchIndex = [];

        // Should not throw, just return no results.
        SkyMapSearchActions.FilterResults(search, new EmptyDb());

        search.Results.ShouldBeEmpty();
        search.SelectedResultIndex.ShouldBe(-1);
    }

    [Fact]
    public void FilterResultsWithShortQueryReturnsEmpty()
    {
        var search = new SkyMapSearchState();
        search.SearchIndex = ["M31", "Andromeda"];

        search.SearchInput.Text = "M";
        SkyMapSearchActions.FilterResults(search, new EmptyDb());

        search.Results.ShouldBeEmpty();
    }

    [Fact]
    public void CloseSearchDeactivatesInputAndClearsOpenFlag()
    {
        var search = new SkyMapSearchState();
        search.IsOpen = true;
        search.SearchInput.Activate("test");

        SkyMapSearchActions.CloseSearch(search);

        search.IsOpen.ShouldBeFalse();
        search.SearchInput.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void CommitResultWithNoSelectionReturnsFalse()
    {
        var search = new SkyMapSearchState();
        var skyMap = new SkyMapState();
        search.SelectedResultIndex = -1;

        var ok = SkyMapSearchActions.CommitResult(
            search, skyMap, new EmptyDb(),
            siteLat: 0, siteLon: 0,
            viewingUtc: DateTimeOffset.UtcNow,
            site: default);

        ok.ShouldBeFalse();
    }

    [Fact]
    public void InfoPanelDataForKnownPositionPopulatesAltAzAndRise()
    {
        var viewingUtc = new DateTimeOffset(2026, 9, 22, 18, 0, 0, TimeSpan.Zero);
        var site = SiteContext.Create(51.5, 0.0, viewingUtc);

        // Autumn equinox sky, RA 0h Dec +30 — easy object for a mid-northern observer.
        var info = SkyMapInfoPanelData.FromPosition(
            "Test", raHours: 0.0, decDeg: 30.0,
            siteLat: 51.5, siteLon: 0.0,
            viewingUtc: viewingUtc, site: site);

        info.Name.ShouldBe("Test");
        // Alt/Az computed — site is valid, so neither should be NaN.
        double.IsNaN(info.AltDeg).ShouldBeFalse();
        double.IsNaN(info.AzDeg).ShouldBeFalse();
        // Dec +30 from lat +51.5 can rise and set (not circumpolar, not hidden).
        info.Circumpolar.ShouldBeFalse();
        info.NeverRises.ShouldBeFalse();
        info.RiseTime.ShouldNotBeNull();
        info.TransitTime.ShouldNotBeNull();
        info.SetTime.ShouldNotBeNull();
    }

    // A nebula with a large shape and a star sitting inside the projected ellipse.
    // Plain click lands on the nebula (the ellipse expands the DSO hit radius and
    // short-circuits the star pass); Ctrl+click (preferPointSource) reaches the star.
    [Fact]
    public void ClickInsideNebulaEllipsePicksNebula_CtrlClickPicksStarInside()
    {
        // Index/type are irrelevant to the pick (it keys off grid + shape + distance),
        // but set sensible values for clarity.
        var nebula = new CelestialObject(CatalogIndex.NGC7331, ObjectType.HIIReg,
            12.0, 0.0, Constellation.Pegasus, Half.NaN, Half.NaN, Half.NaN,
            new HashSet<string> { "TestNebula" });
        var star = new CelestialObject(CatalogIndex.HIP025281, ObjectType.Star,
            12.0, 0.1 /* ~6' north of the nebula centre */, Constellation.Pegasus,
            Half.NaN, Half.NaN, Half.NaN, new HashSet<string> { "TestStar" });
        // 60' major axis -> ~250 px radius at this FOV: comfortably covers the star.
        var nebulaShape = new CelestialObjectShape((Half)60.0, (Half)60.0, (Half)0.0);
        var db = new ClickPickDb(nebula, star, nebulaShape);

        var skyMap = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            CenterRA = 12.0,
            CenterDec = 0.0,
            FieldOfViewDeg = 2.0,
            // The nebula follows the [O] layer; it must be enabled to be clickable (the click
            // resolver now honours per-layer visibility, like the rendered overlay).
            ShowObjectOverlay = true,
        };
        var viewMatrix = skyMap.ComputeViewMatrix();
        const float height = 1000f;
        var ppr = SkyMapProjection.PixelsPerRadian(height, skyMap.FieldOfViewDeg);
        const float cx = 500f, cy = 500f;

        // Click point = the star's projected position. The nebula projects to centre.
        SkyMapProjection.ProjectWithMatrix(star.RA, star.Dec, viewMatrix, ppr, cx, cy, out var starX, out var starY)
            .ShouldBeTrue();
        SkyMapProjection.ProjectWithMatrix(nebula.RA, nebula.Dec, viewMatrix, ppr, cx, cy, out var nebX, out var nebY)
            .ShouldBeTrue();

        // Precondition: the star sits beyond the plain 20 px click tolerance from the
        // nebula centroid (so without the ellipse expansion the nebula would NOT match),
        // yet inside the ~250 px shape radius (so the plain click DOES match the nebula).
        var offsetPx = MathF.Sqrt((starX - nebX) * (starX - nebX) + (starY - nebY) * (starY - nebY));
        offsetPx.ShouldBeGreaterThan(20f);
        offsetPx.ShouldBeLessThan(200f);

        var viewingUtc = DateTimeOffset.UtcNow;
        var site = SiteContext.Create(0, 0, viewingUtc);

        // Plain click -> nebula (ellipse swallows the click).
        var plain = new SkyMapSearchState();
        SkyMapSearchActions.SelectObjectByClick(
            plain, skyMap, db, 0, 0, viewingUtc, site,
            starX, starY, viewMatrix, ppr, cx, cy, preferPointSource: false).ShouldBeTrue();
        var plainInfo = plain.InfoPanel.ShouldNotBeNull();
        plainInfo.Name.ShouldBe("TestNebula");

        // Ctrl+click -> the star underneath the ellipse.
        var ctrl = new SkyMapSearchState();
        SkyMapSearchActions.SelectObjectByClick(
            ctrl, skyMap, db, 0, 0, viewingUtc, site,
            starX, starY, viewMatrix, ppr, cx, cy, preferPointSource: true).ShouldBeTrue();
        var ctrlInfo = ctrl.InfoPanel.ShouldNotBeNull();
        ctrlInfo.Name.ShouldBe("TestStar");
    }

    // SelectAtScreenPoint is the shared entry the desktop AppSignalHandler AND the browser Planner both
    // call: it derives the viewport projection (ppr, centre) from SkyMapState.LastContentRect +
    // CurrentViewMatrix and preferPointSource from the Ctrl modifier, then delegates to
    // SelectObjectByClick. Pins that boilerplate so the web (which has no AppSignalHandler) resolves a
    // sky-map click identically to desktop — the exact path wired in Planner.razor's WireSkyMapInteractions.
    [Fact]
    public void SelectAtScreenPoint_DerivesViewportAndCtrlFromState()
    {
        var nebula = new CelestialObject(CatalogIndex.NGC7331, ObjectType.HIIReg,
            12.0, 0.0, Constellation.Pegasus, Half.NaN, Half.NaN, Half.NaN,
            new HashSet<string> { "TestNebula" });
        var star = new CelestialObject(CatalogIndex.HIP025281, ObjectType.Star,
            12.0, 0.1, Constellation.Pegasus,
            Half.NaN, Half.NaN, Half.NaN, new HashSet<string> { "TestStar" });
        var nebulaShape = new CelestialObjectShape((Half)60.0, (Half)60.0, (Half)0.0);
        var db = new ClickPickDb(nebula, star, nebulaShape);

        const float height = 1000f;
        var skyMap = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            CenterRA = 12.0,
            CenterDec = 0.0,
            FieldOfViewDeg = 2.0,
            ShowObjectOverlay = true,
            // The helper reads the viewport off state — a 1000x1000 content rect at the origin, so the
            // derived centre is (500, 500) exactly like the SelectObjectByClick test above.
            LastContentRect = new RectF32(0f, 0f, 1000f, height),
        };
        skyMap.CurrentViewMatrix = skyMap.ComputeViewMatrix();

        var ppr = SkyMapProjection.PixelsPerRadian(height, skyMap.FieldOfViewDeg);
        SkyMapProjection.ProjectWithMatrix(star.RA, star.Dec, skyMap.CurrentViewMatrix, ppr, 500f, 500f,
            out var starX, out var starY).ShouldBeTrue();

        var viewingUtc = DateTimeOffset.UtcNow;
        var noProposals = ImmutableArray<ProposedObservation>.Empty;

        // Plain click -> nebula (the ellipse swallows the click); proves ppr/centre were derived from
        // LastContentRect (no ppr/cx/cy passed in).
        skyMap.Search.InfoPanel = null;
        SkyMapSearchActions.SelectAtScreenPoint(
            skyMap, db, 0, 0, viewingUtc, starX, starY, InputModifier.None, noProposals).ShouldBeTrue();
        skyMap.Search.InfoPanel.ShouldNotBeNull().Name.ShouldBe("TestNebula");

        // Ctrl click -> the star underneath (preferPointSource derived from the modifier bit).
        skyMap.Search.InfoPanel = null;
        SkyMapSearchActions.SelectAtScreenPoint(
            skyMap, db, 0, 0, viewingUtc, starX, starY, InputModifier.Ctrl, noProposals).ShouldBeTrue();
        skyMap.Search.InfoPanel.ShouldNotBeNull().Name.ShouldBe("TestStar");
    }

    // Dark nebulae follow the [D] layer. The click resolver must honour that: a hidden dark
    // nebula is NOT selectable (regression — it used to hit-test every DSO regardless of the
    // toggle), but enabling [D] or pinning the target makes it clickable again. Covers both the
    // DSO pass and the star pass (CoordinateGrid is composite, so the dark neb is in both).
    [Fact]
    public void DarkNebulaClickRespectsLayerToggleAndPinning()
    {
        var darkNeb = new CelestialObject(CatalogIndex.LDN00146, ObjectType.DarkNeb,
            12.0, 0.0, Constellation.Cygnus, Half.NaN, Half.NaN, Half.NaN,
            new HashSet<string> { "TestDarkNeb" });
        // A field star parked far off the click so the star pass never matches it — the result
        // is then driven purely by whether the dark nebula itself is clickable.
        var farStar = new CelestialObject(CatalogIndex.HIP025281, ObjectType.Star,
            12.0, 80.0, Constellation.Cygnus, Half.NaN, Half.NaN, Half.NaN,
            new HashSet<string> { "FarStar" });
        var shape = new CelestialObjectShape((Half)30.0, (Half)20.0, (Half)0.0);
        var db = new ClickPickDb(darkNeb, farStar, shape);

        var skyMap = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            CenterRA = 12.0,
            CenterDec = 0.0,
            FieldOfViewDeg = 2.0,
        };
        var viewMatrix = skyMap.ComputeViewMatrix();
        const float height = 1000f;
        var ppr = SkyMapProjection.PixelsPerRadian(height, skyMap.FieldOfViewDeg);
        const float cx = 500f, cy = 500f;

        SkyMapProjection.ProjectWithMatrix(darkNeb.RA, darkNeb.Dec, viewMatrix, ppr, cx, cy, out var nx, out var ny)
            .ShouldBeTrue();

        var viewingUtc = DateTimeOffset.UtcNow;
        var site = SiteContext.Create(0, 0, viewingUtc);

        // [D] layer OFF, not pinned -> the dark nebula is not selectable.
        skyMap.ShowDarkNebulae = false;
        var off = new SkyMapSearchState();
        SkyMapSearchActions.SelectObjectByClick(
            off, skyMap, db, 0, 0, viewingUtc, site,
            nx, ny, viewMatrix, ppr, cx, cy).ShouldBeFalse();
        off.InfoPanel.ShouldBeNull();

        // [D] layer ON -> selects the dark nebula.
        skyMap.ShowDarkNebulae = true;
        var on = new SkyMapSearchState();
        SkyMapSearchActions.SelectObjectByClick(
            on, skyMap, db, 0, 0, viewingUtc, site,
            nx, ny, viewMatrix, ppr, cx, cy).ShouldBeTrue();
        on.InfoPanel.ShouldNotBeNull().Name.ShouldBe("TestDarkNeb");

        // [D] layer OFF but pinned -> still clickable (renders as a landmark, stays selectable).
        skyMap.ShowDarkNebulae = false;
        var pinned = new SkyMapSearchState();
        SkyMapSearchActions.SelectObjectByClick(
            pinned, skyMap, db, 0, 0, viewingUtc, site,
            nx, ny, viewMatrix, ppr, cx, cy,
            pinnedCatalogIndices: new HashSet<CatalogIndex> { darkNeb.Index }).ShouldBeTrue();
        pinned.InfoPanel.ShouldNotBeNull().Name.ShouldBe("TestDarkNeb");
    }

    // Planets are ephemeris-computed (GetPlanetPositionsCached), so they are NOT in the
    // fixed-position DSO/star spatial grids the resolver searches -- a click on a planet dot used
    // to resolve to nothing. The resolver now hit-tests the live planet positions too. EmptyDb
    // makes the DSO/star passes find nothing, so the planet pass is what resolves the click. The
    // expected position is read from the ephemeris (not hardcoded), so the test is robust to which
    // bodies VSOP87a reduces.
    [Fact]
    public void ClickOnPlanetSelectsItViaLiveEphemeris()
    {
        var skyMap = new SkyMapState
        {
            Mode = SkyMapMode.Equatorial,
            FieldOfViewDeg = 10.0,
        };
        var viewingUtc = new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero);

        var positions = skyMap.GetPlanetPositionsCached(viewingUtc);
        positions.Length.ShouldBeGreaterThan(0, "the ephemeris must yield at least one planet to click");
        var (planetIdx, pRa, pDec) = positions[0];

        // Centre the view on the planet so it projects to the screen centre, then click there.
        skyMap.CenterRA = pRa;
        skyMap.CenterDec = pDec;
        var viewMatrix = skyMap.ComputeViewMatrix();
        const float height = 1000f;
        var ppr = SkyMapProjection.PixelsPerRadian(height, skyMap.FieldOfViewDeg);
        const float cx = 500f, cy = 500f;
        SkyMapProjection.ProjectWithMatrix(pRa, pDec, viewMatrix, ppr, cx, cy, out var sx, out var sy)
            .ShouldBeTrue();

        var site = SiteContext.Create(0, 0, viewingUtc);
        var search = new SkyMapSearchState();
        SkyMapSearchActions.SelectObjectByClick(
            search, skyMap, new EmptyDb(), 0, 0, viewingUtc, site,
            sx, sy, viewMatrix, ppr, cx, cy).ShouldBeTrue();

        var expectedName = planetIdx == CatalogIndex.Moon ? "Moon"
            : planetIdx == CatalogIndex.Sol ? "Sun"
            : planetIdx.ToCanonical();
        var info = search.InfoPanel.ShouldNotBeNull();
        info.Name.ShouldBe(expectedName);
        info.RA.ShouldBe(pRa, 1e-6);   // live ephemeris RA/Dec, not a stale catalog entry
        info.Dec.ShouldBe(pDec, 1e-6);
    }

    // Committing a planet search result (Enter on "Jupiter") used to bail out: planets carry NaN
    // catalog coords ("not supported in Phase 1"), so CommitResult returned false and Enter did
    // nothing. It now resolves the live ephemeris position and commits to that.
    [Fact]
    public void CommitResultForPlanetResolvesLiveEphemeris()
    {
        var skyMap = new SkyMapState { Mode = SkyMapMode.Equatorial, FieldOfViewDeg = 10.0 };
        var viewingUtc = new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero);

        var positions = skyMap.GetPlanetPositionsCached(viewingUtc);
        positions.Length.ShouldBeGreaterThan(0, "the ephemeris must yield at least one planet to commit");
        var (planetIdx, pRa, pDec) = positions[0];

        // The catalog returns the planet as a NaN-coord solar-system stub (as the real DB does).
        var db = new PlanetStubDb(planetIdx, "TestPlanet");
        var search = new SkyMapSearchState
        {
            IsOpen = true,
            Results = [new SkyMapSearchResult(Display: "TestPlanet", Index: planetIdx, ObjType: ObjectType.Unknown, VMag: float.NaN)],
            SelectedResultIndex = 0,
        };

        var site = SiteContext.Create(0, 0, viewingUtc);
        SkyMapSearchActions.CommitResult(search, skyMap, db, 0, 0, viewingUtc, site).ShouldBeTrue();

        var info = search.InfoPanel.ShouldNotBeNull();
        info.Name.ShouldBe("TestPlanet");
        info.RA.ShouldBe(pRa, 1e-6);   // committed to the LIVE position, not the NaN catalog stub
        info.Dec.ShouldBe(pDec, 1e-6);
        info.ObjType.ShouldBe(ObjectType.Planet, "the predefined planet type carries into the panel");
        info.VMag.ShouldBe(-2.2f, 0.01f);  // predefined reference magnitude, not NaN ("mag -")
        // Constellation is computed from the LIVE position (planets wander) -- not the catalog stub.
        if (ConstellationBoundary.TryFindConstellation(pRa, pDec, out var expectedConstellation))
        {
            info.Constellation.ShouldBe(expectedConstellation);
        }
        search.IsOpen.ShouldBeFalse("a successful commit closes the modal");
    }

    // Minimal ICelestialObjectDB stub — only CreateAutoCompleteList and TryLookupByIndex
    // are needed for the tests above. Rest throw to catch accidental usage.
    private class EmptyDb : TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB
    {
        public System.Collections.Generic.IReadOnlySet<TianWen.Lib.Astrometry.Catalogs.CatalogIndex> AllObjectIndices
            => new System.Collections.Generic.HashSet<TianWen.Lib.Astrometry.Catalogs.CatalogIndex>();

        public System.Collections.Generic.IReadOnlySet<TianWen.Lib.Astrometry.Catalogs.Catalog> Catalogs
            => new System.Collections.Generic.HashSet<TianWen.Lib.Astrometry.Catalogs.Catalog>();

        public System.Collections.Generic.IReadOnlyCollection<string> CommonNames => Array.Empty<string>();
        public virtual TianWen.Lib.Astrometry.Catalogs.IRaDecIndex CoordinateGrid => new EmptyIndex();
        public virtual TianWen.Lib.Astrometry.Catalogs.IRaDecIndex DeepSkyCoordinateGrid => new EmptyIndex();
        public int HipStarCount => 0;
        public int Tycho2StarCount => 0;

        public int CopyTycho2Stars(Span<TianWen.Lib.Astrometry.Catalogs.Tycho2StarLite> destination, int startIndex = 0) => 0;

        public System.Threading.Tasks.Task InitDBAsync(bool waitForTycho2BulkLoad = false, System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task EnsureTycho2DataLoadedAsync(System.Threading.CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.CompletedTask;

        public int LastInitProcessed => 0;
        public int LastInitFailed => 0;

        public bool TryGetCrossIndices(TianWen.Lib.Astrometry.Catalogs.CatalogIndex idx,
            out System.Collections.Generic.IReadOnlySet<TianWen.Lib.Astrometry.Catalogs.CatalogIndex> crossIndices)
        {
            crossIndices = new System.Collections.Generic.HashSet<TianWen.Lib.Astrometry.Catalogs.CatalogIndex>();
            return false;
        }

        public virtual bool TryGetShape(TianWen.Lib.Astrometry.Catalogs.CatalogIndex index, out TianWen.Lib.Astrometry.Catalogs.CelestialObjectShape shape)
        { shape = default; return false; }

        public virtual bool TryLookupByIndex(TianWen.Lib.Astrometry.Catalogs.CatalogIndex index, out TianWen.Lib.Astrometry.Catalogs.CelestialObject celestialObject)
        { celestialObject = default; return false; }

        public bool TryLookupHIP(int hipNumber, out double ra, out double dec, out float vMag, out float bv)
        { ra = dec = 0; vMag = bv = 0; return false; }

        public bool TryResolveCommonName(string name, out System.Collections.Generic.IReadOnlyList<TianWen.Lib.Astrometry.Catalogs.CatalogIndex> matches)
        { matches = Array.Empty<TianWen.Lib.Astrometry.Catalogs.CatalogIndex>(); return false; }

        private sealed class EmptyIndex : TianWen.Lib.Astrometry.Catalogs.IRaDecIndex
        {
            public System.Collections.Generic.IReadOnlyCollection<TianWen.Lib.Astrometry.Catalogs.CatalogIndex> this[double ra, double dec]
                => Array.Empty<TianWen.Lib.Astrometry.Catalogs.CatalogIndex>();
        }
    }

    // Grid-populated stub for click-pick tests: the DSO grid yields the nebula; the
    // (composite) coordinate grid yields both the nebula and the star, mirroring how
    // a bright/field star and an extended object co-occupy the real index.
    private sealed class ClickPickDb(CelestialObject nebula, CelestialObject star, CelestialObjectShape nebulaShape) : EmptyDb
    {
        public override IRaDecIndex CoordinateGrid => new FixedIndex(nebula.Index, star.Index);
        public override IRaDecIndex DeepSkyCoordinateGrid => new FixedIndex(nebula.Index);

        public override bool TryLookupByIndex(CatalogIndex index, out CelestialObject celestialObject)
        {
            if (index == nebula.Index) { celestialObject = nebula; return true; }
            if (index == star.Index) { celestialObject = star; return true; }
            celestialObject = default;
            return false;
        }

        public override bool TryGetShape(CatalogIndex index, out CelestialObjectShape shape)
        {
            if (index == nebula.Index) { shape = nebulaShape; return true; }
            shape = default;
            return false;
        }

        private sealed class FixedIndex(params CatalogIndex[] items) : IRaDecIndex
        {
            public IReadOnlyCollection<CatalogIndex> this[double ra, double dec] => items;
        }
    }

    // Returns the given index as a NaN-coord solar-system stub (mirrors how planets sit in the real
    // catalog: a named entry with no fixed RA/Dec, since their position is ephemeris-computed).
    private sealed class PlanetStubDb(CatalogIndex planetIndex, string name) : EmptyDb
    {
        public override bool TryLookupByIndex(CatalogIndex index, out CelestialObject celestialObject)
        {
            if (index == planetIndex)
            {
                // Predefined planet metadata as the real catalog holds it: type Planet, reference
                // magnitude, NaN coords (position is ephemeris-computed), no fixed constellation.
                celestialObject = new CelestialObject(planetIndex, ObjectType.Planet,
                    double.NaN, double.NaN, default, (Half)(-2.2), Half.NaN, (Half)0.83,
                    new HashSet<string> { name });
                return true;
            }
            celestialObject = default;
            return false;
        }
    }
}
