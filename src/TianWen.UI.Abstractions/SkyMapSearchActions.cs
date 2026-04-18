using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// <see cref="SkyMapSearchState.SearchInput"/> text. Same fuzzy-match rules
    /// as the planner autocomplete (exact &gt; prefix &gt; substring).
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

        var index = search.SearchIndex;
        var candidates = new List<(string Entry, int Score)>(capacity: MaxResults * 4);
        var minScore = 0;

        for (var i = 0; i < index.Length; i++)
        {
            var entry = index[i];
            var score = FuzzyMatchScore(query, entry);
            if (score <= minScore) continue;

            candidates.Add((entry, score));
            if (candidates.Count >= MaxResults * 4)
            {
                candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
                candidates.RemoveRange(MaxResults, candidates.Count - MaxResults);
                minScore = candidates[^1].Score;
            }
        }

        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        var results = ImmutableArray.CreateBuilder<SkyMapSearchResult>(Math.Min(candidates.Count, MaxResults));
        var seenIndices = new HashSet<CatalogIndex>();
        var take = Math.Min(candidates.Count, MaxResults);
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
            ResolveAngularSize(db, catIdx));

        CloseSearch(search);
        return true;
    }

    /// <summary>
    /// Click on the sky map — project the click back to RA/Dec, find the nearest
    /// catalog object within <see cref="ClickToleranceScreenPx"/>, populate the
    /// info panel. Returns true when an object was matched. DSOs are preferred
    /// over stars at equal distance (so clicking M31's halo picks the galaxy,
    /// not the nearest faint Tycho-2 star).
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
        double pixelsPerRadian, float centerX, float centerY)
    {
        var (clickRa, clickDec) = SkyMapProjection.UnprojectWithMatrix(
            clickScreenX, clickScreenY, viewMatrix, pixelsPerRadian, centerX, centerY);

        CatalogIndex? bestIdx = null;
        var bestDistSq = ClickToleranceScreenPx * ClickToleranceScreenPx;
        var bestIsDso = false;

        // DSOs first (pref on ties). DeepSkyCoordinateGrid excludes Tycho-2 so this is cheap.
        foreach (var idx in db.DeepSkyCoordinateGrid[clickRa, clickDec])
        {
            if (!db.TryLookupByIndex(idx, out var o)) continue;
            if (double.IsNaN(o.RA) || double.IsNaN(o.Dec)) continue;
            if (!SkyMapProjection.ProjectWithMatrix(o.RA, o.Dec, viewMatrix, pixelsPerRadian, centerX, centerY,
                    out var sx, out var sy))
            {
                continue;
            }

            var dx = sx - clickScreenX;
            var dy = sy - clickScreenY;
            var distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = idx;
                bestIsDso = true;
            }
        }

        // Stars — only if no DSO matched (the full grid iterates Tycho-2, which
        // would swamp any DSO in dense fields). Same 20 px tolerance.
        if (!bestIsDso)
        {
            foreach (var idx in db.CoordinateGrid[clickRa, clickDec])
            {
                if (!db.TryLookupByIndex(idx, out var o)) continue;
                if (double.IsNaN(o.RA) || double.IsNaN(o.Dec)) continue;
                if (!SkyMapProjection.ProjectWithMatrix(o.RA, o.Dec, viewMatrix, pixelsPerRadian, centerX, centerY,
                        out var sx, out var sy))
                {
                    continue;
                }

                var dx = sx - clickScreenX;
                var dy = sy - clickScreenY;
                var distSq = dx * dx + dy * dy;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIdx = idx;
                }
            }
        }

        if (bestIdx is not { } hit || !db.TryLookupByIndex(hit, out var obj))
        {
            return false;
        }

        search.InfoPanel = SkyMapInfoPanelData.FromCatalogObject(
            obj, siteLat, siteLon, viewingUtc, site,
            ResolveAngularSize(db, hit));
        return true;
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

    private static double? ResolveAngularSize(ICelestialObjectDB db, CatalogIndex idx)
    {
        if (!db.TryGetShape(idx, out var shape)) return null;
        var majorArcmin = (double)shape.MajorAxis;
        if (double.IsNaN(majorArcmin) || majorArcmin <= 0) return null;
        return majorArcmin / 60.0;
    }

    private static int FuzzyMatchScore(string query, string entry)
    {
        if (entry.Equals(query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (entry.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 80;
        if (entry.Contains(query, StringComparison.OrdinalIgnoreCase)) return 40;
        return 0;
    }

    private static void SlewTo(SkyMapState skyMap, double raHours, double decDeg)
    {
        skyMap.CenterRA = raHours;
        skyMap.CenterDec = decDeg;
        skyMap.NormalizeCenter();
        skyMap.NeedsRedraw = true;
    }
}
