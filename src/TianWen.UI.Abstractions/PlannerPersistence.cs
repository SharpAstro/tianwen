using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Persists and restores planner session state (pinned targets, sliders, settings)
/// keyed by profile + date. Files stored under {OutputFolder}/Planner/{profileId}/{date}.json.
/// </summary>
public static class PlannerPersistence
{
    /// <summary>Maximum site coordinate drift (degrees) before discarding a saved plan.</summary>
    private const double SiteInvalidationThreshold = 1.0;

    /// <summary>Maximum RA/Dec proximity (degrees) for fallback name+position matching.</summary>
    private const double ProximityThresholdDeg = 1.0 / 60.0; // 1 arcmin

    /// <summary>
    /// Saves the current planner state to disk.
    /// </summary>
    public static Task SaveAsync(PlannerState state, Profile profile, IExternal external, CancellationToken ct)
        => external.AtomicWriteJsonAsync(
            GetSessionFilePath(profile, state, external),
            CreateDto(state),
            PlannerJsonContext.Default.PlannerSessionDto,
            ct);

    /// <summary>
    /// Attempts to load a previously saved planner session. Returns true if state was restored.
    /// Validates site coordinates and matches saved targets against the current TonightsBest list.
    /// </summary>
    public static async Task<bool> TryLoadAsync(PlannerState state, Profile? profile, IExternal external, CancellationToken ct)
    {
        if (profile is null)
        {
            return false;
        }

        var dto = await external.TryReadJsonAsync(
            GetSessionFilePath(profile, state, external),
            PlannerJsonContext.Default.PlannerSessionDto, ct);

        if (dto is null)
        {
            return false;
        }

        // Site invalidation: if saved site differs by >1° from current, discard
        if (Math.Abs(dto.SiteLatitude - state.SiteLatitude) > SiteInvalidationThreshold
            || Math.Abs(dto.SiteLongitude - state.SiteLongitude) > SiteInvalidationThreshold)
        {
            return false;
        }

        // Build lookup from current TonightsBest + SearchResults for target matching
        var targetLookup = new Dictionary<CatalogIndex, Target>();
        var allTargets = new List<Target>();

        foreach (var scored in state.TonightsBest)
        {
            if (scored.Target.CatalogIndex is { } idx)
            {
                targetLookup.TryAdd(idx, scored.Target);
            }
            allTargets.Add(scored.Target);
        }

        foreach (var scored in state.SearchResults)
        {
            if (scored.Target.CatalogIndex is { } idx)
            {
                targetLookup.TryAdd(idx, scored.Target);
            }
            allTargets.Add(scored.Target);
        }

        // Match saved proposals to current targets
        var restoredProposals = new List<ProposedObservation>();
        foreach (var p in dto.Proposals)
        {
            var target = MatchTarget(p, targetLookup, allTargets);
            if (target is not null)
            {
                restoredProposals.Add(new ProposedObservation(
                    target,
                    Priority: p.Priority,
                    SubExposure: p.SubExposureSeconds.HasValue ? TimeSpan.FromSeconds(p.SubExposureSeconds.Value) : null,
                    ObservationTime: p.ObservationTimeMinutes.HasValue ? TimeSpan.FromMinutes(p.ObservationTimeMinutes.Value) : null,
                    MosaicGroupId: p.MosaicGroupId));
            }
        }

        if (restoredProposals.Count == 0)
        {
            return false;
        }

        // Restore state
        state.Proposals.Clear();
        state.Proposals.AddRange(restoredProposals);
        state.MinHeightAboveHorizon = dto.MinHeightAboveHorizon;
        state.MinRatingFilter = dto.MinRatingFilter;

        // Recompute sliders from restored proposals first (to get correct PinnedCount)
        PlannerActions.RecomputeHandoffSliders(state);

        // Restore saved slider positions if count matches
        if (dto.Sliders.Length == state.HandoffSliders.Count)
        {
            for (var i = 0; i < dto.Sliders.Length; i++)
            {
                state.HandoffSliders[i] = dto.Sliders[i];
            }
        }

        state.NeedsRedraw = true;
        return true;
    }

    private static Target? MatchTarget(
        ProposalDto proposal,
        Dictionary<CatalogIndex, Target> catalogLookup,
        List<Target> allTargets)
    {
        // Primary: exact CatalogIndex match
        if (proposal.CatalogIndex.HasValue
            && catalogLookup.TryGetValue((CatalogIndex)proposal.CatalogIndex.Value, out var catalogMatch))
        {
            return catalogMatch;
        }

        // Fallback: name + proximity
        foreach (var target in allTargets)
        {
            if (string.Equals(target.Name, proposal.Name, StringComparison.OrdinalIgnoreCase))
            {
                var raDiff = Math.Abs(target.RA - proposal.RA) * 15.0; // RA in hours → degrees
                var decDiff = Math.Abs(target.Dec - proposal.Dec);
                if (raDiff < ProximityThresholdDeg && decDiff < ProximityThresholdDeg)
                {
                    return target;
                }
            }
        }

        return null;
    }

    private static PlannerSessionDto CreateDto(PlannerState state)
    {
        var proposals = new ProposalDto[state.Proposals.Count];
        for (var i = 0; i < state.Proposals.Count; i++)
        {
            var p = state.Proposals[i];
            proposals[i] = new ProposalDto(
                RA: p.Target.RA,
                Dec: p.Target.Dec,
                Name: p.Target.Name,
                CatalogIndex: p.Target.CatalogIndex.HasValue ? (ulong)p.Target.CatalogIndex.Value : null,
                Priority: p.Priority,
                SubExposureSeconds: p.SubExposure?.TotalSeconds,
                ObservationTimeMinutes: p.ObservationTime?.TotalMinutes,
                MosaicGroupId: p.MosaicGroupId);
        }

        return new PlannerSessionDto(
            Proposals: proposals,
            Sliders: [.. state.HandoffSliders],
            MinHeightAboveHorizon: state.MinHeightAboveHorizon,
            MinRatingFilter: state.MinRatingFilter,
            SiteLatitude: state.SiteLatitude,
            SiteLongitude: state.SiteLongitude);
    }

    private static string GetSessionFilePath(Profile profile, PlannerState state, IExternal external)
    {
        var date = state.PlanningDate?.Date ?? external.TimeProvider.GetLocalNow().Date;
        var profileId = profile.ProfileId.ToString("D");
        var dateStr = date.ToString("yyyy-MM-dd");

        return Path.Combine(external.OutputFolder.FullName, "Planner", profileId, dateStr + ".json");
    }
}

/// <summary>DTO for a saved planner session.</summary>
public record PlannerSessionDto(
    ProposalDto[] Proposals,
    DateTimeOffset[] Sliders,
    byte MinHeightAboveHorizon,
    float MinRatingFilter,
    double SiteLatitude,
    double SiteLongitude);

/// <summary>DTO for a saved proposed observation.</summary>
public record ProposalDto(
    double RA,
    double Dec,
    string Name,
    ulong? CatalogIndex,
    ObservationPriority Priority,
    double? SubExposureSeconds,
    double? ObservationTimeMinutes,
    Guid? MosaicGroupId);

[JsonSerializable(typeof(PlannerSessionDto))]
internal partial class PlannerJsonContext : JsonSerializerContext
{
}
