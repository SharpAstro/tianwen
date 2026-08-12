using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;

namespace TianWen.AI.Imaging;

/// <summary>
/// End-to-end <c>tianwen dataset build</c> orchestration (docs/plans/ai-denoise-deconv.md §2.4, task
/// P0/#43): the one-command run that turns a raw archive into a regenerable training tile set. It
/// scans the archive ONCE, then:
/// <list type="number">
///   <item>groups lights into sessions (<see cref="SessionDiscovery"/>) and calibration frames into
///     master groups (<see cref="CalibrationResolver"/>);</item>
///   <item>writes the pinned by-session train/test split up front (<see cref="DatasetSplitWriter"/>);</item>
///   <item>per session: resolves an archive-wide header-matched <c>Calibrator</c> (masters built once
///     + cached), registers + integrates (<see cref="SessionRegistrar"/>), exports zero-skew tiles +
///     manifest (<see cref="DatasetTileExporter"/>), folds PSF/noise stats in, and deletes that
///     session's scratch before moving on (peak disk = one session's warped subs, not the archive's);</item>
///   <item>runs the zero-skew parity check on the first exported session as an in-run gate;</item>
///   <item>writes the PSF/noise distribution report.</item>
/// </list>
/// Lives here (not in Lib) because it drives <see cref="DatasetTileExporter"/>; the only piece
/// coupled to the AI input pre-stretch; everything else is Lib.
/// </summary>
public static class DatasetBuildRunner
{
    /// <summary>Outcome of one build run.</summary>
    /// <param name="Failed">Sessions that threw mid-pipeline (unreadable pixel data, I/O faults)
    /// and were skipped. Discovery only validates HEADERS, so a truncated file with a clean header
    /// surfaces here, at register time: fault-isolated per session so one bad frame can never
    /// abort a multi-hour archive bake. Failures are logged per session; a crashed-then-restarted
    /// run starts fresh (the manifest is regenerated) unless <see cref="DatasetBuildOptions.Resume"/>
    /// checkpoints it, so partial output never needs repairing.</param>
    /// <param name="SkippedNoDark">Sessions skipped because no master dark could be resolved and
    /// <see cref="DatasetBuildOptions.RequireDarkCalibration"/> is set; an uncalibrated N2N pair
    /// shares the sensor's fixed-pattern dark signal (correlated between subs), so it is not a valid
    /// training sample. Distinct from <paramref name="Failed"/> (an error), and from the silent
    /// too-few-subs skip.</param>
    /// <param name="Resumed">Sessions skipped wholesale because their tiles were already in the
    /// manifest AND still on disk (<see cref="DatasetBuildOptions.Resume"/>); their prior tile counts
    /// fold into <paramref name="TotalTiles"/> and their PSF stats come from
    /// <see cref="DatasetPsfStore"/>, so the report still covers them without re-registration.</param>
    /// <param name="PsfMissing">Resumed sessions that have tiles but no stored PSF record, so the
    /// report does not cover them. Non-zero means the report is incomplete and says which flag fixes
    /// it (<see cref="DatasetBuildOptions.RegenPsfForExportedSessions"/>).</param>
    /// <param name="PsfRemeasured">Already-exported sessions re-registered purely to recover their
    /// PSF measurement; their tiles were left untouched.</param>
    public sealed record RunResult(
        int Sessions,
        int Registered,
        int Failed,
        int SkippedNoDark,
        int Resumed,
        int TotalTiles,
        int TestSessions,
        bool ParityChecked,
        double ParityMaxDiff,
        string ManifestPath,
        string SplitPath,
        string ReportPath,
        string PsfStorePath,
        int PsfMissing,
        int PsfRemeasured);

    /// <summary>Rendered PSF/noise report, written beside the store under <c>&lt;outDir&gt;/stats</c>.</summary>
    public const string ReportFileName = "psf-noise-report.md";

    public static async Task<RunResult> RunAsync(
        DatasetBuildOptions options,
        ILogger? logger = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outDir = options.OutputDir;
        Directory.CreateDirectory(outDir);

        // 0. Report-only short-circuits BEFORE the archive scan, which is the whole point: the scan
        //    is seek-bound over ~19k headers and is the only reason a re-render would need the
        //    archive mounted at all.
        if (options.ReportOnly)
        {
            return await RenderReportOnlyAsync(outDir, logger, progress, cancellationToken);
        }

        // 1. Single scan of every archive root -> sessions + calibration groups from the same frames.
        var frames = new List<(FrameInfo Frame, string Root)>();
        foreach (var root in options.ArchiveRoots)
        {
            var source = new FitsFolderFrameSource(root, recursive: true);
            await foreach (var frame in source.EnumerateAsync(cancellationToken))
            {
                frames.Add((frame, root));
            }
            progress?.Report($"[dataset] scanned {root}: {frames.Count} FITS headers so far");
        }
        var (sessions, stats) = SessionDiscovery.GroupSessions(frames, options);
        var calGroups = CalibrationResolver.GroupCalibration(frames.Select(f => f.Frame));
        progress?.Report(
            $"[dataset] {stats.Sessions} sessions / {stats.Lights} lights; " +
            $"cal groups: {CalCount(calGroups, FrameType.Dark)} dark, {CalCount(calGroups, FrameType.Flat)} flat, {CalCount(calGroups, FrameType.Bias)} bias");

        // 2. Pinned by-session split, written up front (independent of registration).
        var splitPath = Path.Combine(outDir, DatasetSplitWriter.TestSessionsFileName);
        var testSessions = await DatasetSplitWriter.WriteAsync(sessions.Select(s => s.Id), options.TestFraction, splitPath, cancellationToken);
        progress?.Report($"[dataset] pinned test split: {testSessions.Length}/{sessions.Length} sessions held out");

        // Fresh manifest per run (the exporter appends per session) -- UNLESS resuming, where the
        // existing manifest IS the checkpoint: a session's rows are appended in one block as the
        // LAST step of its export, so "rows present" == "session fully exported". The in-flight
        // session a stop interrupted has no rows and re-runs cleanly (tile names are deterministic,
        // so its partial files are simply overwritten).
        var manifestPath = Path.Combine(outDir, DatasetTileExporter.ManifestFileName);
        var priorTiles = options.Resume
            ? await DatasetTileExporter.ReadManifestCheckpointsAsync(manifestPath, cancellationToken)
            : new Dictionary<string, DatasetTileExporter.ManifestCheckpoint>(StringComparer.Ordinal);
        if (!options.Resume && File.Exists(manifestPath))
        {
            // Rotated, never deleted: the manifest is the only record of what a previous run
            // exported, and the tiles it describes are still on disk. A fresh run legitimately
            // starts a new one, but erasing the old leaves those tiles unaccounted for.
            var rotated = JsonLinesFile.NextFreeBackupPath(manifestPath);
            File.Move(manifestPath, rotated);
            logger?.LogWarning("Fresh (non-resume) run: existing manifest moved aside to {Rotated}.", Path.GetFileName(rotated));
        }

        // The PSF/noise report's inputs are checkpointed per session, so the report accumulates
        // across runs instead of being rebuilt from whatever the current run happened to register.
        // Before this, a resumed run's report was rewritten from only its own sessions and the prior
        // content was lost, unrecoverably: the field-radius profile is measured on the session
        // master, which lives in scratch that is wiped per session.
        var statsDir = Path.Combine(outDir, "stats");
        var reportPath = Path.Combine(statsDir, ReportFileName);
        var psfStorePath = Path.Combine(statsDir, DatasetPsfStore.FileName);
        var psfBySession = await DatasetPsfStore.ReadAsync(psfStorePath, logger, cancellationToken);

        // 3. Per-session pipeline. Scratch (warped subs) is wiped after each session so peak disk is
        //    bounded by the largest single session, not the whole archive; the masters cache
        //    (outDir/masters) is separate and preserved for build-once reuse.
        var masterCache = new MasterCache(Path.Combine(outDir, "masters"), logger);
        // Always the "_scratch" leaf, so an operator-supplied ScratchRoot keeps its own directory:
        // TryDelete below removes this path, and it must never be the caller's parent.
        var scratchRoot = Path.Combine(
            string.IsNullOrWhiteSpace(options.ScratchRoot) ? outDir : options.ScratchRoot,
            "_scratch");
        var sessionIds = new HashSet<string>(sessions.Select(s => s.Id), StringComparer.Ordinal);
        var registered = 0;
        var failed = 0;
        var skippedNoDark = 0;
        var resumed = 0;
        var psfMissing = 0;
        var psfRemeasured = 0;
        var mastersRetained = 0;
        var totalTiles = 0;
        var parityChecked = false;
        var parityMaxDiff = 0.0;
        var idx = 0;
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            idx++;

            // Resume decides per ARTIFACT, not per session: tiles and the PSF record are checkpointed
            // separately, so a session can legitimately need one and not the other.
            var checkpoint = priorTiles.GetValueOrDefault(session.Id);
            var tilesReusable = checkpoint is not null && TilesStillPresent(outDir, checkpoint, logger);
            var psfOnly = false;
            if (tilesReusable && checkpoint is not null)
            {
                // Two separate intents, kept separate because they cost wildly different amounts.
                // RegenPsfForExportedSessions FILLS GAPS: it is idempotent and touches only sessions
                // the report does not cover, so it converges and re-running it is nearly free.
                // ForcePsfRemeasure RE-MEASURES REGARDLESS, which is what an estimator change needs
                // and what the gap-fill cannot express: a session that already has a record is
                // exactly the one whose record is now wrong. It costs a full re-registration of
                // EVERY exported session, so it is never implied.
                var hasRecord = psfBySession.ContainsKey(session.Id);
                var measure = options.ForcePsfRemeasure || (!hasRecord && options.RegenPsfForExportedSessions);
                if (!measure)
                {
                    resumed++;
                    if (!hasRecord) { psfMissing++; }
                    totalTiles += checkpoint.TileCount;
                    progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} resumed ({checkpoint.TileCount} tiles" +
                        (hasRecord ? " + PSF already recorded)" : ", PSF record MISSING)"));
                    continue;
                }
                // Tiles are fine and stay untouched; re-register only to recover the master the PSF
                // measurement needs. Their count is banked HERE, not after a successful re-measure:
                // the tiles are on disk either way, so a re-measurement that fails must not make the
                // run under-report the tiles it still has.
                psfOnly = true;
                totalTiles += checkpoint.TileCount;
                progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} re-measuring PSF" +
                    (hasRecord ? " (forced, tiles kept)" : " (tiles kept)") + " ...");
            }
            else
            {
                progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} ...");
            }

            // Fault-isolated per session: discovery validated only HEADERS, so a truncated /
            // unreadable file first explodes here (LoadFullAsync -> IOException), potentially hours
            // into an archive bake. Log + count + move on; cancellation still propagates.
            try
            {
                var calibrator = await CalibrationResolver.ResolveAsync(
                    session, calGroups, masterCache, options.RequireGainMatch, options.MaxDarkTemperatureDelta, logger, cancellationToken);

                // A training sample needs dark subtraction: an uncalibrated N2N pair shares the
                // sensor's fixed-pattern dark signal (correlated between the two subs), so skip a
                // session with no resolved dark rather than poison the set. Opt-in so the prior
                // register-everything behaviour + existing tests are unchanged.
                if (options.RequireDarkCalibration && calibrator?.Dark is null)
                {
                    skippedNoDark++;
                    logger?.LogWarning("  [{Session}] SKIPPED -- no master dark resolved (RequireDarkCalibration)", session.Id);
                    progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} SKIPPED: no dark calibration");
                    continue;
                }

                var reg = await SessionRegistrar.RegisterAsync(
                    session, calibrator, scratchRoot,
                    options.QualityRejectSigma, options.QualityMaxRejectFraction, options.MinSubsPerSession,
                    logger: logger, cancellationToken: cancellationToken);
                if (reg is null)
                {
                    continue;
                }

                // Retain the integrated master BEFORE anything else touches it. This is the only
                // perishable output of the whole run: scratch is wiped per session, so afterwards the
                // master exists nowhere, and re-deriving anything measured on it has meant registering
                // the session again (two 7h16m re-runs in two days, for a detection fix and an FWHM
                // fix, neither of which needed the subs). Written once and skipped if present, so a
                // resume costs nothing.
                //
                // Best-effort ON PURPOSE, in its own catch: retention is a convenience, the
                // measurement below is the job. A full disk must not cost this session its PSF record
                // as well as its master, which is what letting it fall to the per-session catch would
                // do (that path counts a failure and skips the measure).
                if (options.RetainSessionMasters)
                {
                    try
                    {
                        var mastersDir = Path.Combine(outDir, "session-masters");
                        Directory.CreateDirectory(mastersDir);
                        var masterPath = Path.Combine(mastersDir, DatasetTileExporter.Sanitize(session.Id) + ".fits");
                        if (File.Exists(masterPath))
                        {
                            logger?.LogDebug("  [{Session}] session master already retained", session.Id);
                        }
                        else
                        {
                            // Write to a temp name and move, so a kill mid-write cannot leave a
                            // truncated FITS that a later run would treat as already retained.
                            var tempPath = masterPath + ".partial";
                            reg.Master.WriteToFitsFile(tempPath);
                            File.Move(tempPath, masterPath, overwrite: true);
                            mastersRetained++;
                            logger?.LogDebug("  [{Session}] session master retained", session.Id);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger?.LogWarning(ex, "  [{Session}] could not retain the session master; continuing", session.Id);
                    }
                }
                registered++;

                if (psfOnly)
                {
                    psfRemeasured++; // tile count already banked before the try
                }
                else
                {
                    var export = await DatasetTileExporter.ExportAsync(
                        reg, outDir, options.TileSize, options.CellsPerSession, options.SubsPerCell, logger, cancellationToken);
                    totalTiles += export.Rows.Length;

                    // In-run zero-skew gate: verify the first exported session's stored tiles equal the C#
                    // stretch of their source (before its scratch is wiped).
                    if (!parityChecked && export.Rows.Length > 0)
                    {
                        var parity = await DatasetTileExporter.VerifyParityAsync(reg, outDir, export.Rows, sampleCount: 8, cancellationToken);
                        parityMaxDiff = parity.MaxAbsDiff;
                        parityChecked = true;
                        progress?.Report($"[dataset] parity: maxDiff={parity.MaxAbsDiff} over {parity.Checked} tiles");
                    }
                }

                // Measure, PERSIST, then re-render the report. Persisting before rendering is what
                // makes a kill at any point cost only the in-flight session: the store holds every
                // session measured so far, and the rendered report is rebuilt from the store rather
                // than from this run's in-memory accumulator.
                var psf = await DatasetPsfNoiseReport.MeasureSessionAsync(reg, logger: logger, cancellationToken: cancellationToken);
                await DatasetPsfStore.AppendAsync(psfStorePath, psf, cancellationToken);
                psfBySession[session.Id] = psf;
                await WriteReportAsync(reportPath, psfBySession, sessionIds, logger, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger?.LogError(ex, "  [{Session}] FAILED -- skipped", session.Id);
                progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} FAILED: {ex.Message} -- skipped");
            }
            finally
            {
                TryDelete(scratchRoot);
            }
        }

        // 4. PSF/noise distribution report, rebuilt from the checkpoint store so it covers every
        //    session ever measured into this output dir, not just this run's.
        await WriteReportAsync(reportPath, psfBySession, sessionIds, logger, cancellationToken);

        TryDelete(scratchRoot);
        if (psfMissing > 0)
        {
            // Actionable rather than merely apologetic: name the flag that fixes it and say what it
            // costs, because the fix means re-registering those sessions.
            logger?.LogWarning(
                "PSF/noise report is missing {Missing} session(s) that have tiles but no stored PSF record, so it describes {Covered} of {Total}. Re-run with RegenPsfForExportedSessions (--regen-psf) to measure them; that re-registers each one (tiles are left untouched).",
                psfMissing, psfBySession.Count, sessions.Length);
        }
        progress?.Report(
            $"[dataset] done: {registered}/{sessions.Length} sessions{(resumed > 0 ? $" (+{resumed} resumed)" : "")}" +
            $"{(psfRemeasured > 0 ? $" ({psfRemeasured} PSF re-measured)" : "")} -> {totalTiles} tiles " +
            $"({failed} failed, {skippedNoDark} skipped-no-dark); " +
            $"PSF report covers {psfBySession.Count(kv => sessionIds.Contains(kv.Key))}/{sessions.Length}; " +
            $"{(options.RetainSessionMasters ? $"{mastersRetained} master(s) retained; " : "")}" +
            $"parity {(parityChecked ? parityMaxDiff == 0.0 ? "OK" : $"DIFF {parityMaxDiff}" : "n/a")}");
        return new RunResult(
            sessions.Length, registered, failed, skippedNoDark, resumed, totalTiles, testSessions.Length,
            parityChecked, parityMaxDiff, manifestPath, splitPath, reportPath, psfStorePath, psfMissing, psfRemeasured);
    }

    /// <summary>
    /// Re-renders the report from what is already on disk (<see cref="DatasetBuildOptions.ReportOnly"/>):
    /// the PSF store supplies the measurements, the tile manifest supplies the session set. Nothing
    /// is scanned, registered, measured or written except the report itself.
    /// </summary>
    private static async Task<RunResult> RenderReportOnlyAsync(
        string outDir,
        ILogger? logger,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(outDir, DatasetTileExporter.ManifestFileName);
        var splitPath = Path.Combine(outDir, DatasetSplitWriter.TestSessionsFileName);
        var statsDir = Path.Combine(outDir, "stats");
        var reportPath = Path.Combine(statsDir, ReportFileName);
        var psfStorePath = Path.Combine(statsDir, DatasetPsfStore.FileName);
        Directory.CreateDirectory(statsDir);

        // The manifest IS the session set: a session's rows are appended in one block as the last
        // step of its export, so being in the manifest means it is genuinely part of this dataset.
        var exported = await DatasetTileExporter.ReadManifestCheckpointsAsync(manifestPath, cancellationToken);
        var psfBySession = await DatasetPsfStore.ReadAsync(psfStorePath, logger, cancellationToken);
        var sessionIds = new HashSet<string>(exported.Keys, StringComparer.Ordinal);
        var totalTiles = exported.Values.Sum(c => c.TileCount);
        var covered = psfBySession.Count(kv => sessionIds.Contains(kv.Key));
        var psfMissing = exported.Count - covered;

        await WriteReportAsync(reportPath, psfBySession, sessionIds, logger, cancellationToken);

        if (psfMissing > 0)
        {
            // Same warning a normal run gives, and the same remedy: report-only cannot measure,
            // because the field-radius profile needs the session master.
            logger?.LogWarning(
                "PSF/noise report is missing {Missing} session(s) that have tiles but no stored PSF record, so it describes {Covered} of {Total}. A report-only render cannot fix that; re-run with RegenPsfForExportedSessions (--regen-psf), which re-registers each one.",
                psfMissing, covered, exported.Count);
        }
        progress?.Report(
            $"[dataset] report-only: re-rendered from {covered}/{exported.Count} session record(s) " +
            $"({totalTiles} tiles); no archive scan, nothing re-measured -> {reportPath}");

        return new RunResult(
            Sessions: exported.Count, Registered: 0, Failed: 0, SkippedNoDark: 0,
            Resumed: exported.Count, TotalTiles: totalTiles, TestSessions: 0,
            ParityChecked: false, ParityMaxDiff: 0.0,
            ManifestPath: manifestPath, SplitPath: splitPath, ReportPath: reportPath,
            PsfStorePath: psfStorePath, PsfMissing: psfMissing, PsfRemeasured: 0);
    }

    /// <summary>
    /// Renders the report from the PSF store, filtered to the sessions the CURRENT discovery found.
    /// The store is append-only and never pruned, so a session dropped from the archive (or excluded
    /// by a changed gate) leaves its record behind; including it would make the report describe a
    /// dataset that no longer exists, while deleting it would throw away a measurement that cost a
    /// full registration.
    /// </summary>
    private static async Task WriteReportAsync(
        string reportPath,
        Dictionary<string, DatasetPsfNoiseReport.SessionPsf> psfBySession,
        HashSet<string> sessionIds,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var acc = new DatasetPsfNoiseReport.Accumulator();
        // Ordered so the rendered report is deterministic regardless of the order sessions were
        // measured across however many runs it took.
        foreach (var id in psfBySession.Keys.Where(sessionIds.Contains).OrderBy(id => id, StringComparer.Ordinal))
        {
            acc.Add(psfBySession[id], logger);
        }
        await DatasetPsfNoiseReport.WriteMarkdownAsync(acc.Build(), reportPath, cancellationToken);
    }

    /// <summary>
    /// Verifies a manifest checkpoint against the filesystem: the tile directory must exist and hold
    /// at least as many tiles as the manifest claims. The manifest records what WAS written, so
    /// trusting it alone means a session whose tiles were moved or deleted is skipped as "already
    /// exported" and the run reports success over files that are not there. Costs one directory
    /// enumeration per resumed session.
    /// </summary>
    private static bool TilesStillPresent(string outDir, DatasetTileExporter.ManifestCheckpoint checkpoint, ILogger? logger)
    {
        if (checkpoint.TileDirRelative.Length == 0)
        {
            return false;
        }
        var dir = Path.Combine(outDir, checkpoint.TileDirRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir))
        {
            logger?.LogWarning(
                "  [{Session}] manifest claims {Claimed} tiles in {Dir} but the directory is gone -- re-registering instead of resuming.",
                checkpoint.SessionId, checkpoint.TileCount, checkpoint.TileDirRelative);
            return false;
        }
        var onDisk = Directory.EnumerateFiles(dir, "*" + DatasetTileExporter.TileExtension).Count();
        if (onDisk < checkpoint.TileCount)
        {
            logger?.LogWarning(
                "  [{Session}] manifest claims {Claimed} tiles in {Dir} but only {OnDisk} are present -- re-registering instead of resuming.",
                checkpoint.SessionId, checkpoint.TileCount, checkpoint.TileDirRelative, onDisk);
            return false;
        }
        return true;
    }

    private static int CalCount(IReadOnlyDictionary<FrameType, List<CalibrationResolver.CalGroup>> groups, FrameType type) =>
        groups.TryGetValue(type, out var list) ? list.Count : 0;

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort scratch hygiene; a locked handle just leaves a temp dir behind.
        }
    }
}
