using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Comets;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Sequencing;

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
    /// Open the modal and lazily build the search index from the loaded catalog, merged with the comet set
    /// (designations + common names). Idempotent — repeat opens just re-focus the search box, but the index
    /// is rebuilt if the comet repository has since loaded (it loads in the background after startup, so the
    /// first open may predate it).
    /// </summary>
    public static void OpenSearch(SkyMapSearchState search, ICelestialObjectDB db, ICometRepository? comets = null)
    {
        search.IsOpen = true;

        // Build the index once per catalog load. The autocomplete list is canonical + common names
        // (~200 K entries); comets add a few thousand more. Rebuild if comets arrived after the first open.
        var cometsPending = comets is { All.Length: > 0 } && search.CometEntries.Count == 0;
        if (search.SearchIndex.IsDefaultOrEmpty || cometsPending)
        {
            search.SearchIndex = BuildSearchIndex(search, db, comets);
        }

        search.SearchInput.Activate();
        search.SearchInput.SelectAll();
    }

    // Merge the catalog autocomplete list with comet designations + common names, keeping the result sorted
    // ordinal-ignore-case (FilterResults binary-searches it). Each comet contributes up to two searchable
    // keys (canonical + common name), both routed to its index via SkyMapSearchState.CometEntries.
    private static ImmutableArray<string> BuildSearchIndex(SkyMapSearchState search, ICelestialObjectDB db, ICometRepository? comets)
    {
        var entries = new List<string>(db.CreateAutoCompleteList());
        search.CometEntries.Clear();

        // Register a searchable comet key -> (index, full display label). Deduped, appended to the sorted
        // index. The display is always the "designation (common name)" form whichever key matched.
        void AddCometKey(string key, CatalogIndex idx, string display)
        {
            if (search.CometEntries.TryAdd(key, (idx, display)))
            {
                entries.Add(key);
            }
        }

        // Every accepted comet key spelling (canonical / common name / parenthetical / slash), from the
        // shared CometSearchKeys source so the sky-map + planner-tab searches stay identical. The label
        // is always CometElements.DisplayName ("10P/Tempel" periodic, "C/2026 A1 (PANSTARRS)" provisional).
        foreach (var (key, idx, display) in CometSearchKeys.Enumerate(comets))
        {
            AddCometKey(key, idx, display);
        }

        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. entries];
    }

    /// <summary>Close the modal. Keeps the info panel so the user still sees the selection.</summary>
    public static void CloseSearch(SkyMapSearchState search)
    {
        search.IsOpen = false;
        search.SearchInput.Deactivate();
    }

    /// <summary>
    /// Resolve the search results for <paramref name="query"/> from the catalog + comet index. With
    /// Tycho-2 in the catalog the index is ~2.5M entries; we exploit the fact that
    /// <see cref="ICelestialObjectDB.CreateAutoCompleteList"/> returns its entries sorted
    /// ordinal-ignore-case to binary-search the prefix range in O(log N), then scan the contiguous prefix
    /// run for matches. A substring fallback runs only when the prefix scan returns nothing, keeping the
    /// steady-state hot path off the full-array scan. Pure with respect to <paramref name="search"/> (reads
    /// its index + comet map, returns the array) so it can back
    /// <see cref="SkyMapSearchInteraction.Query"/>; the shared <see cref="DIR.Lib.SearchInteraction{TResult}"/>
    /// owns the result list, selected index, and scroll reset.
    /// </summary>
    public static ImmutableArray<SkyMapSearchResult> FilterResults(SkyMapSearchState search, ICelestialObjectDB db, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return [];
        }

        // Virtual TYC path: the ~2.5M Tycho-2 stars deliberately don't appear in
        // the autocomplete list (would balloon the sort to ~5M entries with ~120MB
        // of string allocations). Instead, queries that look like "TYC <digits>..."
        // are served by a direct byte[] walk over the catalogue, which decodes a
        // small destination buffer on-the-fly without materialising any noise stars.
        if (TryHandleTycPrefix(db, query, out var tycResults))
        {
            return tycResults;
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
            // A real catalog object wins a name tie; a comet-only string (designation or common name)
            // resolves through the comet map (comets are ephemeris-computed, not in the object DB).
            if (TryResolveToObject(db, entry, out var obj))
            {
                if (!seenIndices.Add(obj.Index)) continue;
                results.Add(new SkyMapSearchResult(
                    Display: entry,
                    Index: obj.Index,
                    ObjType: obj.ObjectType,
                    VMag: (float)obj.V_Mag));
            }
            else if (search.CometEntries.TryGetValue(entry, out var cometEntry))
            {
                if (!seenIndices.Add(cometEntry.Index)) continue;
                // Show the full "designation (common name)" label whichever key matched; VMag is
                // time-dependent for a comet, so it's left NaN in the list and resolved live on commit.
                results.Add(new SkyMapSearchResult(
                    Display: cometEntry.Display,
                    Index: cometEntry.Index,
                    ObjType: ObjectType.Comet,
                    VMag: float.NaN));
            }
        }

        return results.ToImmutable();
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
    /// Returns true when the query was TYC-shaped (results in <paramref name="results"/>);
    /// false (with empty <paramref name="results"/>) to let the caller continue with the
    /// general autocomplete-list scan.
    /// </summary>
    private static bool TryHandleTycPrefix(ICelestialObjectDB db, string query, out ImmutableArray<SkyMapSearchResult> results)
    {
        results = [];
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
        var builder = ImmutableArray.CreateBuilder<SkyMapSearchResult>(take);
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
            builder.Add(new SkyMapSearchResult(
                Display: display,
                Index: idx,
                ObjType: ObjectType.Star,
                VMag: m.VMag));
        }

        results = builder.ToImmutable();
        return true;
    }

    /// <summary>
    /// Commit a chosen <paramref name="result"/>: slew the sky map to the object, populate the info panel,
    /// and close the modal. Returns true on success. The caller (the commit-signal handler) resolves the
    /// result from the search interaction's highlighted row; taking it explicitly keeps this helper
    /// directly testable without an interaction.
    /// </summary>
    public static bool CommitResult(
        SkyMapSearchState search,
        SkyMapState skyMap,
        ICelestialObjectDB db,
        SkyMapSearchResult result,
        double siteLat, double siteLon,
        DateTimeOffset viewingUtc,
        in SiteContext site,
        ICometRepository? comets = null)
    {
        if (result.Index is not { } catIdx) return false;

        // Comet: resolve the LIVE ephemeris position + magnitude (it is not in the object DB), slew there,
        // and build the comet info panel (the sparkline is drawn from the state cache in the panel).
        if (catIdx.ToCatalog() == Catalog.Comet && comets is not null)
        {
            if (!comets.TryGetPosition(catIdx, viewingUtc, out var cometRa, out var cometDec, out var cometMag))
            {
                return false;
            }
            SlewTo(skyMap, cometRa, cometDec);
            search.InfoPanel = CometInfoPanel(comets, catIdx, cometRa, cometDec, cometMag, siteLat, siteLon, viewingUtc, site);
            CloseSearch(search);
            return true;
        }

        if (!db.TryLookupByIndex(catIdx, out var obj)) return false;

        if (double.IsNaN(obj.RA) || double.IsNaN(obj.Dec))
        {
            // Solar-system bodies (Sun / Moon / planets) carry NaN catalog coords -- their position
            // is ephemeris-computed. Resolve the LIVE position from the planet cache (the same source
            // the sky map renders from, keyed on the same viewing time) and commit to that, so e.g.
            // searching "Jupiter" + Enter actually slews there instead of doing nothing. Bodies not in
            // the cache (VSOP87a reduction failed for this instant) still can't commit.
            foreach (var (planetIdx, pRa, pDec) in skyMap.GetPlanetPositionsCached(viewingUtc))
            {
                if (planetIdx == catIdx)
                {
                    SlewTo(skyMap, pRa, pDec);
                    search.InfoPanel = PlanetInfoPanel(db, catIdx, pRa, pDec, siteLat, siteLon, viewingUtc, site);
                    CloseSearch(search);
                    return true;
                }
            }
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
    /// Resolve a sky-map click at a screen pixel to the nearest catalog object / planet / comet and
    /// populate <see cref="SkyMapSearchState.InfoPanel"/>, deriving the viewport projection
    /// (pixels-per-radian, centre) from the tab's <see cref="SkyMapState.LastContentRect"/> and the
    /// pinned-target set from the planner proposals. This is the boilerplate the desktop
    /// <c>AppSignalHandler</c> and the browser <c>Planner</c> both need around
    /// <see cref="SelectObjectByClick"/> — hoisted here so the two go through ONE path (the caller
    /// supplies only <paramref name="viewingUtc"/>, computed identically on both as
    /// <c>(PlanningDate ?? now) + sky-map scrub offset</c>). Ctrl in <paramref name="modifiers"/>
    /// forces a point-source pick (a star under an enclosing DSO ellipse). Returns true when
    /// something was selected.
    /// </summary>
    public static bool SelectAtScreenPoint(
        SkyMapState skyMap,
        ICelestialObjectDB db,
        double siteLat, double siteLon,
        DateTimeOffset viewingUtc,
        float screenX, float screenY,
        InputModifier modifiers,
        ImmutableArray<ProposedObservation> proposals,
        ICometRepository? comets = null)
    {
        var rect = skyMap.LastContentRect;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        var ppr = SkyMapProjection.PixelsPerRadian(rect.Height, skyMap.FieldOfViewDeg);
        var cx = rect.X + rect.Width * 0.5f;
        var cy = rect.Y + rect.Height * 0.5f;
        var site = SiteContext.Create(siteLat, siteLon, viewingUtc);

        return SelectObjectByClick(
            skyMap.Search, skyMap, db,
            siteLat, siteLon, viewingUtc, site,
            screenX, screenY,
            skyMap.CurrentViewMatrix, ppr, cx, cy,
            preferPointSource: (modifiers & InputModifier.Ctrl) != 0,
            pinnedCatalogIndices: PlannerActions.GetPinnedCatalogIndices(proposals),
            comets: comets);
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
        IReadOnlySet<CatalogIndex>? pinnedCatalogIndices = null,
        ICometRepository? comets = null)
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

        // Planets (Sun / Moon / major planets) are ephemeris-computed, so they are NOT in the
        // fixed-position DSO/star spatial grids the passes above search. Hit-test the live planet
        // positions directly -- the same cache the renderer's DrawPlanetLabels draws from, keyed on
        // the same viewing time -- so a click on a planet dot resolves to it. A planet wins when it
        // is the closest hit within tolerance: it is a prominent target and its live position is
        // exactly what the user clicked. Built via FromPosition because the catalog entry's stored
        // RA/Dec is not the live ephemeris position.
        var bestPlanetDistSq = double.MaxValue;
        CatalogIndex? bestPlanetIdx = null;
        double bestPlanetRa = 0.0, bestPlanetDec = 0.0;
        foreach (var (planetIdx, pRa, pDec) in skyMap.GetPlanetPositionsCached(viewingUtc))
        {
            if (!SkyMapProjection.ProjectWithMatrix(pRa, pDec, viewMatrix, pixelsPerRadian, centerX, centerY,
                    out var sx, out var sy))
            {
                continue;
            }

            var dx = sx - clickScreenX;
            var dy = sy - clickScreenY;
            var distSq = dx * dx + dy * dy;
            if (distSq <= ClickToleranceScreenPx * ClickToleranceScreenPx && distSq < bestPlanetDistSq)
            {
                bestPlanetDistSq = distSq;
                bestPlanetIdx = planetIdx;
                bestPlanetRa = pRa;
                bestPlanetDec = pDec;
            }
        }

        // Comets (also ephemeris-computed, not in the spatial grids) — same hit-test as planets, over the
        // live comet marker cache filtered to the same zoom-aware magnitude limit the renderer draws with.
        var bestCometDistSq = double.MaxValue;
        SkyMapState.CometMarker? bestComet = null;
        if (comets is not null)
        {
            var cometLimit = Math.Max(SkyMapState.CometBaseMagnitudeLimit, skyMap.EffectiveMagnitudeLimit);
            foreach (var m in skyMap.GetCometPositionsCached(comets, viewingUtc))
            {
                if (m.VMag > cometLimit) continue;
                if (!SkyMapProjection.ProjectWithMatrix(m.RA, m.Dec, viewMatrix, pixelsPerRadian, centerX, centerY,
                        out var sx, out var sy))
                {
                    continue;
                }
                var dx = sx - clickScreenX;
                var dy = sy - clickScreenY;
                var distSq = dx * dx + dy * dy;
                if (distSq <= ClickToleranceScreenPx * ClickToleranceScreenPx && distSq < bestCometDistSq)
                {
                    bestCometDistSq = distSq;
                    bestComet = m;
                }
            }
        }

        // Resolve the nearest hit across catalog objects, planets, and comets. A planet / comet wins on a
        // tie so a prominent moving body under a faint field star is preferred (matches its own dot being
        // exactly what the user clicked).
        var catalogDistSq = bestIdx is null ? double.MaxValue : bestDistSq;

        if (bestComet is { } cm && bestCometDistSq <= catalogDistSq && bestCometDistSq <= bestPlanetDistSq && comets is not null)
        {
            search.InfoPanel = CometInfoPanel(comets, cm.Index, cm.RA, cm.Dec, cm.VMag, siteLat, siteLon, viewingUtc, site);
            return true;
        }

        if (bestPlanetIdx is { } pIdx && bestPlanetDistSq <= catalogDistSq)
        {
            search.InfoPanel = PlanetInfoPanel(db, pIdx, bestPlanetRa, bestPlanetDec, siteLat, siteLon, viewingUtc, site);
            return true;
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

    /// <summary>
    /// Builds an info panel for a solar-system body: the LIVE ephemeris RA/Dec, the planet's
    /// PREDEFINED catalog metadata (<see cref="ObjectType.Planet"/>, reference magnitude, name), and
    /// the constellation it is CURRENTLY in -- computed from the live position via
    /// <see cref="ConstellationBoundary.TryFindConstellation(double, double, out Constellation)"/>,
    /// since planets wander and have no fixed constellation in the catalog. Falls back to a bare named
    /// position when the catalog has no entry for the index (e.g. a minimal test DB).
    /// </summary>
    // internal (not private) so the per-frame info-panel redraw can rebuild a selected planet's
    // panel from its LIVE position as the viewing time advances -- see SkyMapTab.DrawSearchAndInfoPanel.
    internal static SkyMapInfoPanelData PlanetInfoPanel(
        ICelestialObjectDB db, CatalogIndex planetIdx, double raHours, double decDeg,
        double siteLat, double siteLon, DateTimeOffset viewingUtc, in SiteContext site)
    {
        var constellation = ConstellationBoundary.TryFindConstellation(raHours, decDeg, out var c)
            ? c
            : default;

        if (db.TryLookupByIndex(planetIdx, out var obj))
        {
            return SkyMapInfoPanelData.FromPosition(
                obj.DisplayName, raHours, decDeg, siteLat, siteLon, viewingUtc, site)
                with
                {
                    ObjType = obj.ObjectType,
                    VMag = (float)obj.V_Mag,
                    BMinusV = (float)obj.BMinusV,
                    Constellation = constellation,
                    Index = planetIdx,
                };
        }

        // No catalog entry (minimal DB / tests): a bare named position, still tagged with the
        // current constellation.
        var name = planetIdx == CatalogIndex.Moon ? "Moon"
            : planetIdx == CatalogIndex.Sol ? "Sun"
            : planetIdx.ToCanonical();
        return SkyMapInfoPanelData.FromPosition(name, raHours, decDeg, siteLat, siteLon, viewingUtc, site)
            with { Constellation = constellation };
    }

    /// <summary>
    /// Builds an info panel for a comet: its LIVE ephemeris RA/Dec + predicted magnitude, the
    /// <see cref="ObjectType.Comet"/> tag, the canonical designation, the common name (folded into the
    /// display title when SBDB has one), and the constellation the comet is CURRENTLY in (comets wander,
    /// so the constellation is computed from the live position, exactly like a planet). The vmag sparkline
    /// is drawn separately from the state-cached curve, so it is not carried on the panel struct.
    /// </summary>
    // internal so the per-frame info-panel redraw can rebuild a selected comet's panel from its LIVE
    // position as the viewing time advances -- see SkyMapTab.DrawSearchAndInfoPanel.
    internal static SkyMapInfoPanelData CometInfoPanel(
        ICometRepository comets, CatalogIndex idx,
        double raHours, double decDeg, double mag,
        double siteLat, double siteLon, DateTimeOffset viewingUtc, in SiteContext site)
    {
        var canonical = idx.ToCanonical();
        var name = comets.TryGet(idx, out var el) ? el.DisplayName : canonical;
        var constellation = ConstellationBoundary.TryFindConstellation(raHours, decDeg, out var c) ? c : default;

        return SkyMapInfoPanelData.FromPosition(name, raHours, decDeg, siteLat, siteLon, viewingUtc, site)
            with
            {
                Canonical = canonical,
                ObjType = ObjectType.Comet,
                VMag = (float)mag,
                Constellation = constellation,
                Index = idx,
            };
    }

    private static CelestialObjectShape? ResolveShape(ICelestialObjectDB db, CatalogIndex idx)
        => db.TryGetShape(idx, out var shape) ? shape : null;

    private static void SlewTo(SkyMapState skyMap, double raHours, double decDeg)
        => SkyMapViewActions.CenterOn(skyMap, raHours, decDeg);
}
