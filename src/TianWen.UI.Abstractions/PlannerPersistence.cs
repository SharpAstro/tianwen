using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Persists and restores planner session state (pinned targets, sliders, settings)
/// keyed by profile + date. Files stored under {AppDataFolder}/Planner/{profileId}/{date}.json.
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
    public static Task SaveAsync(PlannerState state, Profile profile, IExternal external, ITimeProvider timeProvider, CancellationToken ct)
        => external.AtomicWriteJsonAsync(
            GetSessionFilePath(profile, state, external, timeProvider),
            CreateDto(state),
            PlannerJsonContext.Default.PlannerSessionDto,
            ct);

    /// <summary>
    /// Attempts to load a previously saved planner session. Returns true if state was restored.
    /// Validates site coordinates and matches saved targets against the current TonightsBest list.
    /// </summary>
    public static async Task<bool> TryLoadAsync(PlannerState state, Profile? profile, IExternal external, ILogger logger, ITimeProvider timeProvider, CancellationToken ct)
    {
        if (profile is null)
        {
            return false;
        }

        var filePath = GetSessionFilePath(profile, state, external, timeProvider);
        var dto = await external.TryReadJsonAsync(
            filePath,
            PlannerJsonContext.Default.PlannerSessionDto, logger, ct);

        if (dto is null)
        {
            logger.LogInformation("PlannerPersistence: no saved session at {FilePath}", filePath);
            return false;
        }

        logger.LogInformation("PlannerPersistence: loaded {Count} proposals from {FilePath}", dto.Proposals.Length, filePath);

        // Site invalidation: if saved site differs by >1° from current, discard
        if (Math.Abs(dto.SiteLatitude - state.SiteLatitude) > SiteInvalidationThreshold
            || Math.Abs(dto.SiteLongitude - state.SiteLongitude) > SiteInvalidationThreshold)
        {
            logger.LogWarning("PlannerPersistence: discarding saved session — site moved ({SavedLat:F1},{SavedLon:F1}) → ({CurrentLat:F1},{CurrentLon:F1})",
                dto.SiteLatitude, dto.SiteLongitude, state.SiteLatitude, state.SiteLongitude);
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
            else
            {
                logger.LogWarning("PlannerPersistence: could not match saved target '{Name}' (RA={RA:F3}h Dec={Dec:F1}°) to any current target",
                    p.Name, p.RA, p.Dec);
            }
        }

        if (restoredProposals.Count == 0)
        {
            logger.LogWarning("PlannerPersistence: no proposals could be matched — discarding saved session");
            return false;
        }

        // Restore state — atomic replacement of Proposals. Building the whole list
        // locally first and assigning once keeps concurrent readers on a consistent
        // snapshot.
        state.Proposals = [.. restoredProposals];
        state.MinHeightAboveHorizon = dto.MinHeightAboveHorizon;
        state.MinRatingFilter = dto.MinRatingFilter;

        // Sort proposals by peak altitude time and recompute sliders
        PlannerActions.SortProposalsByPeakTime(state);
        PlannerActions.RecomputeHandoffSliders(state);

        logger.LogInformation("PlannerPersistence: restored {Restored}/{Total} proposals",
            restoredProposals.Count, dto.Proposals.Length);

        // Restore saved slider positions if count matches and they fall within the current night window
        if (dto.Sliders.Length == state.HandoffSliders.Count
            && dto.Sliders.All(s => s >= state.AstroDark && s <= state.AstroTwilight))
        {
            for (var i = 0; i < dto.Sliders.Length; i++)
            {
                state.HandoffSliders[i] = dto.Sliders[i];
            }
            logger.LogInformation("PlannerPersistence: restored {Count} slider positions", dto.Sliders.Length);
        }
        else if (dto.Sliders.Length > 0)
        {
            logger.LogWarning("PlannerPersistence: discarding {Count} saved sliders (count mismatch or outside night window {Dark}–{Twilight})",
                dto.Sliders.Length, state.AstroDark, state.AstroTwilight);
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
        var proposals = new ProposalDto[state.Proposals.Length];
        for (var i = 0; i < state.Proposals.Length; i++)
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

    private static string GetSessionFilePath(Profile profile, PlannerState state, IExternal external, ITimeProvider? timeProvider = null)
    {
        // Use the site's local date (not the machine's) so the file key matches
        // the "tonight" definition from CalculateNightWindow (site-timezone-aware).
        var siteNow = (timeProvider ?? SystemTimeProvider.Instance).GetUtcNow().ToOffset(state.SiteTimeZone);
        var date = state.PlanningDate?.Date ?? CoordinateUtils.AstronomicalEveningDate(siteNow);
        var profileId = profile.ProfileId.ToString("D");
        var dateStr = date.ToString("yyyy-MM-dd");

        return Path.Combine(external.AppDataFolder.FullName, "Planner", profileId, dateStr + ".json");
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
