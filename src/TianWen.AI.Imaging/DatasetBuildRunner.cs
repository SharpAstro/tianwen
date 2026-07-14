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
/// P0/#43) — the one-command run that turns a raw archive into a regenerable training tile set. It
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
/// Lives here (not in Lib) because it drives <see cref="DatasetTileExporter"/> — the only piece
/// coupled to the AI input pre-stretch; everything else is Lib.
/// </summary>
public static class DatasetBuildRunner
{
    /// <summary>Outcome of one build run.</summary>
    /// <param name="Failed">Sessions that threw mid-pipeline (unreadable pixel data, I/O faults)
    /// and were skipped. Discovery only validates HEADERS, so a truncated file with a clean header
    /// surfaces here, at register time — fault-isolated per session so one bad frame can never
    /// abort a multi-hour archive bake. Failures are logged per session; a crashed-then-restarted
    /// run starts fresh (the manifest is regenerated) unless <see cref="DatasetBuildOptions.Resume"/>
    /// checkpoints it, so partial output never needs repairing.</param>
    /// <param name="SkippedNoDark">Sessions skipped because no master dark could be resolved and
    /// <see cref="DatasetBuildOptions.RequireDarkCalibration"/> is set — an uncalibrated N2N pair
    /// shares the sensor's fixed-pattern dark signal (correlated between subs), so it is not a valid
    /// training sample. Distinct from <paramref name="Failed"/> (an error), and from the silent
    /// too-few-subs skip.</param>
    /// <param name="Resumed">Sessions skipped wholesale because their tiles were already in the
    /// manifest (<see cref="DatasetBuildOptions.Resume"/>) — their prior tile counts fold into
    /// <paramref name="TotalTiles"/> but they are NOT re-registered, so the PSF/noise report of a
    /// resumed run covers only the sessions registered in THAT run.</param>
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
        string ReportPath);

    public static async Task<RunResult> RunAsync(
        DatasetBuildOptions options,
        ILogger? logger = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outDir = options.OutputDir;
        Directory.CreateDirectory(outDir);

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
            ? await DatasetTileExporter.ReadManifestSessionTileCountsAsync(manifestPath, cancellationToken)
            : new Dictionary<string, int>(StringComparer.Ordinal);
        if (!options.Resume && File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        // 3. Per-session pipeline. Scratch (warped subs) is wiped after each session so peak disk is
        //    bounded by the largest single session, not the whole archive; the masters cache
        //    (outDir/masters) is separate and preserved for build-once reuse.
        var masterCache = new MasterCache(Path.Combine(outDir, "masters"), logger);
        var scratchRoot = Path.Combine(outDir, "_scratch");
        var reportAcc = new DatasetPsfNoiseReport.Accumulator();
        var registered = 0;
        var failed = 0;
        var skippedNoDark = 0;
        var resumed = 0;
        var totalTiles = 0;
        var parityChecked = false;
        var parityMaxDiff = 0.0;
        var idx = 0;
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            idx++;
            if (priorTiles.TryGetValue(session.Id, out var tiles))
            {
                resumed++;
                totalTiles += tiles;
                progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} resumed ({tiles} tiles already exported)");
                continue;
            }
            progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} ...");

            // Fault-isolated per session: discovery validated only HEADERS, so a truncated /
            // unreadable file first explodes here (LoadFullAsync -> IOException), potentially hours
            // into an archive bake. Log + count + move on; cancellation still propagates.
            try
            {
                var calibrator = await CalibrationResolver.ResolveAsync(session, calGroups, masterCache, options.RequireGainMatch, logger, cancellationToken);

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
                registered++;

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

                await reportAcc.AddAsync(reg, logger, cancellationToken);
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

        // 4. PSF/noise distribution report.
        var reportPath = Path.Combine(outDir, "stats", "psf-noise-report.md");
        await DatasetPsfNoiseReport.WriteMarkdownAsync(reportAcc.Build(), reportPath, cancellationToken);

        TryDelete(scratchRoot);
        if (resumed > 0)
        {
            // The report accumulator only sees sessions registered THIS run -- a resumed session's
            // registration scratch is long gone, so its PSF stats cannot be re-measured cheaply.
            logger?.LogWarning(
                "Resume: PSF/noise report covers only the {Registered} session(s) registered in this run; {Resumed} resumed session(s) are not re-measured.",
                registered, resumed);
        }
        progress?.Report(
            $"[dataset] done: {registered}/{sessions.Length} sessions{(resumed > 0 ? $" (+{resumed} resumed)" : "")} -> {totalTiles} tiles " +
            $"({failed} failed, {skippedNoDark} skipped-no-dark); " +
            $"parity {(parityChecked ? parityMaxDiff == 0.0 ? "OK" : $"DIFF {parityMaxDiff}" : "n/a")}");
        return new RunResult(
            sessions.Length, registered, failed, skippedNoDark, resumed, totalTiles, testSessions.Length,
            parityChecked, parityMaxDiff, manifestPath, splitPath, reportPath);
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
