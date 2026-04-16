using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Static content helper for the planner details panel.
/// Produces display-ready text lines from <see cref="PlannerState"/>.
/// </summary>
public static class PlannerDetails
{
    /// <summary>
    /// Returns display lines for the selected target's details panel.
    /// Returns an empty list if no target is selected.
    /// </summary>
    public static List<string> GetLines(
        PlannerState state,
        IReadOnlyList<ScoredTarget> filteredTargets,
        DateTimeOffset? currentTime = null)
    {
        var idx = state.SelectedTargetIndex;
        if (idx < 0 || idx >= filteredTargets.Count)
        {
            return [];
        }

        var scored = filteredTargets[idx];
        var isProposed = state.Proposals.Any(p => p.Target == scored.Target);
        var pinnedCount = state.PinnedCount;
        var lines = new List<string>();

        // Line 1: target name
        var statusSuffix = isProposed ? " [Proposed]" : "";
        lines.Add(scored.Target.Name + statusSuffix);

        // Line 2: coordinates + altitude + peak time + window
        var window = FormatWindow(scored, state);
        var peakTime = state.AltitudeProfiles.TryGetValue(scored.Target, out var prof) && prof.Count > 0
            ? prof.MaxBy(p => p.Alt).Time.ToOffset(state.SiteTimeZone).ToString("HH:mm")
            : "?";
        lines.Add($"RA {scored.Target.RA:F3}h  Dec {scored.Target.Dec:+0.0;-0.0}\u00b0" +
                  $"  Alt {scored.OptimalAltitude:F0}\u00b0  Peak {peakTime}  Window {window}");

        // Line 3: allocated imaging time (for pinned targets)
        if (isProposed && idx < pinnedCount)
        {
            var imgStart = idx == 0 ? state.AstroDark
                : idx - 1 < state.HandoffSliders.Count ? state.HandoffSliders[idx - 1] : state.AstroDark;
            var imgEnd = idx >= pinnedCount - 1 || idx >= state.HandoffSliders.Count
                ? state.AstroTwilight : state.HandoffSliders[idx];

            var effectiveStart = imgStart;
            if (currentTime is { } ct && ct > imgStart && ct < imgEnd)
            {
                effectiveStart = ct;
            }
            var imgDuration = imgEnd - effectiveStart;
            var imgStartStr = imgStart.ToOffset(state.SiteTimeZone).ToString("HH:mm");
            var imgEndStr = imgEnd.ToOffset(state.SiteTimeZone).ToString("HH:mm");

            var durationStr = imgDuration.TotalHours >= 1.0
                ? $"{(int)imgDuration.TotalHours}h {imgDuration.Minutes:D2}m"
                : $"{(int)imgDuration.TotalMinutes}m";
            lines.Add($"Imaging: {imgStartStr}\u2013{imgEndStr} ({durationStr})");
        }

        // Aliases
        if (state.TargetAliases.TryGetValue(scored.Target, out var alias))
        {
            lines.Add($"Also: {alias}");
        }

        // Rating
        var maxScore = state.TonightsBest.Length > 0 ? state.TonightsBest[0].CombinedScore : 1.0;
        var rating = PlannerActions.ScoreToRating(scored.CombinedScore, maxScore);
        lines.Add($"Rating: {rating:F1}\u2605");

        return lines;
    }

    private static string FormatWindow(ScoredTarget scored, PlannerState state)
    {
        var start = scored.OptimalStart.ToOffset(state.SiteTimeZone);
        var end = (scored.OptimalStart + scored.OptimalDuration).ToOffset(state.SiteTimeZone);
        return $"{start:HH:mm}\u2013{end:HH:mm}";
    }
}
