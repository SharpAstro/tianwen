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
        var loadedFromPath = filePath;

        if (dto is null)
        {
            // Fall back to the most recent prior-day file in the same profile directory.
            // The filename is keyed by the evening date (AstronomicalEveningDate), so
            // crossing an evening boundary (e.g. noon) opens a different file. Without
            // this fallback, users would "lose" their pinned targets every time the
            // evening key rolled forward to a date that hasn't been saved yet. The
            // prior-day pins are still perfectly valid — they get matched against
            // the current object database and re-saved to today's key on the next save.
            var fallbackPath = FindMostRecentPriorSession(filePath);
            if (fallbackPath is not null)
            {
                dto = await external.TryReadJsonAsync(
                    fallbackPath,
                    PlannerJsonContext.Default.PlannerSessionDto, logger, ct);
                if (dto is not null)
                {
                    loadedFromPath = fallbackPath;
                    logger.LogInformation("PlannerPersistence: no session at {FilePath}, carrying forward from {FallbackPath}",
                        filePath, fallbackPath);
                }
            }
        }

        if (dto is null)
        {
            logger.LogInformation("PlannerPersistence: no saved session at {FilePath}", filePath);
            return false;
        }

        logger.LogInformation("PlannerPersistence: loaded {Count} proposals from {FilePath}", dto.Proposals.Length, loadedFromPath);

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
            var target = MatchTarget(p, targetLookup, allTargets, state.ObjectDb);
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
        List<Target> allTargets,
        ICelestialObjectDB? objectDb)
    {
        // Primary: exact CatalogIndex match against TonightsBest + SearchResults
        if (proposal.CatalogIndex.HasValue
            && catalogLookup.TryGetValue((CatalogIndex)proposal.CatalogIndex.Value, out var catalogMatch))
        {
            return catalogMatch;
        }

        // Fallback 1: name + proximity against TonightsBest + SearchResults
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

        // Fallback 2: rebuild the Target from the object database by catalog index.
        // TonightsBest is scored + capped, so a pinned target that is still valid
        // can easily fall off its list (e.g. lower altitude on a different evening).
        // The saved proposal carries its CatalogIndex, Name, RA and Dec — everything
        // we need to reconstruct a Target. Without this fallback the pin gets dropped
        // every time it is not in tonight's top-N, and the user sees it "vanish"
        // across a day rollover even though the save on disk is fine.
        if (objectDb is not null && proposal.CatalogIndex.HasValue)
        {
            var idx = (CatalogIndex)proposal.CatalogIndex.Value;
            if (objectDb.TryLookupByIndex(idx, out var obj))
            {
                return new Target(obj.RA, obj.Dec, proposal.Name, idx);
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

    /// <summary>
    /// Returns the path to the newest <c>YYYY-MM-DD.json</c> session file strictly older
    /// than <paramref name="currentFilePath"/> in the same profile directory, or null
    /// when none exist. Used on load when the current session's file does not exist yet
    /// so pinned targets carry forward across evening-date rollovers.
    /// </summary>
    private static string? FindMostRecentPriorSession(string currentFilePath)
    {
        var dir = Path.GetDirectoryName(currentFilePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return null;
        }

        var currentName = Path.GetFileNameWithoutExtension(currentFilePath);
        string? best = null;
        string? bestName = null;
        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // Only accept strictly-older YYYY-MM-DD.json siblings (string compare works
            // for the ISO date format). Skips the current file and any non-date files.
            if (name.Length != 10 || !DateOnly.TryParse(name, out _)) continue;
            if (string.CompareOrdinal(name, currentName) >= 0) continue;

            if (bestName is null || string.CompareOrdinal(name, bestName) > 0)
            {
                best = path;
                bestName = name;
            }
        }
        return best;
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
