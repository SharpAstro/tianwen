using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.IO;

namespace TianWen.Lib.Imaging.Dataset;

/// <summary>
/// <c>tianwen dataset coverage</c>: one TSV row per discovered session stating what calibration a
/// bake would actually resolve for it -- dark, flat, flat pedestal (dark-flat or bias), dark-scaling
/// bias, bias availability, and APP bad-pixel-map presence, each with its gain/offset/temperature/
/// exposure/epoch identity.
///
/// <para><b>It answers with the production machinery, never a parallel scan.</b> Sessions come from
/// <see cref="SessionDiscovery.GroupSessions"/> and candidates from
/// <see cref="CalibrationResolver.GroupCalibration"/>; the resolved picks are the resolver's own
/// <c>Best*</c> selectors under the caller's gates (the strict gain gate, the optional temperature
/// cutoff). So a row IS what <c>dataset build</c> or <c>stack</c> would do, and the report cannot
/// drift from the pipeline the way a hand-rolled sweep would.</para>
///
/// <para>The one deliberate deviation: sessions are grouped with a minimum of ONE light so that
/// small sessions appear at all, and each row carries a <c>below_bake_min_subs</c> flag against the
/// caller's real threshold instead of being silently absent. A coverage report that hides exactly
/// the marginal sessions would answer the wrong question.</para>
/// </summary>
public static class CalibrationCoverageReport
{
    /// <summary>Days within which a flat is considered to match the session's timeframe. The same
    /// span as <see cref="CalibrationEpochs.MaxEpochGapDays"/>: a flat shot within one library-gap
    /// of the lights belongs to the same acquisition campaign.</summary>
    public const double FlatTimeframeDays = CalibrationEpochs.MaxEpochGapDays;

    /// <summary>What <see cref="WriteAsync"/> produced.</summary>
    public sealed record CoverageResult(
        string TsvPath,
        string SummaryPath,
        int Sessions,
        SessionDiscovery.DiscoveryStats Stats);

    /// <summary>Distinct-content APP bad-pixel maps of one sensor geometry found under the roots.</summary>
    private sealed record BpmCensusEntry(int Files, DateTimeOffset Latest);

    /// <summary>Scans the archive roots, resolves calibration per session, and writes
    /// <c>calibration-coverage.tsv</c> (+ a companion <c>.md</c> rollup) into
    /// <paramref name="reportDir"/>.</summary>
    public static async Task<CoverageResult> WriteAsync(
        DatasetBuildOptions options,
        string reportDir,
        ILogger? logger = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(reportDir);

        // One header-only scan feeds session grouping AND calibration grouping, exactly as the
        // dataset builder's own flow does.
        var frames = new List<(FrameInfo Frame, string Root)>();
        var sidecar = FrameMetaSidecarStats.Empty;
        foreach (var root in options.ArchiveRoots)
        {
            var source = new FitsFolderFrameSource(root, true);
            await foreach (var frame in source.EnumerateAsync(cancellationToken))
            {
                frames.Add((frame, root));
            }
            sidecar = sidecar.Add(source.SidecarStats);
            progress?.Report($"[coverage] scanned {root}: {frames.Count} FITS headers so far");
        }

        // Group with a floor of ONE light so marginal sessions get a row; the caller's real
        // threshold survives as the below_bake_min_subs flag.
        var (sessions, stats) = SessionDiscovery.GroupSessions(frames, options with { MinSubsPerSession = 1 });
        stats = stats with { Sidecar = sidecar };
        var calGroups = CalibrationResolver.GroupCalibration(FramesOnly(frames));
        var bpmCensus = BuildBpmCensus(options.ArchiveRoots, logger, cancellationToken);
        progress?.Report($"[coverage] {sessions.Length} session(s), {bpmCensus.Count} BPM sensor geometr{(bpmCensus.Count == 1 ? "y" : "ies")}");

        var darks = calGroups.GetValueOrDefault(FrameType.Dark);
        var flats = calGroups.GetValueOrDefault(FrameType.Flat);
        var biases = calGroups.GetValueOrDefault(FrameType.Bias);
        var darkFlats = calGroups.GetValueOrDefault(FrameType.DarkFlat);

        var tsvPath = Path.Combine(reportDir, "calibration-coverage.tsv");
        var summaryPath = Path.Combine(reportDir, "calibration-coverage.md");
        var rows = new List<string[]>(sessions.Length);
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(BuildRow(session, options, darks, flats, biases, darkFlats, bpmCensus));
        }

        await File.WriteAllTextAsync(tsvPath, RenderTsv(rows), Encoding.UTF8, cancellationToken);
        await File.WriteAllTextAsync(summaryPath, RenderSummary(rows, stats, options), Encoding.UTF8, cancellationToken);
        logger?.LogInformation("Coverage report: {Tsv} ({Sessions} sessions)", tsvPath, sessions.Length);
        return new CoverageResult(tsvPath, summaryPath, sessions.Length, stats);
    }

    private static IEnumerable<FrameInfo> FramesOnly(List<(FrameInfo Frame, string Root)> frames)
    {
        foreach (var (frame, _) in frames)
        {
            yield return frame;
        }
    }

    private static readonly string[] Header =
    [
        "session_id", "date", "target", "camera", "telescope", "focal_mm", "sw_creator",
        "filter", "filter_source", "lights", "exposure_s", "gain", "offset", "temp_c", "below_bake_min_subs",
        "flat_found", "flat_slug", "flat_is_master", "flat_frames", "flat_filter", "flat_filter_match",
        "flat_gain", "flat_offset", "flat_temp_c", "flat_epoch", "flat_age_days", "flat_within_30d",
        "flat_candidates", "flat_candidates_same_filter",
        "pedestal_kind", "pedestal_slug", "pedestal_frames", "pedestal_exposure_s", "pedestal_gain", "pedestal_offset",
        "darkflat_candidates",
        "dark_found", "dark_slug", "dark_is_master", "dark_frames", "dark_gain", "dark_offset",
        "dark_temp_c", "dark_exposure_s", "dark_epoch", "dark_age_days", "dark_gain_match", "dark_candidates",
        "dark_bias_needed", "dark_bias_found", "dark_bias_slug", "dark_bias_gain", "dark_bias_offset",
        "bias_groups", "bias_frames", "bias_master_present",
        "bpm_files", "bpm_latest",
    ];

    private static string[] BuildRow(
        ImagingSession session,
        DatasetBuildOptions options,
        List<CalibrationResolver.CalGroup>? darks,
        List<CalibrationResolver.CalGroup>? flats,
        List<CalibrationResolver.CalGroup>? biases,
        List<CalibrationResolver.CalGroup>? darkFlats,
        IReadOnlyDictionary<(int Width, int Height), BpmCensusEntry> bpmCensus)
    {
        var light = session.Lights[0];
        var lightKey = MasterGroupKey.FromFrame(light);
        var lightCamera = CalibrationResolver.CalTrain.Camera(light);
        var lightTrain = CalibrationResolver.CalTrain.OpticalTrain(light);
        var sessionDate = light.Meta.ExposureStartTime;

        var darkGroup = CalibrationResolver.BestDark(darks, light, options.RequireGainMatch, options.MaxDarkTemperatureDelta);
        var flatGroup = CalibrationResolver.BestFlat(flats, light);
        var pedestal = flatGroup is { IsMaster: false }
            ? CalibrationResolver.BestFlatPedestal(biases, darkFlats, darks, flatGroup)
            : null;

        // The dark-scaling bias is consulted exactly when ResolveAsync would consult it: a resolved
        // dark whose exposure differs from the lights'.
        var darkBiasNeeded = darkGroup is not null
            && darkGroup.Key.Exposure.TotalSeconds > 0.0 && lightKey.Exposure.TotalSeconds > 0.0
            && Math.Abs(darkGroup.Key.Exposure.TotalSeconds - lightKey.Exposure.TotalSeconds) > 0.01;
        var darkBias = darkBiasNeeded ? CalibrationResolver.BestDarkBias(biases, darkGroup!) : null;

        var (darkCandidates, flatCandidates, flatCandidatesSameFilter, biasGroups, biasFrames, biasMasterPresent) =
            CountAvailability(options, darks, flats, biases, lightKey, lightCamera, lightTrain);
        var darkFlatCandidates = flatGroup is null ? (int?)null : CountPedestalCandidates(darkFlats, darks, flatGroup);

        var bpm = bpmCensus.GetValueOrDefault((light.Width, light.Height));

        var filterMatch = flatGroup is null
            ? (bool?)null
            : flatGroup.Key.SameFilterAs(lightKey);
        var flatAgeDays = AgeDays(flatGroup?.EpochStart, sessionDate);
        var darkAgeDays = AgeDays(darkGroup?.EpochStart, sessionDate);
        var darkGainMatch = darkGroup is null
            ? null
            : darkGroup.Key.Gain >= 0 && lightKey.Gain >= 0
                ? (darkGroup.Key.Gain == lightKey.Gain ? "true" : "false")
                : "unknown";

        return
        [
            Clean(session.Id),
            sessionDate == default ? "" : sessionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Clean(session.Target),
            Clean(session.Camera),
            Clean(light.Meta.Telescope),
            light.Meta.FocalLength > 0 ? light.Meta.FocalLength.ToString(CultureInfo.InvariantCulture) : "",
            Clean(light.Meta.SWCreator ?? ""),
            Clean(session.FilterName),
            FilterSource(session, light),
            session.Lights.Length.ToString(CultureInfo.InvariantCulture),
            Seconds(lightKey.Exposure),
            GainText(lightKey.Gain),
            OffsetText(lightKey.Offset),
            TempText(lightKey.TemperatureC),
            Bool(session.Lights.Length < options.MinSubsPerSession),
            Bool(flatGroup is not null),
            flatGroup is null ? "" : Clean(flatGroup.Key.Slug()),
            flatGroup is null ? "" : Bool(flatGroup.IsMaster),
            flatGroup is null ? "" : flatGroup.Frames.Length.ToString(CultureInfo.InvariantCulture),
            flatGroup is null ? "" : Clean(flatGroup.Key.FilterIdentity),
            filterMatch is null ? "" : Bool(filterMatch.Value),
            flatGroup is null ? "" : GainText(flatGroup.Key.Gain),
            flatGroup is null ? "" : OffsetText(flatGroup.Key.Offset),
            flatGroup is null ? "" : TempText(flatGroup.Key.TemperatureC),
            EpochText(flatGroup),
            flatAgeDays?.ToString(CultureInfo.InvariantCulture) ?? "",
            flatAgeDays is null ? "" : Bool(flatAgeDays.Value <= FlatTimeframeDays),
            flatCandidates.ToString(CultureInfo.InvariantCulture),
            flatCandidatesSameFilter.ToString(CultureInfo.InvariantCulture),
            pedestal is null ? (flatGroup is { IsMaster: true } ? "master-flat" : "none") : PedestalKind(pedestal.Key.Type),
            pedestal is null ? "" : Clean(pedestal.Key.Slug()),
            pedestal is null ? "" : pedestal.Frames.Length.ToString(CultureInfo.InvariantCulture),
            pedestal is null ? "" : Seconds(pedestal.Key.Exposure),
            pedestal is null ? "" : GainText(pedestal.Key.Gain),
            pedestal is null ? "" : OffsetText(pedestal.Key.Offset),
            darkFlatCandidates?.ToString(CultureInfo.InvariantCulture) ?? "",
            Bool(darkGroup is not null),
            darkGroup is null ? "" : Clean(darkGroup.Key.Slug()),
            darkGroup is null ? "" : Bool(darkGroup.IsMaster),
            darkGroup is null ? "" : darkGroup.Frames.Length.ToString(CultureInfo.InvariantCulture),
            darkGroup is null ? "" : GainText(darkGroup.Key.Gain),
            darkGroup is null ? "" : OffsetText(darkGroup.Key.Offset),
            darkGroup is null ? "" : TempText(darkGroup.Key.TemperatureC),
            darkGroup is null ? "" : Seconds(darkGroup.Key.Exposure),
            EpochText(darkGroup),
            darkAgeDays?.ToString(CultureInfo.InvariantCulture) ?? "",
            darkGainMatch ?? "",
            darkCandidates.ToString(CultureInfo.InvariantCulture),
            Bool(darkBiasNeeded),
            darkBiasNeeded ? Bool(darkBias is not null) : "",
            darkBias is null ? "" : Clean(darkBias.Key.Slug()),
            darkBias is null ? "" : GainText(darkBias.Key.Gain),
            darkBias is null ? "" : OffsetText(darkBias.Key.Offset),
            biasGroups.ToString(CultureInfo.InvariantCulture),
            biasFrames.ToString(CultureInfo.InvariantCulture),
            Bool(biasMasterPresent),
            (bpm?.Files ?? 0).ToString(CultureInfo.InvariantCulture),
            bpm is null ? "" : bpm.Latest.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>Availability counts under the same gates the <c>Best*</c> selectors apply, so a
    /// <c>*_found=false</c> row also says WHY: zero candidates means nothing compatible exists,
    /// a positive count means candidates exist and something upstream (a stricter gate) refused
    /// them all.</summary>
    private static (int DarkCandidates, int FlatCandidates, int FlatSameFilter, int BiasGroups, int BiasFrames, bool BiasMasterPresent)
        CountAvailability(
            DatasetBuildOptions options,
            List<CalibrationResolver.CalGroup>? darks,
            List<CalibrationResolver.CalGroup>? flats,
            List<CalibrationResolver.CalGroup>? biases,
            MasterGroupKey lightKey,
            CalibrationResolver.CalTrain lightCamera,
            CalibrationResolver.CalTrain lightTrain)
    {
        var darkCandidates = 0;
        if (darks is not null)
        {
            foreach (var g in darks)
            {
                if (Buildable(g)
                    && CalibrationResolver.ExposureCompatible(g.Key.Exposure, lightKey.Exposure)
                    && CalibrationResolver.DimensionCompatible(g.Key, lightKey)
                    && g.Train.CameraCompatibleWith(lightCamera)
                    && CalibrationResolver.GainCompatible(g.Key, lightKey, options.RequireGainMatch)
                    && TemperatureCompatible(g.Key, lightKey, options.MaxDarkTemperatureDelta))
                {
                    darkCandidates++;
                }
            }
        }

        int flatCandidates = 0, flatSameFilter = 0;
        if (flats is not null)
        {
            foreach (var g in flats)
            {
                if (!Buildable(g) || !CalibrationResolver.DimensionCompatible(g.Key, lightKey) || !g.Train.TrainCompatibleWith(lightTrain))
                {
                    continue;
                }
                flatCandidates++;
                if (g.Key.SameFilterAs(lightKey))
                {
                    flatSameFilter++;
                }
            }
        }

        int biasGroups = 0, biasFrames = 0;
        var biasMasterPresent = false;
        if (biases is not null)
        {
            foreach (var g in biases)
            {
                if (!Buildable(g) || !CalibrationResolver.DimensionCompatible(g.Key, lightKey) || !g.Train.CameraCompatibleWith(lightCamera))
                {
                    continue;
                }
                biasGroups++;
                biasFrames += g.Frames.Length;
                biasMasterPresent |= g.IsMaster;
            }
        }

        return (darkCandidates, flatCandidates, flatSameFilter, biasGroups, biasFrames, biasMasterPresent);
    }

    /// <summary>Frames eligible as the flat's thermal pedestal, gated by EXPOSURE and never by
    /// label -- both the DarkFlat pool and the Dark pool participate, exactly as
    /// <see cref="CalibrationResolver.BestFlatPedestal"/> pools them (N.I.N.A. writes dark-flats as
    /// <c>IMAGETYP=DARK</c>, so counting the labelled pool alone would report 0 on the archives
    /// that most need the answer).</summary>
    private static int CountPedestalCandidates(
        List<CalibrationResolver.CalGroup>? darkFlats,
        List<CalibrationResolver.CalGroup>? darks,
        CalibrationResolver.CalGroup flatGroup)
    {
        if (flatGroup.Frames.Length == 0)
        {
            return 0;
        }
        var flatKey = MasterGroupKey.FromFrame(flatGroup.Frames[0]);
        var flatCamera = CalibrationResolver.CalTrain.Camera(flatGroup.Frames[0]);
        var count = 0;
        Count(darkFlats);
        Count(darks);
        return count;

        void Count(List<CalibrationResolver.CalGroup>? pool)
        {
            if (pool is null)
            {
                return;
            }
            foreach (var g in pool)
            {
                if (Buildable(g)
                    && CalibrationResolver.DimensionCompatible(g.Key, flatKey)
                    && g.Train.CameraCompatibleWith(flatCamera)
                    && CalibrationResolver.FlatPedestalExposureCompatible(g.Key.Exposure, flatKey.Exposure))
                {
                    count++;
                }
            }
        }
    }

    /// <summary>Where the session's filter identity came from. The sidecar deliberately makes a
    /// declared filter indistinguishable downstream, so provenance is recovered here by re-reading
    /// ONE light's header without sidecar resolution: a filter in the raw header is
    /// <c>header</c>, a filter the session carries that the raw header lacks is <c>sidecar</c>,
    /// and no filter at all is <c>none</c>.</summary>
    private static string FilterSource(ImagingSession session, FrameInfo light)
    {
        if (session.FilterName.Length == 0)
        {
            return "none";
        }
        return Image.TryReadFitsHeader(light.Path, out var raw) && raw.Meta.Filter.IdentityKey.Length > 0
            ? "header"
            : "sidecar";
    }

    /// <summary>APP bad-pixel maps (<c>BPM*.fits</c>) under the roots, deduplicated by CONTENT and
    /// keyed by sensor geometry. Content-first because a processing run copies its BPM into each
    /// output folder, so a raw file count would report activity, not coverage. Path exclusions do
    /// NOT apply here: BPMs live precisely in the processed-data directories the frame scan skips.</summary>
    private static Dictionary<(int Width, int Height), BpmCensusEntry> BuildBpmCensus(
        ImmutableArray<string> roots, ILogger? logger, CancellationToken cancellationToken)
    {
        var byGeometry = new Dictionary<(int, int), (HashSet<string> Hashes, DateTimeOffset Latest)>();
        foreach (var root in roots)
        {
            foreach (var path in FileEnumeration.Enumerate(root, recursive: true,
                static (ref FileSystemEntry entry) => !entry.IsDirectory
                    && entry.FileName.StartsWith("BPM", StringComparison.OrdinalIgnoreCase)
                    && entry.FileName.EndsWith(".fits", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Image.TryReadFitsHeader(path, out var info))
                {
                    logger?.LogWarning("Unreadable BPM skipped: {Path}", path);
                    continue;
                }
                // ContentDigest, not SHA-256: this counts DISTINCT bad-pixel maps within one in-memory
                // census, so it asks "same bytes?" and nothing more. Nothing is signed and nothing is
                // persisted, so the cryptographic strength was bought and never used.
                var hash = ContentDigest.OfFile(path);
                if (hash.Length == 0)
                {
                    logger?.LogWarning("BPM could not be hashed, skipped: {Path}", path);
                    continue;
                }
                var key = (info.Width, info.Height);
                var stamp = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (!byGeometry.TryGetValue(key, out var entry))
                {
                    byGeometry[key] = (new HashSet<string> { hash }, stamp);
                }
                else
                {
                    entry.Hashes.Add(hash);
                    byGeometry[key] = (entry.Hashes, stamp > entry.Latest ? stamp : entry.Latest);
                }
            }
        }
        var census = new Dictionary<(int, int), BpmCensusEntry>();
        foreach (var (key, (hashes, latest)) in byGeometry)
        {
            census[key] = new BpmCensusEntry(hashes.Count, latest);
        }
        return census;
    }

    private static string RenderTsv(List<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join('\t', Header));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join('\t', row));
        }
        return sb.ToString();
    }

    private static string RenderSummary(
        List<string[]> rows,
        SessionDiscovery.DiscoveryStats stats,
        DatasetBuildOptions options)
    {
        int darkFound = 0, darkGainMatched = 0, flatFound = 0, flatFilterMatched = 0, flatInTimeframe = 0;
        int pedestalDarkFlat = 0, pedestalBias = 0, pedestalNone = 0, biasAvailable = 0, bpmPresent = 0, belowMinSubs = 0;
        var col = ColumnIndex();
        foreach (var row in rows)
        {
            if (row[col["dark_found"]] == "true") darkFound++;
            if (row[col["dark_gain_match"]] == "true") darkGainMatched++;
            if (row[col["flat_found"]] == "true") flatFound++;
            if (row[col["flat_filter_match"]] == "true") flatFilterMatched++;
            if (row[col["flat_within_30d"]] == "true") flatInTimeframe++;
            switch (row[col["pedestal_kind"]])
            {
                case "darkflat" or "dark": pedestalDarkFlat++; break;
                case "bias": pedestalBias++; break;
                case "none": pedestalNone++; break;
            }
            if (row[col["bias_groups"]] != "0") biasAvailable++;
            if (row[col["bpm_files"]] != "0") bpmPresent++;
            if (row[col["below_bake_min_subs"]] == "true") belowMinSubs++;
        }

        var n = rows.Count;
        var sb = new StringBuilder();
        sb.AppendLine("# Calibration coverage");
        sb.AppendLine();
        sb.AppendLine("The TSV beside this file is the deliverable; this is the rollup. One row per session,");
        sb.AppendLine("resolved by the production matcher (strict gain gate: " + (options.RequireGainMatch ? "ON" : "OFF") + ").");
        sb.AppendLine();
        sb.AppendLine(FormattableString.Invariant($"- Sessions: **{n}** ({stats.Lights} lights); {belowMinSubs} below the bake threshold of {options.MinSubsPerSession} subs"));
        sb.AppendLine(FormattableString.Invariant($"- Dark resolved: **{darkFound}/{n}** ({Pct(darkFound, n)}); gain-matched: {darkGainMatched}"));
        sb.AppendLine(FormattableString.Invariant($"- Flat resolved: **{flatFound}/{n}** ({Pct(flatFound, n)}); filter-matched: {flatFilterMatched}; within {FlatTimeframeDays:F0} days: {flatInTimeframe}"));
        sb.AppendLine(FormattableString.Invariant($"- Flat pedestal: dark-flat/dark {pedestalDarkFlat}, bias {pedestalBias}, none {pedestalNone}"));
        sb.AppendLine(FormattableString.Invariant($"- Bias available (any compatible group): {biasAvailable}/{n}"));
        sb.AppendLine(FormattableString.Invariant($"- APP BPM present for the sensor geometry: {bpmPresent}/{n}"));
        sb.AppendLine();
        sb.AppendLine("## Scan gates");
        sb.AppendLine();
        // Plain interpolation: every hole is an int, whose default formatting is culture-invariant.
        sb.AppendLine(
            $"Scanned {stats.Scanned} FITS headers: {stats.NotLight} non-light (the calibration pool), " +
            $"{stats.ExposureOutOfRange} exposure-gated, {stats.InstrumentExcluded} instrument-excluded, " +
            $"{stats.SoftwareExcluded} software-excluded, {stats.ObjectExcluded} object-excluded, " +
            $"{stats.PathExcluded} path-excluded, {stats.ProductExcluded} products, {stats.Duplicates} duplicates.");
        if (stats.Sidecar is { IsEmpty: false } sidecar)
        {
            sb.AppendLine(
                $"Sidecars: {sidecar.Files} parsed, {sidecar.Malformed} malformed, " +
                $"{sidecar.FilterFilled} frames filter-filled, {sidecar.FilterAlreadyPresent} already carried one.");
        }

        sb.AppendLine();
        sb.AppendLine("## Per camera");
        sb.AppendLine();
        sb.AppendLine("| Camera | Sessions | Dark | Flat | Filter-matched flat |");
        sb.AppendLine("|---|---|---|---|---|");
        var byCamera = new SortedDictionary<string, (int Sessions, int Dark, int Flat, int FilterMatch)>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var camera = rows[i][col["camera"]];
            var entry = byCamera.GetValueOrDefault(camera);
            entry.Sessions++;
            if (rows[i][col["dark_found"]] == "true") entry.Dark++;
            if (rows[i][col["flat_found"]] == "true") entry.Flat++;
            if (rows[i][col["flat_filter_match"]] == "true") entry.FilterMatch++;
            byCamera[camera] = entry;
        }
        foreach (var (camera, entry) in byCamera)
        {
            sb.AppendLine(FormattableString.Invariant(
                $"| {camera} | {entry.Sessions} | {entry.Dark} | {entry.Flat} | {entry.FilterMatch} |"));
        }
        return sb.ToString();
    }

    private static Dictionary<string, int> ColumnIndex()
    {
        var col = new Dictionary<string, int>(Header.Length, StringComparer.Ordinal);
        for (var i = 0; i < Header.Length; i++)
        {
            col[Header[i]] = i;
        }
        return col;
    }

    private static string Pct(int part, int whole) =>
        whole == 0 ? "0%" : FormattableString.Invariant($"{100.0 * part / whole:F0}%");

    private static bool Buildable(CalibrationResolver.CalGroup g) => g.Frames.Length >= (g.IsMaster ? 1 : 2);

    private static bool TemperatureCompatible(MasterGroupKey g, MasterGroupKey light, double? maxTempDelta) =>
        maxTempDelta is not { } max
        || g.TemperatureC is not { } gt
        || light.TemperatureC is not { } lt
        || Math.Abs(gt - lt) <= max;

    private static int? AgeDays(DateTimeOffset? epochStart, DateTimeOffset sessionDate) =>
        epochStart is not { } start || start == default || sessionDate == default
            ? null
            : (int)Math.Round(Math.Abs((start - sessionDate).TotalDays));

    private static string EpochText(CalibrationResolver.CalGroup? group) => group is null
        ? ""
        : group.EpochStart == default
            ? "undated"
            : group.EpochStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string PedestalKind(FrameType type) => type switch
    {
        FrameType.Bias => "bias",
        FrameType.DarkFlat => "darkflat",
        FrameType.Dark => "dark",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static string Seconds(TimeSpan exposure) =>
        exposure.TotalSeconds > 0 ? exposure.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) : "";

    private static string GainText(short gain) => gain >= 0 ? gain.ToString(CultureInfo.InvariantCulture) : "";

    private static string OffsetText(int offset) => offset >= 0 ? offset.ToString(CultureInfo.InvariantCulture) : "";

    private static string TempText(int? temp) => temp?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>TSV fields must not carry the separator or line breaks; target/instrument names are
    /// free text from headers.</summary>
    private static string Clean(string value) =>
        value.IndexOfAny(['\t', '\r', '\n']) < 0 ? value : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
