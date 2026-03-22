using System;
using System.Collections.Generic;
using System.Linq;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Display model for a single row in the planner target list.
/// Produced by <see cref="PlannerTargetList.GetItems"/> and consumed by both
/// GPU (PixelWidgetBase) and terminal (ScrollableList) hosts.
/// </summary>
public readonly record struct PlannerTargetRow(
    string Name,
    string Info,
    bool IsPinned,
    bool IsSelected,
    bool HasConflict,
    int Index,
    double Rating);

/// <summary>
/// Static content helper for the planner target list.
/// Produces display-ready row models from <see cref="PlannerState"/>.
/// </summary>
public static class PlannerTargetList
{
    /// <summary>
    /// Returns display rows for the filtered target list.
    /// </summary>
    public static List<PlannerTargetRow> GetItems(
        PlannerState state,
        IReadOnlyList<ScoredTarget> filteredTargets)
    {
        var pinnedCount = state.PinnedCount;
        var maxScore = state.TonightsBest.Count > 0 ? state.TonightsBest[0].CombinedScore : 1.0;
        var rows = new List<PlannerTargetRow>(filteredTargets.Count);

        for (var i = 0; i < filteredTargets.Count; i++)
        {
            var scored = filteredTargets[i];
            var isPinned = i < pinnedCount;
            var isSelected = i == state.SelectedTargetIndex;

            string info;
            if (isPinned)
            {
                var startTime = i == 0 || state.HandoffSliders.Count == 0
                    ? state.AstroDark
                    : i - 1 < state.HandoffSliders.Count
                        ? state.HandoffSliders[i - 1]
                        : scored.OptimalStart;
                info = startTime.ToOffset(state.SiteTimeZone).ToString("HH:mm");
            }
            else
            {
                info = $"{scored.OptimalAltitude:F0}\u00b0";
            }

            var rating = PlannerActions.ScoreToRating(scored.CombinedScore, maxScore);

            rows.Add(new PlannerTargetRow(
                Name: scored.Target.Name,
                Info: info,
                IsPinned: isPinned,
                IsSelected: isSelected,
                HasConflict: false, // TODO: conflict detection from PlannerState
                Index: i,
                Rating: rating));
        }

        return rows;
    }

    /// <summary>
    /// Returns the label for the rating filter button.
    /// </summary>
    public static string GetFilterLabel(PlannerState state)
        => state.MinRatingFilter > 0f ? $"\u2605{state.MinRatingFilter:F0}+" : "All";
}
