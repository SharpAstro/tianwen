using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Actions for the observation planner. Shared between TUI and GUI.
/// </summary>
public static class PlannerActions
{
    /// <summary>
    /// Shifts the planning date by the given number of days (+1 = tomorrow, -1 = yesterday).
    /// Sets <see cref="PlannerState.PlanningDate"/> and flags for recomputation.
    /// </summary>
    public static void ShiftPlanningDate(PlannerState state, TimeProvider timeProvider, int days)
    {
        var current = state.PlanningDate ?? timeProvider.GetLocalNow();
        state.PlanningDate = current.AddDays(days);
        state.NeedsRecompute = true;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Resets planning to tonight (current date).
    /// </summary>
    public static void ResetPlanningDate(PlannerState state)
    {
        state.PlanningDate = null;
        state.NeedsRecompute = true;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Fast path: recomputes only the night window, twilight boundaries, altitude profiles,
    /// and scores for the existing target list. Does NOT rescan the catalog.
    /// Used when changing the planning date.
    /// </summary>
    public static void RecomputeForDate(PlannerState state, Transform transform)
    {
        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        state.AstroDark = astroDark;
        state.AstroTwilight = astroTwilight;

        ComputeTwilightBoundaries(state, transform, astroDark, astroTwilight);

        // Invalidate cached grid (date changed) and recompute
        state.CachedAstromGrid = null;
        var (astroms, times) = EnsureAstromGrid(state, transform);

        // Re-score all existing targets using the shared grids
        var rescored = new List<ScoredTarget>();
        state.AltitudeProfiles.Clear();

        foreach (var old in state.TonightsBest)
        {
            var scored = ObservationScheduler.ScoreTarget(old.Target, astroms, times,
                astroDark, astroTwilight, state.MinHeightAboveHorizon, transform.SiteLongitude);
            rescored.Add(scored);
            state.ScoredTargets[old.Target] = scored;
            state.AltitudeProfiles[old.Target] = ComputeFineAltitudeProfileFast(
                old.Target, astroms, times, transform.SiteLatitude, transform.SiteLongitude);
        }

        // Also recompute profiles for search results / proposals not in TonightsBest
        foreach (var s in state.SearchResults)
        {
            if (!state.AltitudeProfiles.ContainsKey(s.Target))
            {
                var scored = ObservationScheduler.ScoreTarget(s.Target, astroms, times,
                    astroDark, astroTwilight, state.MinHeightAboveHorizon, transform.SiteLongitude);
                state.ScoredTargets[s.Target] = scored;
                state.AltitudeProfiles[s.Target] = ComputeFineAltitudeProfileFast(
                    s.Target, astroms, times, transform.SiteLatitude, transform.SiteLongitude);
            }
        }

        // Sort by score descending (same as TonightsBest ordering)
        rescored.Sort((a, b) => b.TotalScore.CompareTo(a.TotalScore));
        state.TonightsBest = rescored;

        RecomputeHandoffSliders(state);
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Computes tonight's best targets and populates the planner state with
    /// ranked targets, night window, twilight boundaries, and altitude profiles.
    /// </summary>
    public static async Task ComputeTonightsBestAsync(
        PlannerState state,
        ICelestialObjectDB objectDb,
        Transform transform,
        byte minHeightAboveHorizon,
        CancellationToken ct,
        Action<string>? onProgress = null)
    {
        void Report(string msg)
        {
            state.StatusMessage = msg;
            state.NeedsRedraw = true;
            onProgress?.Invoke(msg);
        }

        Report("Computing night window...");

        var (astroDark, astroTwilight) = ObservationScheduler.CalculateNightWindow(transform);
        state.AstroDark = astroDark;
        state.AstroTwilight = astroTwilight;
        state.MinHeightAboveHorizon = minHeightAboveHorizon;

        // Compute twilight boundaries
        ComputeTwilightBoundaries(state, transform, astroDark, astroTwilight);

        Report("Scanning catalog for tonight's best targets...");

        await objectDb.InitDBAsync(ct);

        var tonightsBest = ObservationScheduler.TonightsBest(objectDb, transform, minHeightAboveHorizon)
            .Take(100)
            .ToList();

        state.TonightsBest = tonightsBest;

        // Score all targets for elevation profiles
        state.ScoredTargets.Clear();
        foreach (var scored in tonightsBest)
        {
            state.ScoredTargets[scored.Target] = scored;
        }

        // Compute fine altitude profiles and cross-index aliases for the top targets
        Report("Computing altitude profiles...");

        // Invalidate cached grid (site or date may have changed)
        state.CachedAstromGrid = null;
        var (gridAstroms, gridTimes) = EnsureAstromGrid(state, transform);

        state.AltitudeProfiles.Clear();
        state.TargetAliases.Clear();
        foreach (var scored in tonightsBest)
        {
            state.AltitudeProfiles[scored.Target] = ComputeFineAltitudeProfileFast(
                scored.Target, gridAstroms, gridTimes,
                transform.SiteLatitude, transform.SiteLongitude);
            PopulateTargetAlias(state, objectDb, scored.Target);
        }

        // Recompute handoff sliders for any existing proposals
        RecomputeHandoffSliders(state);

        Report("");
        state.StatusMessage = null;
    }

    /// <summary>
    /// Returns the filtered target list: applies the star rating filter but always
    /// includes proposed targets regardless of rating.
    /// </summary>
    public static IReadOnlyList<ScoredTarget> GetFilteredTargets(PlannerState state)
    {
        var proposedTargets = new HashSet<Target>(state.Proposals.Select(p => p.Target));
        var searchTargets = new HashSet<Target>(state.SearchResults.Select(s => s.Target));

        var maxScore = state.TonightsBest.Count > 0 ? state.TonightsBest[0].CombinedScore : 1.0;

        var result = new List<ScoredTarget>();
        var seen = new HashSet<Target>();

        // Pinned targets first, sorted by peak time ascending
        var pinnedScored = new List<ScoredTarget>();
        foreach (var p in state.Proposals)
        {
            // Look up the scored target from TonightsBest, SearchResults, or ScoredTargets
            var scored = state.TonightsBest.FirstOrDefault(s => s.Target == p.Target);
            if (scored.Target == default)
            {
                scored = state.SearchResults.FirstOrDefault(s => s.Target == p.Target);
            }
            if (scored.Target == default && state.ScoredTargets.TryGetValue(p.Target, out var fromCache))
            {
                scored = fromCache;
            }
            if (scored.Target != default)
            {
                pinnedScored.Add(scored);
            }
        }

        // Sort pinned by actual peak altitude time (not OptimalStart, which is the
        // start of the viable window — misleading for circumpolar targets)
        pinnedScored.Sort((a, b) =>
        {
            var peakA = state.AltitudeProfiles.TryGetValue(a.Target, out var profA) && profA.Count > 0
                ? profA.MaxBy(p => p.Alt).Time : a.OptimalStart;
            var peakB = state.AltitudeProfiles.TryGetValue(b.Target, out var profB) && profB.Count > 0
                ? profB.MaxBy(p => p.Alt).Time : b.OptimalStart;
            return peakA.CompareTo(peakB);
        });

        foreach (var s in pinnedScored)
        {
            if (seen.Add(s.Target))
            {
                result.Add(s);
            }
        }

        // Track where pinned section ends (for rendering separator)
        state.PinnedCount = result.Count;

        // Unpinned: tonight's best filtered by rating, excluding already-pinned
        foreach (var s in state.TonightsBest)
        {
            if (seen.Contains(s.Target))
            {
                continue;
            }

            var passesFilter = state.MinRatingFilter <= 0f
                || searchTargets.Contains(s.Target)
                || ScoreToRating(s.CombinedScore, maxScore) >= state.MinRatingFilter;

            if (passesFilter && seen.Add(s.Target))
            {
                result.Add(s);
            }
        }

        // Append search results that aren't already shown
        foreach (var s in state.SearchResults)
        {
            if (seen.Add(s.Target))
            {
                result.Add(s);
            }
        }

        return result;
    }

    /// <summary>
    /// Cycles the rating filter: All → 3★+ → 4★+ → All.
    /// </summary>
    public static void CycleRatingFilter(PlannerState state)
    {
        state.MinRatingFilter = state.MinRatingFilter switch
        {
            0f => 3f,
            3f => 4f,
            _ => 0f
        };
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Steps the currently selected handoff slider by <paramref name="minutesDelta"/> minutes.
    /// Clamps to maintain 15-minute minimum gaps between adjacent sliders (or dark/twilight boundaries).
    /// Returns true if the slider was moved.
    /// </summary>
    public static bool StepSelectedSlider(PlannerState state, int minutesDelta)
    {
        var idx = state.SelectedSliderIndex;
        if (idx < 0 || idx >= state.HandoffSliders.Count)
        {
            return false;
        }

        var newTime = state.HandoffSliders[idx] + TimeSpan.FromMinutes(minutesDelta);
        ClampSlider(state, idx, ref newTime);

        state.HandoffSliders[idx] = newTime;
        state.IsDirty = true;
        state.NeedsRedraw = true;
        return true;
    }

    /// <summary>
    /// Moves a handoff slider to an absolute time, clamped to maintain 15-minute gaps.
    /// Used by mouse drag in both GUI and TUI.
    /// </summary>
    public static void MoveSlider(PlannerState state, int idx, DateTimeOffset newTime)
    {
        if (idx < 0 || idx >= state.HandoffSliders.Count)
        {
            return;
        }

        ClampSlider(state, idx, ref newTime);

        state.HandoffSliders[idx] = newTime;
        state.IsDirty = true;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Sets the selected slider index. Pass -1 to deselect (confirms current position).
    /// Saves the original time on selection for Escape-to-revert.
    /// </summary>
    public static void SelectSlider(PlannerState state, int index)
    {
        if (index >= 0 && index < state.HandoffSliders.Count)
        {
            state.SelectedSliderOriginalTime = state.HandoffSliders[index];
        }
        else
        {
            state.SelectedSliderOriginalTime = null;
        }
        state.SelectedSliderIndex = index;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Cycles the selected slider index forward: -1 → 0 → 1 → ... → -1.
    /// Returns true if there are sliders to cycle through.
    /// </summary>
    public static bool CycleSelectedSlider(PlannerState state)
    {
        if (state.HandoffSliders.Count == 0)
        {
            return false;
        }

        state.SelectedSliderIndex =
            state.SelectedSliderIndex < state.HandoffSliders.Count - 1
                ? state.SelectedSliderIndex + 1
                : -1;
        state.NeedsRedraw = true;
        return true;
    }

    /// <summary>
    /// Handles slider-related keyboard input. Returns true if the key was consumed.
    /// Shared between GPU PlannerTab and TUI TuiPlannerTab.
    /// </summary>
    public static bool HandleSliderKeyboard(PlannerState state, InputKey key, InputModifier modifiers = default)
    {
        var fineStep = (modifiers & InputModifier.Shift) != 0;

        switch (key)
        {
            case InputKey.Left when state.SelectedSliderIndex >= 0:
                return StepSelectedSlider(state, fineStep ? -1 : -5);

            case InputKey.Right when state.SelectedSliderIndex >= 0:
                return StepSelectedSlider(state, fineStep ? 1 : 5);

            case InputKey.Enter when state.SelectedSliderIndex >= 0:
                // Confirm current position and deselect
                state.SelectedSliderOriginalTime = null;
                state.SelectedSliderIndex = -1;
                state.NeedsRedraw = true;
                return true;

            case InputKey.Escape when state.SelectedSliderIndex >= 0:
                // Revert to original position and deselect
                if (state.SelectedSliderOriginalTime is { } originalTime)
                {
                    var idx = state.SelectedSliderIndex;
                    if (idx >= 0 && idx < state.HandoffSliders.Count)
                    {
                        state.HandoffSliders[idx] = originalTime;
                    }
                }
                state.SelectedSliderOriginalTime = null;
                state.SelectedSliderIndex = -1;
                state.NeedsRedraw = true;
                return true;

            case InputKey.Tab:
                return CycleSelectedSlider(state);

            default:
                return false;
        }
    }

    /// <summary>
    /// Hit-tests a pixel X coordinate against handoff slider positions in the chart.
    /// Returns the index of the nearest slider within the plot area, or -1 if no sliders exist
    /// or the click is outside the plot area.
    /// </summary>
    public static int HitTestSlider(PlannerState state, float pixelX, float chartX, float chartW)
    {
        if (state.HandoffSliders.Count == 0)
        {
            return -1;
        }

        var (tStart, tEnd, plotX, plotW) = AltitudeChartRenderer.GetChartTimeLayout(state, (int)chartX, (int)chartW);

        // Click must be within the plot area
        if (pixelX < plotX || pixelX > plotX + plotW)
        {
            return -1;
        }

        var bestIdx = -1;
        var bestDist = double.MaxValue;

        for (var i = 0; i < state.HandoffSliders.Count; i++)
        {
            var fraction = (state.HandoffSliders[i] - tStart).TotalSeconds / (tEnd - tStart).TotalSeconds;
            var sliderPixelX = plotX + fraction * plotW;
            var dist = Math.Abs(pixelX - sliderPixelX);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private static void ClampSlider(PlannerState state, int idx, ref DateTimeOffset newTime)
    {
        var minSlot = TimeSpan.FromMinutes(15);

        var minTime = idx > 0 ? state.HandoffSliders[idx - 1] + minSlot : state.AstroDark + minSlot;
        var maxTime = idx < state.HandoffSliders.Count - 1
            ? state.HandoffSliders[idx + 1] - minSlot
            : state.AstroTwilight - minSlot;

        if (newTime < minTime) newTime = minTime;
        if (newTime > maxTime) newTime = maxTime;
    }

    /// <summary>
    /// Searches the catalog for targets matching the query and scores them.
    /// Results are stored in state.SearchResults and are exempt from filtering.
    /// </summary>
    /// <summary>
    /// Searches the catalog for targets matching the query. If the target is already
    /// in the list, selects it (resetting filter if needed). Otherwise adds it as a
    /// search result (exempt from rating filter).
    /// Returns the index in the filtered list to select, or -1.
    /// </summary>
    public static int SearchTargets(
        PlannerState state,
        ICelestialObjectDB objectDb,
        Transform transform,
        string query)
    {
        state.SearchResults.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            state.NeedsRedraw = true;
            return -1;
        }

        // Search catalog by fuzzy name match and catalog designation
        var catalogMatches = new List<CatalogIndex>();

        // Try catalog designation first (exact, e.g. "M31", "NGC 3132", "Messier 31")
        if (objectDb.TryLookupByIndex(query, out var directObj))
        {
            catalogMatches.Add(directObj.Index);
        }

        // Fuzzy search: match autocomplete entries by substring, then Levenshtein on common names
        var autoComplete = objectDb.CreateAutoCompleteList();
        var bestMatches = new List<(string Entry, int Score)>();

        foreach (var entry in autoComplete)
        {
            var score = FuzzyMatchScore(query, entry);
            if (score > 0)
            {
                bestMatches.Add((entry, score));
            }
        }

        // Levenshtein fallback on common names if no substring matches
        if (bestMatches.Count == 0)
        {
            foreach (var name in objectDb.CommonNames)
            {
                var dist = LevenshteinDistance(query, name);
                if (dist <= 3 && dist < name.Length / 2)
                {
                    bestMatches.Add((name, 100 - dist * 20));
                }
            }
        }

        bestMatches.Sort((a, b) => b.Score.CompareTo(a.Score));

        foreach (var (entry, _) in bestMatches.Take(10))
        {
            if (objectDb.TryResolveCommonName(entry, out var nameMatches))
            {
                foreach (var match in nameMatches)
                {
                    if (!catalogMatches.Contains(match))
                    {
                        catalogMatches.Add(match);
                    }
                }
            }
            else if (objectDb.TryLookupByIndex(entry, out var entryObj))
            {
                if (!catalogMatches.Contains(entryObj.Index))
                {
                    catalogMatches.Add(entryObj.Index);
                }
            }
        }

        foreach (var match in catalogMatches.Take(5))
        {
            double ra, dec;
            string name;

            if (objectDb.TryLookupByIndex(match, out var obj))
            {
                ra = obj.RA;
                dec = obj.Dec;

                // For solar system objects (NaN coordinates), compute position via VSOP87 ephemeris
                if (double.IsNaN(ra) || double.IsNaN(dec))
                {
                    if (!transform.TryGetOrbitalPositionRaDec(match, state.AstroDark, out ra, out dec))
                    {
                        continue;
                    }
                }

                name = obj.DisplayName;
            }
            else if (transform.TryGetOrbitalPositionRaDec(match, state.AstroDark, out ra, out dec))
            {
                // Planet/solar system object not in main DB — use query as name since
                // it came from the autocomplete list which contains the common name
                name = query;
            }
            else
            {
                continue;
            }

            var target = new Target(ra, dec, name, match);

            // If already in TonightsBest, select it there instead of re-adding
            var existingIdx = -1;
            for (var i = 0; i < state.TonightsBest.Count; i++)
            {
                if (state.TonightsBest[i].Target.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    existingIdx = i;
                    break;
                }
            }

            if (existingIdx >= 0)
            {
                state.MinRatingFilter = 0f;
                state.NeedsRedraw = true;
                var filtered = GetFilteredTargets(state);
                for (var j = 0; j < filtered.Count; j++)
                {
                    if (filtered[j].Target == state.TonightsBest[existingIdx].Target)
                    {
                        return j;
                    }
                }
                continue;
            }

            var scored = ObservationScheduler.ScoreTarget(target, transform,
                state.AstroDark, state.AstroTwilight, state.MinHeightAboveHorizon);

            if (!state.AltitudeProfiles.ContainsKey(target))
            {
                var (ga, gt) = EnsureAstromGrid(state, transform);
                state.AltitudeProfiles[target] = ComputeFineAltitudeProfileFast(
                    target, ga, gt, transform.SiteLatitude, transform.SiteLongitude);
            }

            state.ScoredTargets[target] = scored;
            state.SearchResults.Add(scored);
            PopulateTargetAlias(state, objectDb, target);
        }

        state.NeedsRedraw = true;

        // Return index of first search result in filtered list
        if (state.SearchResults.Count > 0)
        {
            var filtered = GetFilteredTargets(state);
            for (var i = 0; i < filtered.Count; i++)
            {
                if (filtered[i].Target == state.SearchResults[0].Target)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Resolves a single exact autocomplete entry (common name or catalog designation)
    /// and adds it as a search result, or selects it in TonightsBest if already present.
    /// Unlike <see cref="SearchTargets"/>, this does NOT re-search — it trusts the caller's selection.
    /// </summary>
    public static int CommitSuggestion(
        PlannerState state,
        ICelestialObjectDB objectDb,
        Transform transform,
        string suggestion)
    {
        state.SearchResults.Clear();

        // Resolve the suggestion to a CatalogIndex
        CatalogIndex? resolved = null;

        if (objectDb.TryResolveCommonName(suggestion, out var nameMatches) && nameMatches.Count > 0)
        {
            resolved = nameMatches[0];
        }
        else if (objectDb.TryLookupByIndex(suggestion, out var directObj))
        {
            resolved = directObj.Index;
        }

        if (resolved is not { } catIdx)
        {
            state.NeedsRedraw = true;
            return -1;
        }

        // Get RA/Dec — from DB or VSOP87 for planets
        double ra, dec;
        string name = suggestion;

        if (objectDb.TryLookupByIndex(catIdx, out var obj))
        {
            ra = obj.RA;
            dec = obj.Dec;
            name = obj.DisplayName;

            if (double.IsNaN(ra) || double.IsNaN(dec))
            {
                if (!transform.TryGetOrbitalPositionRaDec(catIdx, state.AstroDark, out ra, out dec))
                {
                    state.NeedsRedraw = true;
                    return -1;
                }
            }
        }
        else if (transform.TryGetOrbitalPositionRaDec(catIdx, state.AstroDark, out ra, out dec))
        {
            // Planet fallback
        }
        else
        {
            state.NeedsRedraw = true;
            return -1;
        }

        var target = new Target(ra, dec, name, catIdx);

        // Check if already in TonightsBest — select it there
        for (var i = 0; i < state.TonightsBest.Count; i++)
        {
            if (state.TonightsBest[i].Target.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                state.MinRatingFilter = 0f;
                state.NeedsRedraw = true;
                var filtered = GetFilteredTargets(state);
                for (var j = 0; j < filtered.Count; j++)
                {
                    if (filtered[j].Target == state.TonightsBest[i].Target)
                    {
                        return j;
                    }
                }
            }
        }

        // Not in TonightsBest — add as search result
        var scored = ObservationScheduler.ScoreTarget(target, transform,
            state.AstroDark, state.AstroTwilight, state.MinHeightAboveHorizon);

        if (!state.AltitudeProfiles.ContainsKey(target))
        {
            var (ga2, gt2) = EnsureAstromGrid(state, transform);
            state.AltitudeProfiles[target] = ComputeFineAltitudeProfileFast(
                target, ga2, gt2, transform.SiteLatitude, transform.SiteLongitude);
        }

        state.ScoredTargets[target] = scored;
        state.SearchResults.Add(scored);
        PopulateTargetAlias(state, objectDb, target);
        state.NeedsRedraw = true;

        var filteredList = GetFilteredTargets(state);
        for (var i = 0; i < filteredList.Count; i++)
        {
            if (filteredList[i].Target == target)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Updates the autocomplete suggestions based on the current search query.
    /// Runs synchronously — ~200K string comparisons takes ~2ms.
    /// </summary>
    public static void UpdateSuggestions(PlannerState state, string[] autoCompleteList, string query)
    {
        if (query == state.LastSuggestionQuery)
        {
            return;
        }

        state.LastSuggestionQuery = query;
        state.Suggestions.Clear();
        state.SuggestionIndex = -1;

        if (query.Length < 2)
        {
            state.NeedsRedraw = true;
            return;
        }

        var candidates = new List<(string Entry, int Score)>();
        var minScore = 0;

        for (var i = 0; i < autoCompleteList.Length; i++)
        {
            var entry = autoCompleteList[i];
            var score = FuzzyMatchScore(query, entry);
            if (score <= minScore)
            {
                continue;
            }

            candidates.Add((entry, score));

            // Once we have enough candidates, raise the bar to avoid sorting a huge list
            if (candidates.Count >= PlannerState.MaxSuggestions * 4)
            {
                candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
                candidates.RemoveRange(PlannerState.MaxSuggestions, candidates.Count - PlannerState.MaxSuggestions);
                minScore = candidates[^1].Score;
            }
        }

        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        var count = Math.Min(candidates.Count, PlannerState.MaxSuggestions);
        for (var i = 0; i < count; i++)
        {
            state.Suggestions.Add(candidates[i].Entry);
        }

        state.NeedsRedraw = true;
    }

    private static int FuzzyMatchScore(string query, string entry)
    {
        if (entry.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (entry.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (entry.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        return 0;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var ca = char.ToLowerInvariant(a[i - 1]);

            for (var j = 1; j <= b.Length; j++)
            {
                var cb = char.ToLowerInvariant(b[j - 1]);
                var cost = ca == cb ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    private static void PopulateTargetAlias(PlannerState state, ICelestialObjectDB objectDb, Target target)
    {
        if (state.TargetAliases.ContainsKey(target) || target.CatalogIndex is not { } catIdx)
        {
            return;
        }

        var parts = new List<string>();

        // Add the canonical catalog designation (e.g. "NGC 3372")
        var canonical = catIdx.ToCanonical();
        if (!canonical.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(canonical);
        }

        // Add all other common names
        if (objectDb.TryLookupByIndex(catIdx, out var obj))
        {
            foreach (var cn in obj.CommonNames.OrderByDescending(n => n.Length))
            {
                if (!cn.Equals(target.Name, StringComparison.OrdinalIgnoreCase)
                    && !parts.Contains(cn))
                {
                    parts.Add(cn);
                }
            }
        }

        // Add cross-referenced catalog designations
        if (objectDb.TryGetCrossIndices(catIdx, out var crossIndices))
        {
            foreach (var cross in crossIndices)
            {
                if (cross != catIdx)
                {
                    var crossCanonical = cross.ToCanonical();
                    if (!parts.Contains(crossCanonical))
                    {
                        parts.Add(crossCanonical);
                    }
                }
            }
        }

        if (parts.Count > 0)
        {
            state.TargetAliases[target] = string.Join(", ", parts.Take(8));
        }
    }

    /// <summary>
    /// Adds a target from TonightsBest to the proposals list.
    /// </summary>
    public static void AddProposal(PlannerState state, int tonightsBestIndex, ObservationPriority priority = ObservationPriority.Normal)
    {
        if (tonightsBestIndex < 0 || tonightsBestIndex >= state.TonightsBest.Count)
        {
            return;
        }

        var target = state.TonightsBest[tonightsBestIndex].Target;

        // Don't add duplicates
        if (state.Proposals.Any(p => p.Target == target))
        {
            return;
        }

        state.Proposals.Add(new ProposedObservation(target, Priority: priority));
        // Schedule (on SessionTabState) becomes stale — rebuilt on demand via "Build Schedule"
        RecomputeHandoffSliders(state);
        state.IsDirty = true;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Removes a proposal by index.
    /// </summary>
    public static void RemoveProposal(PlannerState state, int proposalIndex)
    {
        if (proposalIndex < 0 || proposalIndex >= state.Proposals.Count)
        {
            return;
        }

        state.Proposals.RemoveAt(proposalIndex);
        // Schedule (on SessionTabState) becomes stale — rebuilt on demand via "Build Schedule"
        RecomputeHandoffSliders(state);
        state.IsDirty = true;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Toggles a target: if already proposed, removes it; otherwise adds it.
    /// </summary>
    public static void ToggleProposal(PlannerState state, Target target, ObservationPriority priority = ObservationPriority.Normal)
    {
        var existingIndex = state.Proposals.FindIndex(p => p.Target == target);
        if (existingIndex >= 0)
        {
            state.Proposals.RemoveAt(existingIndex);
        }
        else
        {
            state.Proposals.Add(new ProposedObservation(target, Priority: priority));
        }
        // Schedule (on SessionTabState) becomes stale — rebuilt on demand via "Build Schedule"
        RecomputeHandoffSliders(state);

        // Selection follows the target to its new position in the reordered list
        var filtered = GetFilteredTargets(state);
        for (var i = 0; i < filtered.Count; i++)
        {
            if (filtered[i].Target == target)
            {
                state.SelectedTargetIndex = i;
                break;
            }
        }

        state.IsDirty = true;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Cycles the priority of a proposal.
    /// </summary>
    public static void CyclePriority(PlannerState state, int proposalIndex)
    {
        if (proposalIndex < 0 || proposalIndex >= state.Proposals.Count)
        {
            return;
        }

        var p = state.Proposals[proposalIndex];
        var nextPriority = p.Priority switch
        {
            ObservationPriority.High => ObservationPriority.Normal,
            ObservationPriority.Normal => ObservationPriority.Low,
            ObservationPriority.Low => ObservationPriority.Spare,
            _ => ObservationPriority.High
        };
        state.Proposals[proposalIndex] = p with { Priority = nextPriority };
        // Schedule (on SessionTabState) becomes stale — rebuilt on demand via "Build Schedule"
        state.IsDirty = true;
        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Recomputes handoff slider positions between adjacent pinned targets.
    /// Sliders default to where the altitude curves of adjacent targets intersect.
    /// Also computes conflict flags for targets with overlapping peak windows.
    /// </summary>
    public static void RecomputeHandoffSliders(PlannerState state)
    {
        // Get sorted pinned targets (call GetFilteredTargets to update PinnedCount)
        var filtered = GetFilteredTargets(state);
        var pinnedCount = state.PinnedCount;

        state.HandoffSliders.Clear();
        state.PinnedTargetConflicts = new bool[pinnedCount];

        if (pinnedCount < 2)
        {
            return;
        }

        var minSlot = TimeSpan.FromMinutes(15);

        for (var i = 0; i < pinnedCount - 1; i++)
        {
            var targetA = filtered[i].Target;
            var targetB = filtered[i + 1].Target;

            var slider = FindCurveIntersection(state, targetA, targetB);

            // Ensure sliders are monotonically increasing with minimum gap
            var minTime = i > 0 ? state.HandoffSliders[i - 1] + minSlot : state.AstroDark + minSlot;
            var maxTime = state.AstroTwilight - minSlot * (pinnedCount - 1 - i);
            if (slider < minTime) slider = minTime;
            if (slider > maxTime) slider = maxTime;

            state.HandoffSliders.Add(slider);

            // Conflict: check if actual peak altitude times are within 1 hour
            var peakTimeA = state.AltitudeProfiles.TryGetValue(filtered[i].Target, out var profA) && profA.Count > 0
                ? profA.MaxBy(p => p.Alt).Time : filtered[i].OptimalStart;
            var peakTimeB = state.AltitudeProfiles.TryGetValue(filtered[i + 1].Target, out var profB) && profB.Count > 0
                ? profB.MaxBy(p => p.Alt).Time : filtered[i + 1].OptimalStart;
            var peakSeparation = Math.Abs((peakTimeB - peakTimeA).TotalHours);

            if (peakSeparation < 1.0)
            {
                state.PinnedTargetConflicts[i] = true;
                state.PinnedTargetConflicts[i + 1] = true;
            }
        }

        // Flag targets whose allocated window is very small (< 1.5 hours)
        for (var i = 0; i < pinnedCount; i++)
        {
            var windowStart = i == 0 ? state.AstroDark
                : i - 1 < state.HandoffSliders.Count ? state.HandoffSliders[i - 1] : state.AstroDark;
            var windowEnd = i >= pinnedCount - 1 || i >= state.HandoffSliders.Count
                ? state.AstroTwilight : state.HandoffSliders[i];
            var allocatedHours = (windowEnd - windowStart).TotalHours;

            if (allocatedHours < 1.5)
            {
                state.PinnedTargetConflicts[i] = true;
            }
        }
    }

    /// <summary>
    /// Finds where two targets' altitude curves intersect during the night.
    /// Falls back to the midpoint between their optimal starts.
    /// </summary>
    private static DateTimeOffset FindCurveIntersection(PlannerState state, Target a, Target b)
    {
        if (state.AltitudeProfiles.TryGetValue(a, out var profileA)
            && state.AltitudeProfiles.TryGetValue(b, out var profileB)
            && profileA.Count >= 2 && profileB.Count >= 2)
        {
            // Both profiles have the same time steps, find where altA - altB changes sign
            var minCount = Math.Min(profileA.Count, profileB.Count);
            for (var k = 0; k < minCount - 1; k++)
            {
                var diffK = profileA[k].Alt - profileB[k].Alt;
                var diffK1 = profileA[k + 1].Alt - profileB[k + 1].Alt;

                // Sign change → curves cross between k and k+1
                if (diffK >= 0 && diffK1 < 0 || diffK <= 0 && diffK1 > 0)
                {
                    // Linear interpolation for exact crossing time
                    var fraction = Math.Abs(diffK) / (Math.Abs(diffK) + Math.Abs(diffK1));
                    var tK = profileA[k].Time;
                    var tK1 = profileA[k + 1].Time;
                    var crossTime = tK + TimeSpan.FromSeconds((tK1 - tK).TotalSeconds * fraction);

                    // Only use if within the dark window
                    if (crossTime >= state.AstroDark && crossTime <= state.AstroTwilight)
                    {
                        return crossTime;
                    }
                }
            }
        }

        // Fallback: midpoint between optimal starts
        var scoredA = state.ScoredTargets.TryGetValue(a, out var sA) ? sA : default;
        var scoredB = state.ScoredTargets.TryGetValue(b, out var sB) ? sB : default;
        if (scoredA.Target != default && scoredB.Target != default)
        {
            var midTicks = (scoredA.OptimalStart.Ticks + scoredB.OptimalStart.Ticks) / 2;
            return new DateTimeOffset(midTicks, scoredA.OptimalStart.Offset);
        }

        // Last resort: midpoint of the dark window
        return state.AstroDark + (state.AstroTwilight - state.AstroDark) / 2;
    }

    /// <summary>
    /// Runs the scheduler on current proposals and stores the result on <paramref name="sessionState"/>.
    /// </summary>
    public static void BuildSchedule(
        PlannerState state,
        SessionTabState sessionState,
        Transform transform,
        int defaultGain,
        int defaultOffset,
        TimeSpan defaultSubExposure,
        TimeSpan defaultObservationTime,
        IReadOnlyList<InstalledFilter>? availableFilters = null,
        OpticalDesign opticalDesign = OpticalDesign.Unknown)
    {
        if (state.Proposals.Count == 0)
        {
            sessionState.Schedule = null;
            state.NeedsRedraw = true;
            return;
        }

        var pinnedCount = state.PinnedCount;

        // When handoff sliders are set, use them to define the schedule windows
        // (the user manually positioned them in the altitude chart)
        if (pinnedCount > 0 && state.HandoffSliders.Count == pinnedCount - 1)
        {
            var siderealTimeAtDark = Transform.CalculateLocalSiderealTime(
                state.AstroDark.UtcDateTime, transform.SiteLongitude);

            var observations = ImmutableArray.CreateBuilder<ScheduledObservation>(pinnedCount);
            for (var i = 0; i < pinnedCount; i++)
            {
                var proposal = state.Proposals[i];
                var start = i == 0 ? state.AstroDark : state.HandoffSliders[i - 1];
                var end = i >= pinnedCount - 1 ? state.AstroTwilight : state.HandoffSliders[i];
                var duration = end - start;

                // AcrossMeridian: target transits during this observation window
                var hourAngle = CoordinateUtils.ConditionHA(siderealTimeAtDark - proposal.Target.RA);
                var transitTime = state.AstroDark - TimeSpan.FromHours(hourAngle);
                var acrossMeridian = transitTime >= start && transitTime <= end;

                var filterPlan = availableFilters is { Count: > 0 }
                    ? FilterPlanBuilder.BuildAutoFilterPlan(availableFilters, defaultSubExposure, defaultSubExposure * 3)
                    : FilterPlanBuilder.BuildSingleFilterPlan(defaultSubExposure);

                observations.Add(new ScheduledObservation(
                    proposal.Target, start, duration, acrossMeridian,
                    filterPlan, proposal.Gain ?? defaultGain, proposal.Offset ?? defaultOffset,
                    proposal.Priority));
            }

            sessionState.Schedule = new ScheduledObservationTree(observations.MoveToImmutable());
            state.NeedsRedraw = true;
            sessionState.NeedsRedraw = true;
            return;
        }

        // Fallback: automatic scheduling via ObservationScheduler
        sessionState.Schedule = ObservationScheduler.Schedule(
            state.Proposals.ToArray(),
            transform,
            state.AstroDark,
            state.AstroTwilight,
            state.MinHeightAboveHorizon,
            defaultGain,
            defaultOffset,
            defaultSubExposure,
            defaultObservationTime,
            availableFilters: availableFilters,
            opticalDesign: opticalDesign);

        state.NeedsRedraw = true;
        sessionState.NeedsRedraw = true;
    }

    /// <summary>
    /// Formats the TonightsBest list as lines for non-interactive output.
    /// </summary>
    /// <summary>
    /// Normalizes a score to a 0.5–5.0★ rating using log-scale compression.
    /// Preserves relative differences while compressing the steep drop-off
    /// typical of astronomical target scoring.
    /// </summary>
    public static float ScoreToRating(double score, double maxScore)
    {
        if (maxScore <= 0 || score <= 0) return 0.5f;
        // Log-scale: ratio in log space, mapped to 0.5–5.0
        var logRatio = Math.Log(1 + score) / Math.Log(1 + maxScore);
        return Math.Max(0.5f, (float)(logRatio * 4.5 + 0.5));
    }

    public static IReadOnlyList<string> FormatTonightsBestLines(PlannerState state, int maxLines = 30)
    {
        var maxScore = state.TonightsBest.Count > 0 ? state.TonightsBest[0].CombinedScore : 1.0;

        var lines = new List<string>
        {
            $" # | {"Target",-20} | {"Type",-10} | {"Alt",4} | {"Window",-13} | {"Rating",5}",
            new string('-', 72)
        };

        for (var i = 0; i < Math.Min(state.TonightsBest.Count, maxLines); i++)
        {
            var s = state.TonightsBest[i];
            var proposed = state.Proposals.Any(p => p.Target == s.Target) ? "*" : " ";
            var typeName = s.Target.CatalogIndex?.ToCatalog().ToString() ?? "?";
            if (typeName.Length > 10) typeName = typeName[..10];

            var window = $"{s.OptimalStart.ToOffset(state.SiteTimeZone):HH:mm}-{(s.OptimalStart + s.OptimalDuration).ToOffset(state.SiteTimeZone):HH:mm}";
            var rating = ScoreToRating(s.CombinedScore, maxScore);

            lines.Add($"{proposed}{i + 1,2} | {s.Target.Name,-20} | {typeName,-10} | {s.OptimalAltitude,3:F0}° | {window,-13} | {rating,4:F1}\u2605");
        }

        return lines;
    }

    /// <summary>
    /// Formats the schedule as lines for non-interactive output.
    /// </summary>
    public static IReadOnlyList<string> FormatScheduleLines(SessionTabState sessionState, TimeSpan siteTimeZone)
    {
        if (sessionState.Schedule is not { Count: > 0 } schedule)
        {
            return ["No schedule computed. Build schedule from the Session tab."];
        }

        var lines = new List<string>
        {
            $"Schedule: {schedule.Count} observations",
            $" # | {"Target",-20} | {"Priority",-8} | {"Start",-5} | {"End",-5} | {"Dur",4} | Flip",
            new string('-', 72)
        };

        for (var i = 0; i < schedule.Count; i++)
        {
            var obs = schedule[i];
            var end = obs.Start + obs.Duration;
            var flip = obs.AcrossMeridian ? "yes" : "no";
            lines.Add($"{i + 1,2} | {obs.Target.Name,-20} | {obs.Priority,-8} | {obs.Start.ToOffset(siteTimeZone):HH:mm} | {end.ToOffset(siteTimeZone):HH:mm} | {obs.Duration.TotalMinutes,3:F0}m | {flip}");

            var spares = schedule.GetSparesForSlot(i);
            if (!spares.IsEmpty)
            {
                foreach (var spare in spares)
                {
                    lines.Add($"   |   spare: {spare.Target.Name}");
                }
            }
        }

        return lines;
    }

    // --- Private helpers ---

    private static void ComputeTwilightBoundaries(
        PlannerState state, Transform transform,
        DateTimeOffset astroDark, DateTimeOffset astroTwilight)
    {
        var eveningDate = astroDark.AddHours(-12);
        var eveningDayStart = new DateTimeOffset(eveningDate.Date, eveningDate.Offset);
        var morningDayStart = new DateTimeOffset(astroTwilight.Date, astroTwilight.Offset);

        // Evening SET events (dusk)
        transform.DateTimeOffset = eveningDayStart;
        var (_, _, civilS) = transform.EventTimes(EventType.CivilTwilight);
        if (civilS is { Count: >= 1 })
        {
            state.CivilSet = eveningDayStart + civilS[0];
        }

        transform.DateTimeOffset = eveningDayStart;
        var (_, _, nautS) = transform.EventTimes(EventType.NauticalTwilight);
        if (nautS is { Count: >= 1 })
        {
            state.NauticalSet = eveningDayStart + nautS[0];
        }

        // Morning RISE events (dawn)
        transform.DateTimeOffset = morningDayStart;
        var (_, civilR, _) = transform.EventTimes(EventType.CivilTwilight);
        if (civilR is { Count: >= 1 })
        {
            state.CivilRise = morningDayStart + civilR[0];
        }

        transform.DateTimeOffset = morningDayStart;
        var (_, nautR, _) = transform.EventTimes(EventType.NauticalTwilight);
        if (nautR is { Count: >= 1 })
        {
            state.NauticalRise = morningDayStart + nautR[0];
        }
    }

    private static (Astrom[] Astroms, DateTimeOffset[] Times) EnsureAstromGrid(PlannerState state, Transform transform)
    {
        return state.CachedAstromGrid ??= ComputeAstromGrid(state, transform);
    }

    private static (Astrom[] Astroms, DateTimeOffset[] Times) ComputeAstromGrid(PlannerState state, Transform transform)
    {
        var start = state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1);
        var end = state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1);
        start -= TimeSpan.FromMinutes(15);
        end += TimeSpan.FromMinutes(15);
        return ObservationScheduler.PrecomputeAstromGrid(
            start, end, transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);
    }

    /// <summary>
    /// Fast altitude profile using precomputed Astrom grid — no per-sample SOFA overhead.
    /// </summary>
    private static List<(DateTimeOffset Time, double Alt)> ComputeFineAltitudeProfileFast(
        Target target, Astrom[] astroms, DateTimeOffset[] times,
        double siteLat, double siteLong)
    {
        var profile = new List<(DateTimeOffset Time, double Alt)>(astroms.Length);

        // For planets, fall back to VSOP87 (they move)
        if (target.CatalogIndex is { } catIdx
            && VSOP87a.GetBody(catIdx, 0, stackalloc double[3]))
        {
            foreach (var t in times)
            {
                if (VSOP87a.Reduce(catIdx, t, siteLat, siteLong,
                    out _, out _, out _, out var planetAlt, out _))
                {
                    profile.Add((t, planetAlt));
                }
            }
            return profile;
        }

        // Fixed objects: use precomputed Astrom for fast altitude calculation
        for (var i = 0; i < astroms.Length; i++)
        {
            var alt = SOFAHelpers.AltitudeFromAstrom(target.RA, target.Dec, in astroms[i]);
            profile.Add((times[i], alt));
        }

        return profile;
    }
}
