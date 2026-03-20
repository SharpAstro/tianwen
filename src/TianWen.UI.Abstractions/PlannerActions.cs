using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        state.AltitudeProfiles.Clear();
        state.TargetAliases.Clear();
        foreach (var scored in tonightsBest)
        {
            state.AltitudeProfiles[scored.Target] = ComputeFineAltitudeProfile(
                transform, scored.Target, state);
            PopulateTargetAlias(state, objectDb, scored.Target);
        }

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

        // Sort pinned by optimal start time (peak time ascending)
        pinnedScored.Sort((a, b) => a.OptimalStart.CompareTo(b.OptimalStart));

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
                state.AltitudeProfiles[target] = ComputeFineAltitudeProfile(transform, target, state);
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
            state.AltitudeProfiles[target] = ComputeFineAltitudeProfile(transform, target, state);
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
        state.Schedule = null;
        RecomputeHandoffSliders(state);
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
        state.Schedule = null;
        RecomputeHandoffSliders(state);
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
        state.Schedule = null;
        RecomputeHandoffSliders(state);
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
        state.Schedule = null;
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

        for (var i = 0; i < pinnedCount - 1; i++)
        {
            var targetA = filtered[i].Target;
            var targetB = filtered[i + 1].Target;

            var slider = FindCurveIntersection(state, targetA, targetB);
            state.HandoffSliders.Add(slider);

            // Conflict: check if peak times (midpoint of viable window) are within 1 hour
            var scoredA = filtered[i];
            var scoredB = filtered[i + 1];
            var peakA = scoredA.OptimalStart + scoredA.OptimalDuration / 2;
            var peakB = scoredB.OptimalStart + scoredB.OptimalDuration / 2;
            var peakSeparation = Math.Abs((peakB - peakA).TotalHours);

            if (peakSeparation < 1.0)
            {
                state.PinnedTargetConflicts[i] = true;
                state.PinnedTargetConflicts[i + 1] = true;
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
    /// Runs the scheduler on current proposals and stores the result.
    /// </summary>
    public static void BuildSchedule(
        PlannerState state,
        Transform transform,
        int defaultGain,
        int defaultOffset,
        TimeSpan defaultSubExposure,
        TimeSpan defaultObservationTime)
    {
        if (state.Proposals.Count == 0)
        {
            state.Schedule = null;
            state.NeedsRedraw = true;
            return;
        }

        state.Schedule = ObservationScheduler.Schedule(
            state.Proposals.ToArray(),
            transform,
            state.AstroDark,
            state.AstroTwilight,
            state.MinHeightAboveHorizon,
            defaultGain,
            defaultOffset,
            defaultSubExposure,
            defaultObservationTime);

        state.NeedsRedraw = true;
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
    public static IReadOnlyList<string> FormatScheduleLines(PlannerState state)
    {
        if (state.Schedule is not { Count: > 0 } schedule)
        {
            return ["No schedule computed. Add proposals and press S to schedule."];
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
            lines.Add($"{i + 1,2} | {obs.Target.Name,-20} | {obs.Priority,-8} | {obs.Start.ToOffset(state.SiteTimeZone):HH:mm} | {end.ToOffset(state.SiteTimeZone):HH:mm} | {obs.Duration.TotalMinutes,3:F0}m | {flip}");

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

    private static List<(DateTimeOffset Time, double Alt)> ComputeFineAltitudeProfile(
        Transform transform, Target target, PlannerState state)
    {
        var start = state.CivilSet ?? state.AstroDark - TimeSpan.FromHours(1);
        var end = state.CivilRise ?? state.AstroTwilight + TimeSpan.FromHours(1);
        start -= TimeSpan.FromMinutes(15);
        end += TimeSpan.FromMinutes(15);

        var step = TimeSpan.FromMinutes(10);
        var profile = new List<(DateTimeOffset Time, double Alt)>();

        for (var t = start; t <= end; t += step)
        {
            // For planets, Reduce recomputes position at each time step; returns false for non-planets
            if (target.CatalogIndex is { } catIdx
                && VSOP87a.Reduce(catIdx, t, transform.SiteLatitude, transform.SiteLongitude,
                    out _, out _, out _, out var planetAlt, out _))
            {
                profile.Add((t, planetAlt));
            }
            else
            {
                transform.SetJ2000(target.RA, target.Dec);
                transform.JulianDateUTC = t.ToJulian();

                if (transform.ElevationTopocentric is double alt)
                {
                    profile.Add((t, alt));
                }
            }
        }

        return profile;
    }
}
