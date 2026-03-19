using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
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

        // Compute fine altitude profiles for the top targets
        Report("Computing altitude profiles...");

        state.AltitudeProfiles.Clear();
        var targets = tonightsBest.Take(20).Select(s => s.Target).ToArray();
        foreach (var target in targets)
        {
            state.AltitudeProfiles[target] = ComputeFineAltitudeProfile(
                transform, target, state);
        }

        Report("");
        state.StatusMessage = null;
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
        state.Schedule = null; // invalidate schedule
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
    public static IReadOnlyList<string> FormatTonightsBestLines(PlannerState state, int maxLines = 30)
    {
        var lines = new List<string>
        {
            $" # | {"Target",-20} | {"Type",-10} | {"Alt",4} | {"Window",-13} | {"Score",6}",
            new string('-', 72)
        };

        for (var i = 0; i < Math.Min(state.TonightsBest.Count, maxLines); i++)
        {
            var s = state.TonightsBest[i];
            var proposed = state.Proposals.Any(p => p.Target == s.Target) ? "*" : " ";
            var typeName = s.Target.CatalogIndex?.ToCatalog().ToString() ?? "?";
            if (typeName.Length > 10) typeName = typeName[..10];

            var window = $"{s.OptimalStart.ToOffset(state.SiteTimeZone):HH:mm}-{(s.OptimalStart + s.OptimalDuration).ToOffset(state.SiteTimeZone):HH:mm}";

            lines.Add($"{proposed}{i + 1,2} | {s.Target.Name,-20} | {typeName,-10} | {s.OptimalAltitude,3:F0}° | {window,-13} | {s.CombinedScore,6:F0}");
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
            transform.SetJ2000(target.RA, target.Dec);
            transform.JulianDateUTC = t.ToJulian();

            if (transform.ElevationTopocentric is double alt)
            {
                profile.Add((t, alt));
            }
        }

        return profile;
    }
}
