using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
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
    /// Precomputes Astrom structs internally. For batch scoring, prefer the overload taking
    /// precomputed arrays to avoid redundant Apco13 calls.
    /// </summary>
    public static ScoredTarget ScoreTarget(
        Target target,
        Transform transform,
        DateTimeOffset astroDark,
        DateTimeOffset astroTwilight,
        byte minHeightAboveHorizon)
    {
        var (astroms, times) = PrecomputeAstromGrid(
            astroDark, astroTwilight,
            transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);
        return ScoreTarget(target, astroms, times, astroDark, astroTwilight, minHeightAboveHorizon, transform.SiteLongitude);
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
        TimeSpan defaultObservationTime,
        IReadOnlyList<InstalledFilter>? availableFilters = null,
        TimeSpan? defaultNarrowbandSubExposure = null,
        OpticalDesign opticalDesign = OpticalDesign.Unknown)
    {
        if (proposals.IsEmpty)
        {
            return new ScheduledObservationTree([]);
        }

        // Precompute Astrom grid once for all proposals
        var (astroms, times) = PrecomputeAstromGrid(
            astroDark, astroTwilight,
            transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);

        // Score all proposals
        var scored = new List<(ProposedObservation Proposal, ScoredTarget Score)>(proposals.Length);
        foreach (var proposal in proposals)
        {
            var score = ScoreTarget(proposal.Target, astroms, times, astroDark, astroTwilight,
                minHeightAboveHorizon, transform.SiteLongitude);
            scored.Add((proposal, score));
        }

        // Separate spares from schedulable proposals, sorted by score descending
        var spares = scored.Where(s => s.Proposal.Priority == ObservationPriority.Spare)
            .OrderByDescending(s => s.Score.TotalScore)
            .ToList();

        // Separate mosaic groups from individual proposals
        var mosaicGroups = new Dictionary<Guid, List<(ProposedObservation Proposal, ScoredTarget Score)>>();
        var individualSchedulable = new List<(ProposedObservation Proposal, ScoredTarget Score)>();

        foreach (var item in scored.Where(s => s.Proposal.Priority != ObservationPriority.Spare))
        {
            if (item.Proposal.MosaicGroupId is { } groupId)
            {
                if (!mosaicGroups.TryGetValue(groupId, out var group))
                {
                    group = new List<(ProposedObservation Proposal, ScoredTarget Score)>();
                    mosaicGroups[groupId] = group;
                }
                group.Add(item);
            }
            else
            {
                individualSchedulable.Add(item);
            }
        }

        // Build a unified schedulable list: mosaic groups are represented by their first panel
        // (for priority/score ordering), then expanded during scheduling
        var schedulable = new List<(ProposedObservation Proposal, ScoredTarget Score, Guid? GroupId)>();

        foreach (var (groupId, group) in mosaicGroups)
        {
            // Order panels by RA ascending within the group
            group.Sort((a, b) => a.Proposal.Target.RA.CompareTo(b.Proposal.Target.RA));

            // Use the group's best score and highest priority for ordering
            var bestScore = group.Max(g => g.Score.TotalScore);
            var highestPriority = group.Min(g => g.Proposal.Priority);
            var representative = group[0];

            schedulable.Add((representative.Proposal with { Priority = highestPriority },
                representative.Score with { TotalScore = bestScore }, groupId));
        }

        foreach (var item in individualSchedulable)
        {
            schedulable.Add((item.Proposal, item.Score, null));
        }

        schedulable.Sort((a, b) =>
        {
            var priCmp = a.Proposal.Priority.CompareTo(b.Proposal.Priority);
            return priCmp != 0 ? priCmp : -a.Score.TotalScore.CompareTo(b.Score.TotalScore);
        });

        // Build time bins from astro dark to astro twilight
        var nightDuration = astroTwilight - astroDark;
        var binCount = Math.Max(1, (int)Math.Ceiling(nightDuration / TimeBinDuration));

        var primaryList = ImmutableArray.CreateBuilder<ScheduledObservation>(scored.Count);
        var sparesMap = ImmutableDictionary.CreateBuilder<int, ImmutableArray<ScheduledObservation>>();
        var allocatedBins = new bool[binCount];

        var siderealTimeAtAstroDark = Transform.CalculateLocalSiderealTime(astroDark.UtcDateTime, transform.SiteLongitude);

        // Schedule each proposal (or mosaic group) into its optimal time window
        foreach (var (proposal, score, groupId) in schedulable)
        {
            if (score.TotalScore <= Half.Zero)
            {
                continue; // Target never rises above minimum
            }

            if (groupId is { } gid && mosaicGroups.TryGetValue(gid, out var mosaicGroup))
            {
                // Schedule entire mosaic group as a contiguous block
                ScheduleMosaicGroup(mosaicGroup, proposal, score, astroDark, defaultObservationTime,
                    defaultSubExposure, defaultGain, defaultOffset, allocatedBins, binCount,
                    siderealTimeAtAstroDark, availableFilters, defaultNarrowbandSubExposure,
                    primaryList, sparesMap, spares, minHeightAboveHorizon);
            }
            else
            {
                // Schedule individual proposal
                ScheduleIndividualProposal(proposal, score, astroDark, defaultObservationTime,
                    defaultSubExposure, defaultGain, defaultOffset, allocatedBins, binCount,
                    availableFilters, defaultNarrowbandSubExposure,
                    primaryList, sparesMap, spares, minHeightAboveHorizon);
            }
        }

        return new ScheduledObservationTree(primaryList.ToImmutable(), sparesMap.ToImmutable());
    }

    private static void ScheduleIndividualProposal(
        ProposedObservation proposal,
        ScoredTarget score,
        DateTimeOffset astroDark,
        TimeSpan defaultObservationTime,
        TimeSpan defaultSubExposure,
        int defaultGain,
        int defaultOffset,
        bool[] allocatedBins,
        int binCount,
        IReadOnlyList<InstalledFilter>? availableFilters,
        TimeSpan? defaultNarrowbandSubExposure,
        ImmutableArray<ScheduledObservation>.Builder primaryList,
        ImmutableDictionary<int, ImmutableArray<ScheduledObservation>>.Builder sparesMap,
        List<(ProposedObservation Proposal, ScoredTarget Score)> spares,
        byte minHeightAboveHorizon)
    {
        var observationTime = proposal.ObservationTime ?? defaultObservationTime;
        var subExposure = proposal.SubExposure ?? defaultSubExposure;
        var gain = proposal.Gain ?? defaultGain;
        var offset = proposal.Offset ?? defaultOffset;

        var binsNeeded = Math.Max(1, (int)Math.Ceiling(observationTime / TimeBinDuration));
        var bestStartBin = FindBestStartBin(allocatedBins, binsNeeded, score.OptimalStart, astroDark, binCount);

        if (bestStartBin < 0)
        {
            return;
        }

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

        var acrossMeridian = score.ElevationProfile.TryGetValue(RaDecEventTime.Meridian, out var meridianInfo)
            && meridianInfo.Time >= start
            && meridianInfo.Time <= start + duration;

        var slotIndex = primaryList.Count;
        var filterPlan = ResolveFilterPlan(proposal, subExposure, availableFilters, defaultNarrowbandSubExposure, score.OptimalAltitude);

        primaryList.Add(new ScheduledObservation(
            proposal.Target, start, duration, acrossMeridian, filterPlan, gain, offset, proposal.Priority));

        AttachSpares(slotIndex, start, duration, subExposure, gain, offset,
            availableFilters, defaultNarrowbandSubExposure, spares, minHeightAboveHorizon, sparesMap);
    }

    private static void ScheduleMosaicGroup(
        List<(ProposedObservation Proposal, ScoredTarget Score)> mosaicGroup,
        ProposedObservation representativeProposal,
        ScoredTarget representativeScore,
        DateTimeOffset astroDark,
        TimeSpan defaultObservationTime,
        TimeSpan defaultSubExposure,
        int defaultGain,
        int defaultOffset,
        bool[] allocatedBins,
        int binCount,
        double siderealTimeAtAstroDark,
        IReadOnlyList<InstalledFilter>? availableFilters,
        TimeSpan? defaultNarrowbandSubExposure,
        ImmutableArray<ScheduledObservation>.Builder primaryList,
        ImmutableDictionary<int, ImmutableArray<ScheduledObservation>>.Builder sparesMap,
        List<(ProposedObservation Proposal, ScoredTarget Score)> spares,
        byte minHeightAboveHorizon)
    {
        var perPanelTime = representativeProposal.ObservationTime ?? defaultObservationTime;
        var totalBinsNeeded = Math.Max(1, mosaicGroup.Count * Math.Max(1, (int)Math.Ceiling(perPanelTime / TimeBinDuration)));

        var bestStartBin = FindBestStartBin(allocatedBins, totalBinsNeeded, representativeScore.OptimalStart, astroDark, binCount);
        if (bestStartBin < 0)
        {
            return;
        }

        for (var b = bestStartBin; b < bestStartBin + totalBinsNeeded && b < binCount; b++)
        {
            allocatedBins[b] = true;
        }

        var blockStart = astroDark + TimeBinDuration * bestStartBin;
        var binsPerPanel = Math.Max(1, (int)Math.Ceiling(perPanelTime / TimeBinDuration));
        var subExposure = representativeProposal.SubExposure ?? defaultSubExposure;
        var gain = representativeProposal.Gain ?? defaultGain;
        var offset = representativeProposal.Offset ?? defaultOffset;

        // Panels are already sorted by RA ascending in the mosaic group
        for (var i = 0; i < mosaicGroup.Count; i++)
        {
            var (panelProposal, panelScore) = mosaicGroup[i];
            var panelStart = blockStart + perPanelTime * i;
            var panelDuration = perPanelTime;

            // Determine AcrossMeridian per panel using its individual transit time
            var panelHA = CoordinateUtils.ConditionHA(siderealTimeAtAstroDark - panelProposal.Target.RA);
            var panelCrossMeridianTime = astroDark - TimeSpan.FromHours(panelHA);
            var acrossMeridian = panelCrossMeridianTime >= panelStart
                && panelCrossMeridianTime <= panelStart + panelDuration;

            var panelSubExposure = panelProposal.SubExposure ?? subExposure;
            var filterPlan = ResolveFilterPlan(panelProposal, panelSubExposure, availableFilters,
                defaultNarrowbandSubExposure, panelScore.OptimalAltitude > 0 ? panelScore.OptimalAltitude : representativeScore.OptimalAltitude);
            var panelGain = panelProposal.Gain ?? gain;
            var panelOffset = panelProposal.Offset ?? offset;

            var slotIndex = primaryList.Count;
            primaryList.Add(new ScheduledObservation(
                panelProposal.Target, panelStart, panelDuration, acrossMeridian, filterPlan,
                panelGain, panelOffset, panelProposal.Priority));

            AttachSpares(slotIndex, panelStart, panelDuration, panelSubExposure, panelGain, panelOffset,
                availableFilters, defaultNarrowbandSubExposure, spares, minHeightAboveHorizon, sparesMap);
        }
    }

    private static ImmutableArray<FilterExposure> ResolveFilterPlan(
        ProposedObservation proposal,
        TimeSpan subExposure,
        IReadOnlyList<InstalledFilter>? availableFilters,
        TimeSpan? defaultNarrowbandSubExposure,
        double optimalAltitude)
    {
        return proposal.FilterPlan is { IsEmpty: false } explicitPlan
            ? explicitPlan
            : availableFilters is { Count: > 0 }
                ? FilterPlanBuilder.BuildAutoFilterPlan(
                    availableFilters,
                    subExposure,
                    defaultNarrowbandSubExposure ?? TimeSpan.FromTicks(subExposure.Ticks * 3),
                    optimalAltitude)
                : FilterPlanBuilder.BuildSingleFilterPlan(subExposure);
    }

    private static void AttachSpares(
        int slotIndex,
        DateTimeOffset start,
        TimeSpan duration,
        TimeSpan subExposure,
        int gain,
        int offset,
        IReadOnlyList<InstalledFilter>? availableFilters,
        TimeSpan? defaultNarrowbandSubExposure,
        List<(ProposedObservation Proposal, ScoredTarget Score)> spares,
        byte minHeightAboveHorizon,
        ImmutableDictionary<int, ImmutableArray<ScheduledObservation>>.Builder sparesMap)
    {
        var slotSpares = ImmutableArray.CreateBuilder<ScheduledObservation>();
        foreach (var (spareProposal, spareScore) in spares)
        {
            if (spareScore.TotalScore <= Half.Zero)
            {
                continue;
            }

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

                var spareSubExposure = spareProposal.SubExposure ?? subExposure;
                var spareFilterPlan = ResolveFilterPlan(spareProposal, spareSubExposure, availableFilters,
                    defaultNarrowbandSubExposure, spareScore.OptimalAltitude);

                slotSpares.Add(new ScheduledObservation(
                    spareProposal.Target, start, duration, spareAcrossMeridian, spareFilterPlan,
                    spareProposal.Gain ?? gain, spareProposal.Offset ?? offset, ObservationPriority.Spare));
            }
        }

        if (slotSpares.Count > 0)
        {
            sparesMap[slotIndex] = slotSpares.ToImmutable();
        }
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
            if (cameraUri.QueryValue(DeviceQueryKey.Gain) is { Length: > 0 } gainStr && int.TryParse(gainStr, out var uriGain))
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
            if (cameraUri.QueryValue(DeviceQueryKey.Offset) is { Length: > 0 } offsetStr && int.TryParse(offsetStr, out var uriOffset))
            {
                return uriOffset;
            }
        }

        return 0;
    }

    private static readonly TimeSpan AstromGridStep = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Precomputes Astrom structs for evenly-spaced time samples across the night (every 15 minutes).
    /// Each Astrom encapsulates the expensive star-independent astrometry parameters (Epv00, Pnm06a, etc.)
    /// and can be reused across all targets at the same time point.
    /// The dense grid naturally captures meridian transit detail.
    /// </summary>
    private static (Astrom[] Astroms, DateTimeOffset[] Times) PrecomputeAstromGrid(
        DateTimeOffset astroDark,
        DateTimeOffset astroTwilight,
        double siteLat,
        double siteLong,
        double siteElevation)
    {
        var nightDuration = astroTwilight - astroDark;
        var sampleCount = Math.Max(2, (int)(nightDuration / AstromGridStep) + 1);
        var astroms = new Astrom[sampleCount];
        var times = new DateTimeOffset[sampleCount];
        var step = nightDuration / (sampleCount - 1);

        for (var i = 0; i < sampleCount; i++)
        {
            times[i] = astroDark + step * i;
            times[i].ToSOFAUtcJd(out var utc1, out var utc2);
            astroms[i] = SOFAHelpers.PrepareAstrom(utc1, utc2, siteLat, siteLong, siteElevation);
        }

        return (astroms, times);
    }

    /// <summary>
    /// Scores a target using precomputed Astrom structs.
    /// Calls only Atciq + Atioq per sample (no Apco13).
    /// </summary>
    internal static ScoredTarget ScoreTarget(
        Target target,
        ReadOnlySpan<Astrom> astroms,
        ReadOnlySpan<DateTimeOffset> times,
        DateTimeOffset astroDark,
        DateTimeOffset astroTwilight,
        byte minHeightAboveHorizon,
        double siteLong)
    {
        var profile = new Dictionary<RaDecEventTime, RaDecEventInfo>(astroms.Length + 4);
        var totalScore = 0d;
        var bestAlt = double.MinValue;
        DateTimeOffset bestTime = astroDark;
        DateTimeOffset? windowStart = null;
        DateTimeOffset? windowEnd = null;

        for (var i = 0; i < astroms.Length; i++)
        {
            var alt = SOFAHelpers.AltitudeFromAstrom(target.RA, target.Dec, in astroms[i]);
            var time = times[i];

            profile[RaDecEventTime.Balance + i] = new RaDecEventInfo(time, alt);

            if (alt > minHeightAboveHorizon)
            {
                totalScore += alt - minHeightAboveHorizon;

                windowStart ??= time;
                windowEnd = time;

                if (alt > bestAlt)
                {
                    bestAlt = alt;
                    bestTime = time;
                }
            }
        }

        // Add landmark events using the nearest precomputed sample
        profile[RaDecEventTime.AstroDark] = profile[RaDecEventTime.Balance]; // first sample ≈ astroDark
        profile[RaDecEventTime.AstroTwilight] = profile[RaDecEventTime.Balance + astroms.Length - 1]; // last ≈ astroTwilight

        // Estimate meridian crossing from RA and LST (skip for targets with unknown RA, e.g. solar system objects)
        var siderealTimeAtAstroDark = Transform.CalculateLocalSiderealTime(astroDark.UtcDateTime, siteLong);
        var conditionedHA = CoordinateUtils.ConditionHA(siderealTimeAtAstroDark - target.RA);
        if (!double.IsNaN(conditionedHA))
        {
            var hourAngle = TimeSpan.FromHours(conditionedHA);
            var crossMeridianTime = astroDark - hourAngle;
            // Find the closest precomputed sample to the meridian time
            var meridianIdx = FindClosestTimeIndex(times, crossMeridianTime);
            profile[RaDecEventTime.Meridian] = new RaDecEventInfo(crossMeridianTime, profile[RaDecEventTime.Balance + meridianIdx].Alt);
        }

        var optimalStart = windowStart ?? astroDark;
        var optimalDuration = windowEnd.HasValue && windowStart.HasValue
            ? windowEnd.Value - windowStart.Value
            : TimeSpan.Zero;

        return new ScoredTarget(target, (Half)totalScore, Half.One, profile, optimalStart, optimalDuration, bestAlt == double.MinValue ? 0 : bestAlt);
    }

    private static int FindClosestTimeIndex(ReadOnlySpan<DateTimeOffset> times, DateTimeOffset target)
    {
        var bestIdx = 0;
        var bestDiff = Math.Abs((times[0] - target).TotalSeconds);
        for (var i = 1; i < times.Length; i++)
        {
            var diff = Math.Abs((times[i] - target).TotalSeconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    /// <summary>
    /// Enumerates tonight's best deep-sky targets from the catalog, ranked by a combination
    /// of altitude score and object desirability (size, brightness, type).
    /// Uses precomputed Astrom structs to avoid redundant Apco13 calls across targets.
    /// </summary>
    public static IEnumerable<ScoredTarget> TonightsBest(
        ICelestialObjectDB objectDb,
        Transform transform,
        byte minHeightAboveHorizon)
    {
        var (astroDark, astroTwilight) = CalculateNightWindow(transform);

        // Precompute Astrom structs for evenly-spaced time samples across the night.
        // This is the expensive part (Epv00 + Pnm06a per sample) — done once for all targets.
        var (astroms, times) = PrecomputeAstromGrid(
            astroDark, astroTwilight,
            transform.SiteLatitude, transform.SiteLongitude, transform.SiteElevation);

        // Precompute a single Astrom at astronomical midnight for the quick pre-filter.
        var midIdx = astroms.Length / 2;
        var midAstrom = astroms[midIdx];

        // Astronomical midnight = midpoint of the night
        var astroMidnight = astroDark + (astroTwilight - astroDark) / 2;

        // LST at astronomical midnight = RA on the meridian
        var lstAtMidnight = Transform.CalculateLocalSiderealTime(astroMidnight.UtcDateTime, transform.SiteLongitude);

        // RA band: ±half night duration (objects transit during the night)
        var halfNightHours = (astroTwilight - astroDark).TotalHours / 2;
        var raMin = lstAtMidnight - halfNightHours;
        var raMax = lstAtMidnight + halfNightHours;

        // Dec band: visible from site latitude above minAlt
        var lat = transform.SiteLatitude;
        var decMin = Math.Max(-90, lat - (90 - minHeightAboveHorizon));
        var decMax = Math.Min(90, lat + (90 - minHeightAboveHorizon));

        var grid = objectDb.DeepSkyCoordinateGrid;
        var seen = new HashSet<CatalogIndex>();
        var candidates = new SortedSet<ScoredTarget>();

        // Iterate RA/Dec grid cells (1° RA resolution = 1/15 hour steps)
        for (var ra = raMin; ra <= raMax; ra += 1.0 / 15)
        {
            var wrappedRa = ((ra % 24) + 24) % 24; // wrap to [0, 24)
            for (var dec = decMin; dec <= decMax; dec += 1.0)
            {
                ScanGridCell(grid, wrappedRa, dec, objectDb, in midAstrom, astroms, times, astroDark, astroTwilight,
                    minHeightAboveHorizon, transform.SiteLongitude, seen, candidates);
            }
        }

        // Circumpolar sweep: objects at extreme declinations (same hemisphere as site) are
        // always above the horizon but may be missed by the RA-band search when their RA
        // is far from the night-time meridian (e.g. Magellanic Clouds from Melbourne in winter).
        const double CircumpolarMinObjectBonus = 10.0;
        var absLat = Math.Abs(lat);
        var circumpolarThresholdDec = 90 - absLat; // |dec| > this = circumpolar

        var hasCircumpolarBand = (lat < 0 && decMin < -circumpolarThresholdDec)
            || (lat > 0 && decMax > circumpolarThresholdDec);

        if (hasCircumpolarBand)
        {
            foreach (var idx in objectDb.AllObjectIndices)
            {
                if (!seen.Add(idx)) continue;
                if (!objectDb.TryLookupByIndex(idx, out var obj)) continue;

                // Check if object is in the circumpolar dec band
                var isCircumpolar = lat < 0
                    ? obj.Dec < -circumpolarThresholdDec && obj.Dec >= decMin
                    : obj.Dec > circumpolarThresholdDec && obj.Dec <= decMax;
                if (!isCircumpolar) continue;

                if (obj.ObjectType.IsStar) continue;
                if (obj.ObjectType is ObjectType.Duplicate or ObjectType.Inexistent) continue;

                var objectBonus = CalculateObjectBonus(obj, objectDb);
                if (objectBonus < CircumpolarMinObjectBonus) continue;

                // Mark cross-referenced indices as seen to avoid duplicate entries
                MarkCrossIndicesSeen(objectDb, idx, seen);

                    var target = new Target(obj.RA, obj.Dec, obj.DisplayName, idx);
                var scored = ScoreTarget(target, astroms, times, astroDark, astroTwilight, minHeightAboveHorizon, transform.SiteLongitude);
                if (scored.TotalScore <= Half.Zero) continue;

                candidates.Add(scored with { ObjectBonus = (Half)objectBonus });
            }
        }

        return candidates;
    }

    private static void ScanGridCell(
        IRaDecIndex grid,
        double ra,
        double dec,
        ICelestialObjectDB objectDb,
        in Astrom midAstrom,
        ReadOnlySpan<Astrom> astroms,
        ReadOnlySpan<DateTimeOffset> times,
        DateTimeOffset astroDark,
        DateTimeOffset astroTwilight,
        byte minHeightAboveHorizon,
        double siteLong,
        HashSet<CatalogIndex> seen,
        SortedSet<ScoredTarget> candidates)
    {
        foreach (var idx in grid[ra, dec])
        {
            if (!seen.Add(idx)) continue;
            if (!objectDb.TryLookupByIndex(idx, out var obj)) continue;
            if (obj.ObjectType.IsStar) continue;
            if (obj.ObjectType is ObjectType.Duplicate or ObjectType.Inexistent) continue;

            var objectBonus = CalculateObjectBonus(obj, objectDb);
            if (objectBonus <= 0) continue;

            // Mark cross-referenced indices as seen to avoid duplicate entries
            MarkCrossIndicesSeen(objectDb, idx, seen);

            // Quick altitude check at astronomical midnight using the precomputed Astrom.
            // No Apco13 overhead — just Atciq + Atioq.
            var quickAlt = SOFAHelpers.AltitudeFromAstrom(obj.RA, obj.Dec, in midAstrom);
            if (quickAlt < minHeightAboveHorizon - 10) continue; // 10° margin for objects rising/setting

            var target = new Target(obj.RA, obj.Dec, obj.DisplayName, idx);
            var scored = ScoreTarget(target, astroms, times, astroDark, astroTwilight, minHeightAboveHorizon, siteLong);
            if (scored.TotalScore <= Half.Zero) continue;

            candidates.Add(scored with { ObjectBonus = (Half)objectBonus });
        }
    }

    private static void MarkCrossIndicesSeen(ICelestialObjectDB objectDb, CatalogIndex idx, HashSet<CatalogIndex> seen)
    {
        if (objectDb.TryGetCrossIndices(idx, out var crossIndices))
        {
            foreach (var crossIdx in crossIndices)
            {
                seen.Add(crossIdx);
            }
        }
    }

    private static double CalculateObjectBonus(in CelestialObject obj, ICelestialObjectDB objectDb)
    {
        // Size score (arcmin, log-scaled)
        var sizeScore = 1.0;
        if (objectDb.TryGetShape(obj.Index, out var shape) && (double)shape.MajorAxis > 0)
        {
            sizeScore = Math.Log2(1 + (double)shape.MajorAxis);
        }

        // Surface brightness score (lower mag/arcsec² = brighter = better)
        var brightnessScore = !Half.IsNaN(obj.SurfaceBrightness)
            ? Math.Max(0, 25 - (double)obj.SurfaceBrightness)
            : 5.0;

        // Type bonus: planetary nebulae are compact but visually rich
        var typeBonusFactor = obj.ObjectType is ObjectType.PlanetaryNeb ? 2.0 : 1.0;

        // Named object bonus: objects with common names (M13, M42, LMC, etc.) are
        // the ones users most want to image — boost them significantly in ranking.
        var nameFactor = obj.CommonNames.Count > 0 ? 3.0 : 1.0;

        return sizeScore * brightnessScore * typeBonusFactor * nameFactor;
    }
}
