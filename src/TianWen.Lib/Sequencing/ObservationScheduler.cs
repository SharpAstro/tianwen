using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

internal static class ObservationScheduler
{
    private static readonly TimeSpan TimeBinDuration = TimeSpan.FromMinutes(30);

    // Fallback chain: at high latitudes in summer, deeper twilight boundaries may not be reached.
    // CivilTwilight is excluded — too bright for astronomical observation.
    private static readonly EventType[] EveningFallbackChain =
    [
        EventType.AmateurAstronomicalTwilight,
        EventType.NauticalTwilight
    ];

    private static readonly EventType[] MorningFallbackChain =
    [
        EventType.AstronomicalTwilight,
        EventType.NauticalTwilight
    ];

    /// <summary>
    /// Calculates the night window from a Transform, with fallback for high-latitude sites
    /// where deeper twilight boundaries are not reached (e.g., Dublin in summer).
    /// For polar night (sun never rises), returns a 24-hour observation window.
    /// The Transform must have SiteLatitude, SiteLongitude, SiteElevation, and DateTimeOffset set.
    /// </summary>
    /// <returns>Tuple of (astroDark, astroTwilight) as absolute DateTimeOffsets.</returns>
    public static (DateTimeOffset AstroDark, DateTimeOffset AstroTwilight) CalculateNightWindow(Transform transform)
    {
        // Read back DateTimeOffset to get the computed site timezone, then strip to local midnight.
        var dto = transform.DateTimeOffset;
        var localDayStart = new DateTimeOffset(dto.Date, dto.Offset);

        // Check for polar night: if the sun never rises, the entire day is available for observation.
        transform.DateTimeOffset = localDayStart;
        var (_, sunRise, _) = transform.EventTimes(EventType.SunRiseSunset);
        if (sunRise is { Count: 0 })
        {
            return (localDayStart, localDayStart.AddDays(1));
        }

        // Evening: find when it gets dark enough (try deepest twilight first)
        var astroDark = TryGetFirstEvent(transform, localDayStart, EveningFallbackChain, set: true)
            ?? localDayStart; // Fallback: midnight

        // Morning: find when it gets too light AFTER the evening dark time.
        // If the SET is after midnight (high-latitude summer, e.g. Dublin), search the same day;
        // otherwise search the next day for the morning RISE.
        var morningSearchDay = astroDark.TimeOfDay.TotalHours >= 12
            ? localDayStart.AddDays(1)
            : new DateTimeOffset(astroDark.Date, astroDark.Offset);

        var astroTwilight = TryGetFirstEvent(transform, morningSearchDay, MorningFallbackChain, set: false);

        // Validate: twilight must be after dark
        if (astroTwilight is null || astroTwilight.Value <= astroDark)
        {
            astroTwilight = localDayStart.AddDays(1);
        }

        return (astroDark, astroTwilight.Value);
    }

    private static DateTimeOffset? TryGetFirstEvent(Transform transform, DateTimeOffset localDayStart, ReadOnlySpan<EventType> fallbackChain, bool set)
    {
        foreach (var eventType in fallbackChain)
        {
            transform.DateTimeOffset = localDayStart;
            var (_, rise, setEvents) = transform.EventTimes(eventType);
            var events = set ? setEvents : rise;
            if (events is { Count: >= 1 })
            {
                return localDayStart + events[0];
            }
        }

        return null;
    }

    /// <summary>
    /// Scores a target by accumulating altitude above the minimum horizon across the night.
    /// Higher altitude = more points. Returns the total score, elevation profile, and optimal window.
    /// </summary>
    public static TargetScore ScoreTarget(
        Target target,
        Transform transform,
        DateTimeOffset astroDark,
        DateTimeOffset astroTwilight,
        byte minHeightAboveHorizon)
    {
        var profile = transform.CalculateObjElevation(target.RA, target.Dec, astroDark, astroTwilight);

        var totalScore = 0d;
        var bestAlt = double.MinValue;
        DateTimeOffset bestTime = astroDark;
        DateTimeOffset? windowStart = null;
        DateTimeOffset? windowEnd = null;

        foreach (var (_, info) in profile)
        {
            if (info.Alt > minHeightAboveHorizon)
            {
                totalScore += info.Alt - minHeightAboveHorizon;

                windowStart ??= info.Time;
                windowEnd = info.Time;

                if (info.Alt > bestAlt)
                {
                    bestAlt = info.Alt;
                    bestTime = info.Time;
                }
            }
        }

        var optimalStart = windowStart ?? astroDark;
        var optimalDuration = windowEnd.HasValue && windowStart.HasValue
            ? windowEnd.Value - windowStart.Value
            : TimeSpan.Zero;

        return new TargetScore(target, totalScore, profile, optimalStart, optimalDuration);
    }

    /// <summary>
    /// Schedules proposed observations into a <see cref="ScheduledObservationTree"/>.
    /// Calculates astro dark/twilight from the Transform, then:
    /// 1. Score all proposals by altitude across the night.
    /// 2. Sort by priority then score descending.
    /// 3. Allocate time bins: High first in optimal windows, Normal fills gaps, Low gets leftovers.
    /// 4. Spare proposals attach as alternatives per slot.
    /// 5. Resolve nullable fields from defaults.
    /// </summary>
    public static ScheduledObservationTree Schedule(
        ReadOnlySpan<ProposedObservation> proposals,
        Transform transform,
        byte minHeightAboveHorizon,
        int defaultGain,
        int defaultOffset,
        TimeSpan defaultSubExposure,
        TimeSpan defaultObservationTime)
    {
        if (proposals.IsEmpty)
        {
            return new ScheduledObservationTree([]);
        }

        var (astroDark, astroTwilight) = CalculateNightWindow(transform);

        return Schedule(proposals, transform, astroDark, astroTwilight, minHeightAboveHorizon,
            defaultGain, defaultOffset, defaultSubExposure, defaultObservationTime);
    }

    /// <summary>
    /// Schedules proposed observations with explicitly provided night window boundaries.
    /// Useful for testing or when the caller has already computed the night window.
    /// </summary>
    public static ScheduledObservationTree Schedule(
        ReadOnlySpan<ProposedObservation> proposals,
        Transform transform,
        DateTimeOffset astroDark,
        DateTimeOffset astroTwilight,
        byte minHeightAboveHorizon,
        int defaultGain,
        int defaultOffset,
        TimeSpan defaultSubExposure,
        TimeSpan defaultObservationTime)
    {
        if (proposals.IsEmpty)
        {
            return new ScheduledObservationTree([]);
        }

        // Score all proposals
        var scored = new List<(ProposedObservation Proposal, TargetScore Score)>(proposals.Length);
        foreach (var proposal in proposals)
        {
            var score = ScoreTarget(proposal.Target, transform, astroDark, astroTwilight, minHeightAboveHorizon);
            scored.Add((proposal, score));
        }

        // Separate spares from schedulable proposals, sorted by score descending
        var spares = scored.Where(s => s.Proposal.Priority == ObservationPriority.Spare)
            .OrderByDescending(s => s.Score.TotalScore)
            .ToList();
        var schedulable = scored
            .Where(s => s.Proposal.Priority != ObservationPriority.Spare)
            .OrderBy(s => s.Proposal.Priority)
            .ThenByDescending(s => s.Score.TotalScore)
            .ToList();

        // Build time bins from astro dark to astro twilight
        var nightDuration = astroTwilight - astroDark;
        var binCount = Math.Max(1, (int)Math.Ceiling(nightDuration / TimeBinDuration));

        var primaryList = ImmutableArray.CreateBuilder<ScheduledObservation>(schedulable.Count);
        var sparesMap = ImmutableDictionary.CreateBuilder<int, ImmutableArray<ScheduledObservation>>();
        var allocatedBins = new bool[binCount];

        // Schedule each proposal into its optimal time window
        foreach (var (proposal, score) in schedulable)
        {
            if (score.TotalScore <= 0)
            {
                continue; // Target never rises above minimum
            }

            var observationTime = proposal.ObservationTime ?? defaultObservationTime;
            var subExposure = proposal.SubExposure ?? defaultSubExposure;
            var gain = proposal.Gain ?? defaultGain;
            var offset = proposal.Offset ?? defaultOffset;

            // Find the best contiguous bins for this target
            var binsNeeded = Math.Max(1, (int)Math.Ceiling(observationTime / TimeBinDuration));
            var bestStartBin = FindBestStartBin(allocatedBins, binsNeeded, score.OptimalStart, astroDark, binCount);

            if (bestStartBin < 0)
            {
                continue; // No room
            }

            // Mark bins as allocated
            for (var b = bestStartBin; b < bestStartBin + binsNeeded && b < binCount; b++)
            {
                allocatedBins[b] = true;
            }

            var start = astroDark + TimeBinDuration * bestStartBin;
            var duration = TimeBinDuration * binsNeeded;
            if (duration > observationTime)
            {
                duration = observationTime;
            }

            // Determine if observation crosses meridian
            var acrossMeridian = score.ElevationProfile.TryGetValue(RaDecEventTime.Meridian, out var meridianInfo)
                && meridianInfo.Time >= start
                && meridianInfo.Time <= start + duration;

            var slotIndex = primaryList.Count;
            primaryList.Add(new ScheduledObservation(
                proposal.Target,
                start,
                duration,
                acrossMeridian,
                subExposure,
                gain,
                offset,
                proposal.Priority
            ));

            // Attach spares for this slot (spares that are also visible during this window)
            var slotSpares = ImmutableArray.CreateBuilder<ScheduledObservation>();
            foreach (var (spareProposal, spareScore) in spares)
            {
                if (spareScore.TotalScore <= 0)
                {
                    continue;
                }

                // Check if spare is visible during this slot's time window
                var spareVisibleDuringSlot = false;
                foreach (var (_, info) in spareScore.ElevationProfile)
                {
                    if (info.Alt > minHeightAboveHorizon && info.Time >= start && info.Time <= start + duration)
                    {
                        spareVisibleDuringSlot = true;
                        break;
                    }
                }

                if (spareVisibleDuringSlot)
                {
                    var spareAcrossMeridian = spareScore.ElevationProfile.TryGetValue(RaDecEventTime.Meridian, out var spareMeridian)
                        && spareMeridian.Time >= start
                        && spareMeridian.Time <= start + duration;

                    slotSpares.Add(new ScheduledObservation(
                        spareProposal.Target,
                        start,
                        duration,
                        spareAcrossMeridian,
                        spareProposal.SubExposure ?? subExposure,
                        spareProposal.Gain ?? gain,
                        spareProposal.Offset ?? offset,
                        ObservationPriority.Spare
                    ));
                }
            }

            if (slotSpares.Count > 0)
            {
                sparesMap[slotIndex] = slotSpares.ToImmutable();
            }
        }

        return new ScheduledObservationTree(primaryList.ToImmutable(), sparesMap.ToImmutable());
    }

    private static int FindBestStartBin(bool[] allocatedBins, int binsNeeded, DateTimeOffset optimalStart, DateTimeOffset nightStart, int binCount)
    {
        // Preferred bin based on optimal start time
        var preferredBin = Math.Max(0, Math.Min(binCount - 1, (int)((optimalStart - nightStart) / TimeBinDuration)));

        // Search outward from preferred bin
        for (var offset = 0; offset < binCount; offset++)
        {
            foreach (var candidate in new[] { preferredBin + offset, preferredBin - offset })
            {
                if (candidate >= 0 && candidate + binsNeeded <= binCount && AreBinsFree(allocatedBins, candidate, binsNeeded))
                {
                    return candidate;
                }
            }
        }

        return -1;
    }

    private static bool AreBinsFree(bool[] allocatedBins, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (allocatedBins[i])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Resolves gain from available sources. Priority:
    /// 1. Explicit proposed value
    /// 2. Camera URI query "gain" value
    /// 3. Interpolation from MinGain/MaxGain (40% from min, same as DAL default)
    /// </summary>
    public static int ResolveGain(int? proposedGain, Uri? cameraUri, short gainMin, short gainMax, bool usesGainValue)
    {
        if (proposedGain.HasValue)
        {
            return proposedGain.Value;
        }

        if (cameraUri is not null)
        {
            var query = System.Web.HttpUtility.ParseQueryString(cameraUri.Query);
            if (query[DeviceQueryKey.Gain.Key] is { Length: > 0 } gainStr && int.TryParse(gainStr, out var uriGain))
            {
                return uriGain;
            }
        }

        if (usesGainValue && gainMin >= 0 && gainMax > gainMin)
        {
            // 40% from min toward max — reasonable default for most CMOS cameras
            return (int)MathF.FusedMultiplyAdd(gainMax - gainMin, 0.4f, gainMin);
        }

        return 0;
    }

    /// <summary>
    /// Resolves offset from available sources. Priority:
    /// 1. Explicit proposed value
    /// 2. Camera URI query "offset" value
    /// 3. Zero (safe default)
    /// </summary>
    public static int ResolveOffset(int? proposedOffset, Uri? cameraUri)
    {
        if (proposedOffset.HasValue)
        {
            return proposedOffset.Value;
        }

        if (cameraUri is not null)
        {
            var query = System.Web.HttpUtility.ParseQueryString(cameraUri.Query);
            if (query[DeviceQueryKey.Offset.Key] is { Length: > 0 } offsetStr && int.TryParse(offsetStr, out var uriOffset))
            {
                return uriOffset;
            }
        }

        return 0;
    }
}
