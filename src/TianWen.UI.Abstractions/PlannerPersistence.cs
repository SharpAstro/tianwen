using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using TianWen.Lib.IO;
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
    /// <param name="remoteBindingId">The rig this plan belongs to, or null for this computer's own
    /// plan. See <see cref="GetSessionFilePath"/> for why remote pins are scoped separately.</param>
    public static Task SaveAsync(PlannerState state, Profile profile, IExternal external, ITimeProvider timeProvider, Guid? remoteBindingId, CancellationToken ct)
        => external.AtomicWriteJsonAsync(
            GetSessionFilePath(profile, state, external, timeProvider, remoteBindingId),
            CreateDto(state),
            PlannerJsonContext.Default.PlannerSessionDto,
            ct);

    /// <summary>
    /// Attempts to load a previously saved planner session. Returns true if state was restored.
    /// Validates site coordinates and matches saved targets against the current TonightsBest list.
    /// </summary>
    /// <param name="remoteBindingId">The rig this plan belongs to, or null for this computer's own plan.</param>
    public static async Task<bool> TryLoadAsync(PlannerState state, Profile? profile, IExternal external, ILogger logger, ITimeProvider timeProvider, Guid? remoteBindingId, CancellationToken ct)
    {
        if (profile is null)
        {
            return false;
        }

        var filePath = GetSessionFilePath(profile, state, external, timeProvider, remoteBindingId);
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
            // prior-day pins are still perfectly valid: they get matched against
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

        return TryRestoreFromDto(state, dto, logger);
    }

    /// <summary>
    /// Serializes the current planner session (pins, sliders, settings, site) to JSON using the
    /// same DTO the file store writes. Storage-agnostic counterpart of <see cref="SaveAsync"/> for
    /// hosts without a profile/file store (the browser host persists this string to localStorage).
    /// </summary>
    public static string SerializeToJson(PlannerState state)
        => System.Text.Json.JsonSerializer.Serialize(CreateDto(state), PlannerJsonContext.Default.PlannerSessionDto);

    /// <summary>
    /// Restores a planner session from a JSON string produced by <see cref="SerializeToJson"/>.
    /// Returns true if state was restored. Storage-agnostic counterpart of <see cref="TryLoadAsync"/>.
    /// </summary>
    public static bool TryRestoreFromJson(PlannerState state, string json, ILogger logger)
    {
        PlannerSessionDto? dto;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize(json, PlannerJsonContext.Default.PlannerSessionDto);
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(ex, "PlannerPersistence: discarding unparseable saved session");
            return false;
        }

        return dto is not null && TryRestoreFromDto(state, dto, logger);
    }

    /// <summary>
    /// The CANDIDATE SET of a completed sweep -- target, name, catalog index and object type per
    /// entry, and nothing else -- so a later run can skip the catalog scan and re-score it for the
    /// night in question.
    ///
    /// <para><b>Only the candidates, deliberately.</b> Scores, altitude profiles and the night window
    /// are all functions of the date, and <see cref="PlannerActions.RecomputeForDate"/> rebuilds every
    /// one of them from the target list alone. Persisting them would multiply the payload by the
    /// profile arrays and then have it thrown away on restore. Measured on the deployed browser build:
    /// the full sweep is ~1520 ms, of which the catalog scan is the great majority -- the rescore that
    /// replaces it here is ~170 ms.</para>
    /// </summary>
    public static string SerializeTonightsBest(
        PlannerState state, double siteLatitude, double siteLongitude, DateTimeOffset computedFor)
    {
        var candidates = new CandidateDto[state.TonightsBest.Length];
        for (var i = 0; i < candidates.Length; i++)
        {
            var t = state.TonightsBest[i];
            candidates[i] = new CandidateDto(
                t.Target.RA, t.Target.Dec, t.Target.Name, (ulong?)t.Target.CatalogIndex, t.ObjectType);
        }

        var dto = new TonightsBestCacheDto(
            TonightsBestCacheVersion, siteLatitude, siteLongitude,
            state.MinHeightAboveHorizon, computedFor, candidates);
        return System.Text.Json.JsonSerializer.Serialize(dto, PlannerJsonContext.Default.TonightsBestCacheDto);
    }

    /// <summary>
    /// Restores a cached candidate set into <see cref="PlannerState.TonightsBest"/> as UNSCORED
    /// entries. The caller MUST follow with <see cref="PlannerActions.RecomputeForDate"/>, which
    /// replaces every entry with one scored for the current night -- until it runs, the list carries
    /// real targets and zero scores.
    ///
    /// <para>Refused when the schema version, the minimum altitude or the site (beyond
    /// <see cref="SiteInvalidationThreshold"/>) differ, or when the cached night is more than
    /// <see cref="TonightsBestCacheMaxAgeDays"/> from the one being planned.</para>
    /// </summary>
    public static bool TryRestoreTonightsBest(
        PlannerState state, string json,
        double siteLatitude, double siteLongitude, byte minHeightAboveHorizon,
        DateTimeOffset plannedFor, ILogger logger)
    {
        TonightsBestCacheDto? dto;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize(json, PlannerJsonContext.Default.TonightsBestCacheDto);
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(ex, "PlannerPersistence: discarding unparseable tonight's-best cache");
            return false;
        }

        if (dto is null || dto.Version != TonightsBestCacheVersion || dto.Candidates.Length == 0)
        {
            return false;
        }

        if (dto.MinHeightAboveHorizon != minHeightAboveHorizon
            || Math.Abs(dto.SiteLatitude - siteLatitude) > SiteInvalidationThreshold
            || Math.Abs(dto.SiteLongitude - siteLongitude) > SiteInvalidationThreshold)
        {
            return false;
        }

        // The candidate set is "what is above the horizon during the dark window", which drifts by
        // about four minutes of RA a day -- so a week is ~28 minutes, comfortably inside the slack a
        // hundred-entry list has. Past that the sky genuinely has different things in it and the scan
        // has to run again.
        if (Math.Abs((dto.ComputedFor - plannedFor).TotalDays) > TonightsBestCacheMaxAgeDays)
        {
            return false;
        }

        var restored = ImmutableArray.CreateBuilder<ScoredTarget>(dto.Candidates.Length);
        foreach (var c in dto.Candidates)
        {
            var target = new Target(c.RA, c.Dec, c.Name, (CatalogIndex?)c.CatalogIndex);
            restored.Add(new ScoredTarget(
                target, Half.Zero, Half.Zero,
                EmptyElevationProfile, default, TimeSpan.Zero, ObjectType: c.ObjectType));
        }

        state.TonightsBest = restored.MoveToImmutable();
        return true;
    }

    /// <summary>Bumped whenever <see cref="CandidateDto"/> changes shape, so an old payload is
    /// discarded rather than deserialized into something that no longer means the same thing.</summary>
    private const int TonightsBestCacheVersion = 1;

    /// <summary>See <see cref="TryRestoreTonightsBest"/> for why a week.</summary>
    public const double TonightsBestCacheMaxAgeDays = 7.0;

    private static readonly IReadOnlyDictionary<RaDecEventTime, RaDecEventInfo> EmptyElevationProfile
        = new Dictionary<RaDecEventTime, RaDecEventInfo>();

    /// <summary>
    /// Applies a loaded session DTO to the planner state: validates the site, matches saved
    /// proposals against the current target lists / object DB, and restores slider positions
    /// that still fall inside tonight's window. Requires <see cref="PlannerState.TonightsBest"/>
    /// and the night window to be computed first. Returns true if state was restored.
    /// </summary>
    public static bool TryRestoreFromDto(PlannerState state, PlannerSessionDto dto, ILogger logger)
    {
        // Site invalidation: if saved site differs by >1° from current, discard
        if (Math.Abs(dto.SiteLatitude - state.SiteLatitude) > SiteInvalidationThreshold
            || Math.Abs(dto.SiteLongitude - state.SiteLongitude) > SiteInvalidationThreshold)
        {
            logger.LogWarning("PlannerPersistence: discarding saved session, site moved ({SavedLat:F1},{SavedLon:F1}) → ({CurrentLat:F1},{CurrentLon:F1})",
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
            logger.LogWarning("PlannerPersistence: no proposals could be matched, discarding saved session");
            return false;
        }

        // Restore state: atomic replacement of Proposals. Building the whole list
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
        if (dto.Sliders.Length == state.HandoffSliders.Length
            && dto.Sliders.All(s => s >= state.AstroDark && s <= state.AstroTwilight))
        {
            state.HandoffSliders = [.. dto.Sliders];
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
        // The saved proposal carries its CatalogIndex, Name, RA and Dec; everything
        // we need to reconstruct a Target. Without this fallback the pin gets dropped
        // every time it is not in tonight's top-N, and the user sees it "vanish"
        // across a day rollover even though the save on disk is fine.
        if (objectDb is not null && proposal.CatalogIndex.HasValue)
        {
            var idx = (CatalogIndex)proposal.CatalogIndex.Value;
            if (objectDb.TryLookupByIndex(idx, out var obj))
            {
                // A solar-system body is stored in the DB with NaN coordinates -- its RA/Dec is
                // ephemeris-computed, so the catalog has no fixed position to hand back. Taking them
                // verbatim produced a NaN-positioned target that matched no scored entry and no
                // altitude profile, i.e. an invisible, unremovable pin. The saved proposal's own RA/Dec
                // is a real position (whatever it was when pinned), and the planner recomputes the
                // live one anyway, so prefer it whenever the catalog's is not a number.
                var ra = double.IsNaN(obj.RA) ? proposal.RA : obj.RA;
                var dec = double.IsNaN(obj.Dec) ? proposal.Dec : obj.Dec;
                return new Target(ra, dec, proposal.Name, idx);
            }
        }

        // Fallback 3: a solar-system body that is not in the object DB at all (e.g. a comet, which is
        // never in it by design -- every consumer augments from ICometRepository at its own layer).
        // The saved proposal still fully describes it, and dropping it here is what silently discarded
        // a pinned comet on restore.
        if (proposal.CatalogIndex is { } savedIdx && ((CatalogIndex)savedIdx).IsSolarSystemObject)
        {
            return new Target(proposal.RA, proposal.Dec, proposal.Name, (CatalogIndex)savedIdx);
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
        foreach (var path in FileEnumeration.EnumerateFiles(dir, ".json", recursive: false))
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

    /// <summary>
    /// Where one night's plan lives: <c>Planner/{profileId}/{date}.json</c> locally, and
    /// <c>Planner/rigs/{bindingId}/{profileId}/{date}.json</c> for a bound rig.
    /// <para>
    /// <b>Why rigs are scoped by binding id.</b> A profile id alone is not unique across machines -- a
    /// user who copies a rig's profile to a second rig (or to this computer) gives two different
    /// contexts the same id, and their pinned targets would then merge into one file. The binding id is
    /// unique per rig by construction.
    /// </para>
    /// <para>
    /// <b>Local paths are deliberately unchanged</b> rather than moved under a <c>local/</c> sibling:
    /// re-keying them would orphan every pin a user already has, for no benefit.
    /// </para>
    /// </summary>
    private static string GetSessionFilePath(Profile profile, PlannerState state, IExternal external, ITimeProvider? timeProvider = null, Guid? remoteBindingId = null)
    {
        // Use the site's local date (not the machine's) so the file key matches
        // the "tonight" definition from CalculateNightWindow (site-timezone-aware).
        var siteNow = (timeProvider ?? SystemTimeProvider.Instance).GetUtcNow().ToOffset(state.SiteTimeZone);
        var date = state.PlanningDate?.Date ?? CoordinateUtils.AstronomicalEveningDate(siteNow);
        var profileId = profile.ProfileId.ToString("D");
        var dateStr = date.ToString("yyyy-MM-dd");

        return remoteBindingId is { } bindingId
            ? Path.Combine(external.AppDataFolder.FullName, "Planner", "rigs", bindingId.ToString("D"), profileId, dateStr + ".json")
            : Path.Combine(external.AppDataFolder.FullName, "Planner", profileId, dateStr + ".json");
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

/// <summary>
/// DTO for a cached tonight's-best CANDIDATE SET. Carries the guards it is validated against
/// (schema version, site, minimum altitude, the night it was computed for) so a stale or
/// foreign payload is refused rather than silently believed.
/// </summary>
public record TonightsBestCacheDto(
    int Version,
    double SiteLatitude,
    double SiteLongitude,
    byte MinHeightAboveHorizon,
    DateTimeOffset ComputedFor,
    CandidateDto[] Candidates);

/// <summary>One swept candidate: everything <see cref="PlannerActions.RecomputeForDate"/> needs to
/// score it, and nothing that a score or a date would invalidate.</summary>
public record CandidateDto(
    double RA,
    double Dec,
    string Name,
    ulong? CatalogIndex,
    ObjectType ObjectType);

[JsonSerializable(typeof(PlannerSessionDto))]
[JsonSerializable(typeof(TonightsBestCacheDto))]
internal partial class PlannerJsonContext : JsonSerializerContext
{
}
