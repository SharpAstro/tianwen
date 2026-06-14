using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Pure helpers for the F3 sky-map search modal. Renderer-agnostic —
/// shared by <c>SkyMapTab</c> (CPU fallback + TUI) and <c>VkSkyMapTab</c> (GPU).
/// </summary>
public static class SkyMapSearchActions
{
    /// <summary>Max rows shown in the Object-tab results list.</summary>
    public const int MaxResults = 30;

    // Pixel distance (screen-space) within which a click on the sky map snaps
    // to a nearby catalog object. 20 px matches Stellarium's default feel —
    // precise enough for stars, forgiving enough for DSO clicks.
    private const float ClickToleranceScreenPx = 20f;

    /// <summary>
    /// Open the modal and lazily build the search index from the loaded catalog.
    /// Idempotent — repeat opens just re-focus the search box.
    /// </summary>
    public static void OpenSearch(SkyMapSearchState search, ICelestialObjectDB db)
    {
        search.IsOpen = true;

        // Build the index once per catalog load. The autocomplete list is
        // canonical + common names — ~200 K entries, enough for live filtering.
        if (search.SearchIndex.IsDefaultOrEmpty)
        {
            search.SearchIndex = [.. db.CreateAutoCompleteList()];
        }

        search.SearchInput.Activate();
        search.SearchInput.SelectAll();
    }

    /// <summary>Close the modal. Keeps the info panel so the user still sees the selection.</summary>
    public static void CloseSearch(SkyMapSearchState search)
    {
        search.IsOpen = false;
        search.SearchInput.Deactivate();
    }

    /// <summary>
    /// Recompute <see cref="SkyMapSearchState.Results"/> from the current
    /// <see cref="SkyMapSearchState.SearchInput"/> text. With Tycho-2 in the
    /// catalog the index is ~2.5M entries; we exploit the fact that
    /// <see cref="ICelestialObjectDB.CreateAutoCompleteList"/> returns its
    /// entries sorted ordinal-ignore-case to binary-search the prefix
    /// range in O(log N), then scan the contiguous prefix run for matches.
    /// A substring fallback runs only when the prefix scan returns nothing,
    /// keeping the steady-state hot path off the full-array scan that used
    /// to fire on every keystroke.
    /// </summary>
    public static void FilterResults(SkyMapSearchState search, ICelestialObjectDB db)
    {
        var query = search.SearchInput.Text;

        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            search.Results = [];
            search.SelectedResultIndex = -1;
            return;
        }

        // Virtual TYC path: the ~2.5M Tycho-2 stars deliberately don't appear in
        // the autocomplete list (would balloon the sort to ~5M entries with ~120MB
        // of string allocations). Instead, queries that look like "TYC <digits>..."
        // are served by a direct byte[] walk over the catalogue, which decodes a
        // small destination buffer on-the-fly without materialising any noise stars.
        if (TryHandleTycPrefix(search, db, query))
        {
            return;
        }

        var index = search.SearchIndex;
        var candidates = new List<(string Entry, int Score)>(capacity: MaxResults * 2);

        // 1. Binary-search to find the first entry >= query, then iterate forward
        //    while StartsWith(query) holds. The sorted array means this prefix
        //    range is contiguous -- a couple log2(N) ~ 22 string compares followed
        //    by O(matches) of linear scan.
        var startIdx = LowerBound(index, query);
        for (var i = startIdx; i < index.Length; i++)
        {
            var entry = index[i];
            if (!entry.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                // The sorted order means once StartsWith stops, no later entry
                // can satisfy it either. Stop scanning.
                break;
            }

            // Score the prefix match by whether it covers a complete catalog
            // token (followed by a delimiter or end-of-string) vs sits in the
            // middle of a longer token. The boundary case wins so e.g.
            // "TYC 425" surfaces TYC 425-2502-1 ahead of TYC 4250-1960-1.
            int score;
            if (entry.Length == query.Length)
            {
                score = 100;  // exact
            }
            else
            {
                var next = entry[query.Length];
                score = next is '-' or ' ' or '/' or '.' ? 95 : 80;
            }
            candidates.Add((entry, score));
        }

        // 2. Substring fallback: only run when prefix yielded nothing -- with
        //    millions of entries a Contains scan is hundreds of ms, so we skip
        //    it when the prefix already produced anything useful.
        if (candidates.Count == 0)
        {
            for (var i = 0; i < index.Length; i++)
            {
                var entry = index[i];
                if (entry.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add((entry, 40));
                    if (candidates.Count >= MaxResults * 2)
                    {
                        break;
                    }
                }
            }
        }

        // Score DESC, then alphabetical ASC as a stable tie-break so the user
        // sees a predictable ordering of equally-scored entries.
        candidates.Sort(static (a, b) =>
        {
            var c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : string.Compare(a.Entry, b.Entry, StringComparison.OrdinalIgnoreCase);
        });

        var take = Math.Min(candidates.Count, MaxResults);
        var results = ImmutableArray.CreateBuilder<SkyMapSearchResult>(take);
        var seenIndices = new HashSet<CatalogIndex>();
        for (var i = 0; i < take; i++)
        {
            var entry = candidates[i].Entry;
            if (!TryResolveToObject(db, entry, out var obj)) continue;
            if (!seenIndices.Add(obj.Index)) continue;

            results.Add(new SkyMapSearchResult(
                Display: entry,
                Index: obj.Index,
                ObjType: obj.ObjectType,
                VMag: (float)obj.V_Mag));
        }

        search.Results = results.ToImmutable();
        search.SelectedResultIndex = search.Results.Length > 0 ? 0 : -1;
        search.ResultsScrollOffset = 0;
    }

    /// <summary>
    /// Standard lower-bound binary search: returns the index of the first
    /// entry in <paramref name="sorted"/> that is greater-or-equal to
    /// <paramref name="query"/> under <see cref="StringComparison.OrdinalIgnoreCase"/>.
    /// Returns <c>sorted.Length</c> when every entry is strictly less than the
    /// query (i.e. query would insert at the end).
    /// </summary>
    private static int LowerBound(ImmutableArray<string> sorted, string query)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (string.Compare(sorted[mid], query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    /// <summary>
    /// Detect a "TYC ..." (or "TYC..." / "TYC-...") query, strip the catalog tag,
    /// and route to <see cref="ICelestialObjectDB.FindTycho2ByCanonicalPrefix"/>.
    /// Returns true when the query was TYC-shaped (results written to
    /// <paramref name="search"/>); false to let the caller continue with the
    /// general autocomplete-list scan.
    /// </summary>
    private static bool TryHandleTycPrefix(SkyMapSearchState search, ICelestialObjectDB db, string query)
    {
        var trimmed = query.AsSpan().Trim();
        if (trimmed.Length < 4) return false;  // need at least "TYC" + 1 digit
        if (!trimmed.StartsWith("TYC", StringComparison.OrdinalIgnoreCase)) return false;

        // Allow either whitespace or a stray "-" between TYC and the first
        // digit so "TYC 425", "TYC-425", "TYC425" all work.
        var rest = trimmed[3..].TrimStart();
        if (!rest.IsEmpty && rest[0] == '-')
        {
            rest = rest[1..].TrimStart();
        }
        if (rest.IsEmpty) return false;

        // Allocate a scratch span on the stack -- MaxResults * 2 gives the dedupe
        // step downstream some slack. ~30 records * 16 bytes = ~480 B, well below
        // any stackalloc limit.
        Span<Tycho2PrefixMatch> buf = stackalloc Tycho2PrefixMatch[MaxResults * 2];
        var count = db.FindTycho2ByCanonicalPrefix(rest, buf);

        var take = Math.Min(count, MaxResults);
        var results = ImmutableArray.CreateBuilder<SkyMapSearchResult>(take);
        for (var i = 0; i < take; i++)
        {
            var m = buf[i];
            // Format canonical display directly from the triple -- one InvariantCulture
            // string interpolation, no Base91 work. The CatalogIndex round-trip via
            // EncodeTyc2CatalogIndex + AbbreviationToCatalogIndex is still needed because
            // SkyMapSearchResult.Index is what the commit handler hands to
            // db.TryLookupByIndex; only the (up to MaxResults) records that actually
            // make it to the UI pay this cost, never the scanned-but-overflowed records.
            var display = string.Create(CultureInfo.InvariantCulture, $"TYC {m.Tyc1}-{m.Tyc2}-{m.Tyc3}");
            var encoded = CatalogUtils.EncodeTyc2CatalogIndex(Catalog.Tycho2, m.Tyc1, m.Tyc2, m.Tyc3);
            var idx = CatalogUtils.AbbreviationToCatalogIndex(encoded, isBase91Encoded: true);
            results.Add(new SkyMapSearchResult(
                Display: display,
                Index: idx,
                ObjType: ObjectType.Star,
                VMag: m.VMag));
        }

        search.Results = results.ToImmutable();
        search.SelectedResultIndex = search.Results.Length > 0 ? 0 : -1;
        search.ResultsScrollOffset = 0;
        return true;
    }

    /// <summary>
    /// Commit the currently-selected result: slew the sky map to the object,
    /// populate the info panel, and close the modal. Returns true on success.
    /// </summary>
    public static bool CommitResult(
        SkyMapSearchState search,
        SkyMapState skyMap,
        ICelestialObjectDB db,
        double siteLat, double siteLon,
        DateTimeOffset viewingUtc,
        in SiteContext site)
    {
        if (search.SelectedResultIndex < 0 || search.SelectedResultIndex >= search.Results.Length)
        {
            return false;
        }

        var result = search.Results[search.SelectedResultIndex];
        if (result.Index is not { } catIdx) return false;
        if (!db.TryLookupByIndex(catIdx, out var obj)) return false;

        if (double.IsNaN(obj.RA) || double.IsNaN(obj.Dec))
        {
            // Solar-system stubs in the DB have NaN coords — not supported in Phase 1.
            return false;
        }

        SlewTo(skyMap, obj.RA, obj.Dec);
        search.InfoPanel = SkyMapInfoPanelData.FromCatalogObject(
            obj, siteLat, siteLon, viewingUtc, site,
            ResolveShape(db, catIdx));

        CloseSearch(search);
        return true;
    }

    /// <summary>
    /// Click on the sky map — project the click back to RA/Dec, find the nearest
    /// catalog object within <see cref="ClickToleranceScreenPx"/>, populate the
    /// info panel. Returns true when an object was matched. DSOs are preferred
    /// over stars at equal distance (so clicking M31's halo picks the galaxy,
    /// not the nearest faint Tycho-2 star).
    /// <para>When <paramref name="preferPointSource"/> is set (Ctrl+click), the
    /// preference inverts: the enclosing extended-object ellipse no longer expands
    /// its hit radius, and the star pass always runs and wins over a co-located DSO.
    /// This lets a click inside a large IC/nebula shape select a star underneath it
    /// instead of being swallowed by the shape. A DSO is still returned as a fallback
    /// when its centroid is the only thing within tolerance and no star is hit.</para>
    /// </summary>
    public static bool SelectObjectByClick(
        SkyMapSearchState search,
        SkyMapState skyMap,
        ICelestialObjectDB db,
        double siteLat, double siteLon,
        DateTimeOffset viewingUtc,
        in SiteContext site,
        float clickScreenX, float clickScreenY,
        in Matrix4x4 viewMatrix,
        double pixelsPerRadian, float centerX, float centerY,
        bool preferPointSource = false,
        IReadOnlySet<CatalogIndex>? pinnedCatalogIndices = null)
    {
        var (clickRa, clickDec) = SkyMapProjection.UnprojectWithMatrix(
            clickScreenX, clickScreenY, viewMatrix, pixelsPerRadian, centerX, centerY);

        // Walk a 3x3 window of spatial-index cells around the click. The index
        // cells are ~1 deg squares; at mid-FOV the click tolerance can span up
        // to ~1.5 cells, so a single-cell lookup misses objects one cell over.
        // RA wraps 0..24h; Dec clamps to poles (handled by the index itself).
        const double CellRaHours = 1.0 / 15.0;   // 4 min of RA = ~1 deg at equator
        const double CellDecDeg = 1.0;
        Span<(double Ra, double Dec)> probes = stackalloc (double, double)[9];
        var k = 0;
        for (var di = -1; di <= 1; di++)
        {
            for (var dj = -1; dj <= 1; dj++)
            {
                var probeRa = (clickRa + di * CellRaHours + 24.0) % 24.0;
                var probeDec = Math.Clamp(clickDec + dj * CellDecDeg, -90.0, 90.0);
                probes[k++] = (probeRa, probeDec);
            }
        }

        // DSOs first. Hit test uses max(ClickTolerancePx, shape major-axis radius)
        // so clicks inside a large nebula like Eta Carinae / NGC 7000 land on the
        // nebula instead of a random Tycho star at its edge. Among overlapping DSO
        // hits we pick the one whose centroid is closest to the click — that way
        // a small nested object (e.g. M42 inside the Orion Molecular Cloud) wins
        // over the surrounding extended shape.
        CatalogIndex? bestDsoIdx = null;
        var bestDsoDistSq = double.MaxValue;
        var seenDso = new HashSet<CatalogIndex>();
        foreach (var (probeRa, probeDec) in probes)
        {
            foreach (var idx in db.DeepSkyCoordinateGrid[probeRa, probeDec])
            {
                if (!seenDso.Add(idx)) continue;
                if (!db.TryLookupByIndex(idx, out var o)) continue;
                if (double.IsNaN(o.RA) || double.IsNaN(o.Dec)) continue;

                // Honour the same per-layer visibility the rendered overlay uses (mirrors
                // OverlayEngine.GatherSkyMapOverlayCandidates): dark nebulae follow the [D]
                // layer, all other catalog objects follow the [O] layer, and pinned planner
                // targets stay clickable as landmarks regardless of layer state. Without this a
                // hidden object stays selectable by a click on apparently-empty sky.
                if (!IsDsoLayerClickable(o.ObjectType, o.Index, idx, skyMap, pinnedCatalogIndices))
                {
                    continue;
                }

                if (!SkyMapProjection.ProjectWithMatrix(o.RA, o.Dec, viewMatrix, pixelsPerRadian, centerX, centerY,
                        out var sx, out var sy))
                {
                    continue;
                }

                var dx = sx - clickScreenX;
                var dy = sy - clickScreenY;
                var distSq = dx * dx + dy * dy;

                // Effective hit radius: click tolerance, extended to the shape's
                // projected major-axis radius for extended objects. Arcmin -> rad
                // -> screen px uses the current pixelsPerRadian.
                // Ctrl+click (preferPointSource) skips the shape expansion so the
                // ellipse no longer swallows clicks meant for stars inside it — the
                // DSO then only matches near its centroid.
                var hitRadiusPx = (double)ClickToleranceScreenPx;
                if (!preferPointSource && db.TryGetShape(idx, out var shape))
                {
                    var majorArcmin = (double)shape.MajorAxis;
                    if (majorArcmin > 0)
                    {
                        var majorRadiusRad = majorArcmin * Math.PI / (180.0 * 60.0) * 0.5;
                        var shapeRadiusPx = majorRadiusRad * pixelsPerRadian;
                        if (shapeRadiusPx > hitRadiusPx) hitRadiusPx = shapeRadiusPx;
                    }
                }

                if (distSq <= hitRadiusPx * hitRadiusPx && distSq < bestDsoDistSq)
                {
                    bestDsoDistSq = distSq;
                    bestDsoIdx = idx;
                }
            }
        }

        CatalogIndex? bestIdx = bestDsoIdx;
        var bestDistSq = bestDsoDistSq;

        // Stars — when no DSO matched, OR when the caller forced a point-source pick
        // (Ctrl+click). Filter by the current visible-magnitude cutoff so we never
        // "select" a Tycho star that isn't drawn on screen. Hit radius scales with the
        // rendered star size: brighter stars draw bigger sprites (Stellarium-style
        // pow10 curve) and should be proportionally easier to click. 1.5x the visual
        // radius is slop room, floored at 20 px.
        // Note: the star pass resets bestDistSq but not bestIdx, so when
        // preferPointSource finds no star it falls back to the (tight-radius) DSO hit.
        if (bestDsoIdx is null || preferPointSource)
        {
            var magLimit = skyMap.EffectiveMagnitudeLimit;
            var fovDeg = skyMap.FieldOfViewDeg;
            bestDistSq = double.MaxValue;
            var seenStar = new HashSet<CatalogIndex>();
            foreach (var (probeRa, probeDec) in probes)
            {
                foreach (var idx in db.CoordinateGrid[probeRa, probeDec])
                {
                    if (!seenStar.Add(idx)) continue;
                    if (!db.TryLookupByIndex(idx, out var o)) continue;
                    if (double.IsNaN(o.RA) || double.IsNaN(o.Dec)) continue;
                    // Stars follow the visible magnitude cutoff (same rule the GPU uses; NaN
                    // V_Mag falls through as visible) and are never layer-gated -- the star
                    // field is always drawn. But CoordinateGrid is the COMPOSITE index
                    // (deep-sky + Tycho-2), so a layer-hidden deep-sky object (e.g. a dark
                    // nebula with [D] off) can surface here too; gate any non-star by the same
                    // per-layer visibility as the DSO pass so it can't be selected through the
                    // star pass after the DSO pass already skipped it.
                    var vMag = (float)o.V_Mag;
                    if (o.ObjectType.IsStar)
                    {
                        if (!float.IsNaN(vMag) && vMag > magLimit) continue;
                    }
                    else if (!IsDsoLayerClickable(o.ObjectType, o.Index, idx, skyMap, pinnedCatalogIndices))
                    {
                        continue;
                    }
                    if (!SkyMapProjection.ProjectWithMatrix(o.RA, o.Dec, viewMatrix, pixelsPerRadian, centerX, centerY,
                            out var sx, out var sy))
                    {
                        continue;
                    }

                    var dx = sx - clickScreenX;
                    var dy = sy - clickScreenY;
                    var distSq = dx * dx + dy * dy;

                    var starRadius = float.IsNaN(vMag)
                        ? ClickToleranceScreenPx
                        : SkyMapProjection.StarRadius(vMag, fovDeg) * 1.5f;
                    var hitRadius = Math.Max(starRadius, ClickToleranceScreenPx);
                    if (distSq <= hitRadius * hitRadius && distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestIdx = idx;
                    }
                }
            }
        }

        if (bestIdx is not { } hit || !db.TryLookupByIndex(hit, out var obj))
        {
            return false;
        }

        search.InfoPanel = SkyMapInfoPanelData.FromCatalogObject(
            obj, siteLat, siteLon, viewingUtc, site,
            ResolveShape(db, hit));
        return true;
    }

    /// <summary>
    /// Whether a deep-sky object is currently selectable by a sky-map click, given the
    /// per-layer visibility toggles. Mirrors the render-side filter in
    /// <c>OverlayEngine.GatherSkyMapOverlayCandidates</c>: dark nebulae follow the [D] layer
    /// (<see cref="SkyMapState.ShowDarkNebulae"/>), every other catalog object follows the [O]
    /// layer (<see cref="SkyMapState.ShowObjectOverlay"/>), and pinned planner targets are
    /// always clickable (they render as landmarks even when the layer is off).
    /// </summary>
    private static bool IsDsoLayerClickable(
        ObjectType objectType, CatalogIndex objIndex, CatalogIndex gridIndex,
        SkyMapState skyMap, IReadOnlySet<CatalogIndex>? pinnedCatalogIndices)
    {
        if (pinnedCatalogIndices is not null
            && ((objIndex != default && pinnedCatalogIndices.Contains(objIndex))
                || pinnedCatalogIndices.Contains(gridIndex)))
        {
            return true;
        }

        return objectType == ObjectType.DarkNeb ? skyMap.ShowDarkNebulae : skyMap.ShowObjectOverlay;
    }

    /// <summary>
    /// Clear the selected object (e.g. on right-click or dedicated "clear" key).
    /// </summary>
    public static void ClearSelection(SkyMapSearchState search)
    {
        search.InfoPanel = null;
    }

    // -----------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------

    private static bool TryResolveToObject(
        ICelestialObjectDB db, string entry, out CelestialObject obj)
    {
        if (db.TryResolveCommonName(entry, out var matches) && matches.Count > 0
            && db.TryLookupByIndex(matches[0], out obj))
        {
            return true;
        }
        if (db.TryLookupByIndex(entry, out obj))
        {
            return true;
        }
        obj = default;
        return false;
    }

    private static CelestialObjectShape? ResolveShape(ICelestialObjectDB db, CatalogIndex idx)
        => db.TryGetShape(idx, out var shape) ? shape : null;

    private static void SlewTo(SkyMapState skyMap, double raHours, double decDeg)
        => SkyMapViewActions.CenterOn(skyMap, raHours, decDeg);
}
