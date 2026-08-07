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

        // Only the spellings that identify ONE comet go in the 1:1 map. A bare common name is SBDB's
        // discoverer field, shared by 3,563 of 4,069 comets ("Tempel" is eight, "SOHO" is 1,465), so
        // putting it here meant TryAdd kept the first and silently swallowed the rest -- searching
        // "Tempel" offered exactly one comet and 10P was reachable only by typing its designation.
        // Ambiguous names are matched by ShortlistComets below instead, which can return all of them.
        var aliases = ImmutableArray.CreateBuilder<(string Alias, CatalogIndex Index, string Display)>();
        foreach (var el in comets?.All ?? [])
        {
            if (el.CatalogIndex is not { } idx)
            {
                continue;
            }

            var canonical = el.Designation.ToCanonical();
            var display = el.DisplayName;
            AddCometKey(canonical, idx, display);
            if (el.CommonName is { Length: > 0 } commonName)
            {
                AddCometKey($"{canonical} ({commonName})", idx, display);
                AddCometKey($"{canonical}/{commonName}", idx, display);
                aliases.Add((commonName, idx, display));
            }
        }
        search.CometAliases = aliases.DrainToImmutable();

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
        ScanPrefix(index, query, candidates);

        // 1a. The index stores each designation in the catalog's OWN canonical spelling, and this
        //     scan is ORDINAL, so a query typed with different separators cannot prefix-match:
        //     "NGC7000" never reaches the stored "NGC 7000", and the substring fallback at step 2
        //     cannot rescue it either, since neither string contains the other. Re-scan using the
        //     canonical spellings of whatever the query parses to.
        //
        //     Parse with TryGetCleanedUpCatalogName rather than normalising here, because the
        //     separator is PER CATALOG and not guessable from the string: ToCanonical gives NGC a
        //     space ("NGC 7000"), Messier none ("M31"), Sharpless a hyphen ("Sh2-155"). Anything
        //     hand-rolled here would re-encode those rules and drift from the parser the moment one
        //     changed. It is also the same method the commit and deep-link paths already resolve
        //     through, which is why THEY always accepted "NGC7000" while the list did not; the two
        //     now agree by construction.
        //
        //     Partial numbers keep working: "NGC700" parses to NGC 700, whose canonical prefix-matches
        //     NGC 700, NGC 7000, NGC 7001 and so on. A query that is not a designation at all (a
        //     common name) simply fails to parse and keeps whatever the raw scan found.
        if (CatalogUtils.TryGetCleanedUpCatalogName(query, out var designation))
        {
            var normal = designation.ToCanonical(CanonicalFormat.Normal);
            if (!normal.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                ScanPrefix(index, normal, candidates);
            }

            var alternative = designation.ToCanonical(CanonicalFormat.Alternative);
            if (!alternative.Equals(query, StringComparison.OrdinalIgnoreCase)
                && !alternative.Equals(normal, StringComparison.Ordinal))
            {
                ScanPrefix(index, alternative, candidates);
            }
        }

        // 1b. Comet common names, ALWAYS, because they cannot be served by the shared index: a name
        //     like "Tempel" belongs to eight comets and the index holds one string per entry. Bounded
        //     by the comet count (~4 K, and only those with a name), so it is nothing against the
        //     2.5 M-entry catalog scan it sits beside -- and it must not be conditional on the prefix
        //     scan coming up empty the way step 2 is, or a single unrelated catalog entry starting
        //     with the same letters would hide every comet the user was actually searching for.
        //     Emits the DISPLAY string, so the resolve below finds it in CometEntries unchanged.
        foreach (var (alias, _, display) in search.CometAliases)
        {
            var score = alias.Length == query.Length && alias.Equals(query, StringComparison.OrdinalIgnoreCase) ? 100
                : alias.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 90
                : alias.Contains(query, StringComparison.OrdinalIgnoreCase) ? 40
                : 0;
            if (score > 0)
            {
                candidates.Add((display, score));
            }
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

        // Fill up to MaxResults *results*, rather than walking a fixed MaxResults *candidates*:
        // several entries can resolve to one object (a canonical designation, its alternative
        // spelling and a common name all name the same thing, and now the raw and space-inserted
        // scans can each contribute one), and seenIndices drops the repeats. Capping the walk
        // instead of the output let those duplicates eat visible rows.
        var results = ImmutableArray.CreateBuilder<SkyMapSearchResult>(Math.Min(candidates.Count, MaxResults));
        var seenIndices = new HashSet<CatalogIndex>();
        for (var i = 0; i < candidates.Count && results.Count < MaxResults; i++)
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
    /// Selects the object named by a single token, the way a deep link or a restored session does:
    /// resolve, centre the view on it, and populate the info panel, exactly as if the user had
    /// searched for it and pressed Enter.
    ///
    /// <para>Resolution order matters. A COMET is tried first, through
    /// <see cref="CometSearchKeys.TryResolve"/>, because comets are not in the object DB and a
    /// designation like <c>10P</c> would otherwise fall through to a catalog lookup that answers
    /// nothing. Everything else goes through the same catalog resolve the search list uses, so a
    /// canonical index, a common name and a Messier number all work.</para>
    ///
    /// <para>Deliberately does NOT need the search index built: a deep link arrives before the user
    /// has ever opened the search modal, and building the ~200 K-entry index to resolve one string
    /// would make the first paint wait on it.</para>
    /// </summary>
    public static bool TrySelectByToken(
        SkyMapSearchState search,
        SkyMapState skyMap,
        ICelestialObjectDB db,
        string token,
        double siteLat, double siteLon,
        DateTimeOffset viewingUtc,
        in SiteContext site,
        ICometRepository? comets = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var trimmed = token.Trim();
        if (CometSearchKeys.TryResolve(comets, trimmed, out var cometIndex, out var cometDisplay))
        {
            return CommitResult(search, skyMap, db,
                new SkyMapSearchResult(cometDisplay, cometIndex, ObjectType.Comet, float.NaN),
                siteLat, siteLon, viewingUtc, site, comets);
        }

        if (TryResolveToObject(db, trimmed, out var obj))
        {
            return CommitResult(search, skyMap, db,
                new SkyMapSearchResult(trimmed, obj.Index, obj.ObjectType, (float)obj.V_Mag),
                siteLat, siteLon, viewingUtc, site, comets);
        }

        return false;
    }

    /// <summary>
    /// Collect every entry in the sorted <paramref name="index"/> that starts with
    /// <paramref name="query"/>, scored. Binary-searches to the first entry &gt;= the query then walks
    /// the contiguous prefix run, so it is O(log N) plus O(matches). Appends to
    /// <paramref name="candidates"/> rather than returning, because it is run more than once per
    /// query (see the space-inserted second pass in <see cref="FilterResults"/>).
    /// </summary>
    private static void ScanPrefix(ImmutableArray<string> index, string query, List<(string Entry, int Score)> candidates)
    {
        for (var i = LowerBound(index, query); i < index.Length; i++)
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
