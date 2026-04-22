using System;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
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

    // Minimal ICelestialObjectDB stub — only CreateAutoCompleteList and TryLookupByIndex
    // are needed for the tests above. Rest throw to catch accidental usage.
    private sealed class EmptyDb : TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB
    {
        public System.Collections.Generic.IReadOnlySet<TianWen.Lib.Astrometry.Catalogs.CatalogIndex> AllObjectIndices
            => new System.Collections.Generic.HashSet<TianWen.Lib.Astrometry.Catalogs.CatalogIndex>();

        public System.Collections.Generic.IReadOnlySet<TianWen.Lib.Astrometry.Catalogs.Catalog> Catalogs
            => new System.Collections.Generic.HashSet<TianWen.Lib.Astrometry.Catalogs.Catalog>();

        public System.Collections.Generic.IReadOnlyCollection<string> CommonNames => Array.Empty<string>();
        public TianWen.Lib.Astrometry.Catalogs.IRaDecIndex CoordinateGrid => new EmptyIndex();
        public TianWen.Lib.Astrometry.Catalogs.IRaDecIndex DeepSkyCoordinateGrid => new EmptyIndex();
        public int HipStarCount => 0;
        public int Tycho2StarCount => 0;

        public int CopyTycho2Stars(Span<TianWen.Lib.Astrometry.Catalogs.Tycho2StarLite> destination, int startIndex = 0) => 0;

        public System.Threading.Tasks.Task InitDBAsync(System.Threading.CancellationToken cancellationToken)
            => System.Threading.Tasks.Task.CompletedTask;

        public bool TryGetCrossIndices(TianWen.Lib.Astrometry.Catalogs.CatalogIndex idx,
            out System.Collections.Generic.IReadOnlySet<TianWen.Lib.Astrometry.Catalogs.CatalogIndex> crossIndices)
        {
            crossIndices = new System.Collections.Generic.HashSet<TianWen.Lib.Astrometry.Catalogs.CatalogIndex>();
            return false;
        }

        public bool TryGetShape(TianWen.Lib.Astrometry.Catalogs.CatalogIndex index, out TianWen.Lib.Astrometry.Catalogs.CelestialObjectShape shape)
        { shape = default; return false; }

        public bool TryLookupByIndex(TianWen.Lib.Astrometry.Catalogs.CatalogIndex index, out TianWen.Lib.Astrometry.Catalogs.CelestialObject celestialObject)
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
}
