using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.IO;
using TianWen.Lib.Imaging.Stacking;

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
        int PsfRemeasured,
        int PsfRemeasuredFromMaster = 0);

    /// <summary>Rendered PSF/noise report, written beside the store under <c>&lt;outDir&gt;/stats</c>.</summary>
    public const string ReportFileName = "psf-noise-report.md";

    /// <summary>Tile export: cell selection, stretch, and the tile writes. Items = TILES, not subs.
    /// The distinction is the reason <see cref="StageTimings.Stage.Items"/> is not called "frames":
    /// normalising this stage per input frame makes a stage that creates ~41 files a second on a
    /// spindle read as a compute stage running at 14 Mpx/s.</summary>
    public const string ExportStage = "export";

    /// <summary>PSF/noise measurement on the session master. Items = the master (1), so its per-item
    /// figure is simply its own cost.</summary>
    public const string PsfStage = "psf";

    /// <summary>Writing the retained session master. Items = masters written (0 when retention is off
    /// or the master was already on disk).</summary>
    public const string RetainStage = "retain";

    /// <summary>Deciding, per session, whether a resume can reuse its tiles. Items = sessions
    /// CONSIDERED, including the ones that then went on to full work, because the decision is paid for
    /// either way. Run-level rather than per-session: a resumed session writes no timing record, so
    /// charging this to sessions would make the commonest case the invisible one.</summary>
    public const string ResumeCheckStage = "resume-check";

    public static async Task<RunResult> RunAsync(
        DatasetBuildOptions options,
        ILogger? logger = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outDir = options.OutputDir;
        Directory.CreateDirectory(outDir);

        // Before anything reads or writes it. Report-only is included on purpose: it rewrites the
        // rendered report from the store, so it is a writer too.
        using var outputLock = AcquireRunLock(
            Path.Combine(outDir, RunLockFileName), $"the output directory {outDir}", "--out");

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

        // The sessions that FAIL are kept in the source set on purpose, never excluded by path or
        // object, because a change in which ones fail (or in their numbers) is how a detection or
        // registration regression announces itself. That only works if a skip leaves something
        // diffable behind: previously it left a WARNING in a console log, and comparing two bakes
        // meant grepping them by hand.
        var skipStorePath = Path.Combine(statsDir, DatasetSkipStore.FileName);

        // Per-stage wall-clock accounting, one accumulator per session, persisted beside the other
        // two stores. Before this there was no instrumentation at all and "where does a bake spend
        // its time" was answered by parsing timestamps out of the Debug log and inferring the stage
        // boundaries from message shapes, which cannot see a denominator and got one wrong.
        var timingStorePath = Path.Combine(statsDir, DatasetTimingStore.FileName);
        var sessionTimings = new List<ImmutableArray<StageTimings.Stage>>();

        // 3. Per-session pipeline. Scratch (warped subs) is wiped after each session so peak disk is
        //    bounded by the largest single session, not the whole archive; the masters cache
        //    (outDir/masters) is separate and preserved for build-once reuse.
        var masterCache = new MasterCache(Path.Combine(outDir, "masters"), logger);
        // Always the "_scratch" leaf, so an operator-supplied ScratchRoot keeps its own directory:
        // TryDelete below removes this path, and it must never be the caller's parent.
        var scratchRoot = Path.Combine(
            string.IsNullOrWhiteSpace(options.ScratchRoot) ? outDir : options.ScratchRoot,
            "_scratch");
        // A SECOND lock, because the output directory's does not imply this one: --scratch-root
        // exists so scratch can be steered onto a fast disk, and two bakes with different output
        // directories pointed at the same SSD share exactly the tree that gets wiped per session.
        // Sibling path, never inside scratchRoot, so the held handle cannot block TryDelete.
        using var scratchLock = AcquireRunLock(
            scratchRoot + ".lock", $"the scratch root {scratchRoot}", "--scratch-root");
        var sessionIds = new HashSet<string>(sessions.Select(s => s.Id), StringComparer.Ordinal);
        var registered = 0;
        var failed = 0;
        var skippedNoDark = 0;
        var resumed = 0;
        var psfMissing = 0;
        var psfRemeasured = 0;
        // Of those, how many avoided re-registration by reading a retained master. Reported separately
        // because the two cost wildly different amounts, and a run that quietly took the slow path for
        // every session looks identical in the summary otherwise.
        var psfRemeasuredFromMaster = 0;
        var mastersRetained = 0;
        var totalTiles = 0;
        var parityChecked = false;
        var parityMaxDiff = 0.0;
        var idx = 0;
        var loopStart = StageTimings.Start();
        // Cross-session overhead, which belongs to the RUN rather than to any one session: a resumed
        // session produces no timing record of its own, so without this its cost is invisible. The
        // first run of this instrumentation is what surfaced the need -- a --regen-psf run reported
        // 72.9% unaccounted, all of it the resume checks below, and that is exactly the reading the
        // unaccounted line exists to make possible.
        var overhead = new StageTimings();
        foreach (var session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            idx++;

            // Resume decides per ARTIFACT, not per session: tiles and the PSF record are checkpointed
            // separately, so a session can legitimately need one and not the other.
            var resumeStart = StageTimings.Start();
            var checkpoint = priorTiles.GetValueOrDefault(session.Id);
            var tilesReusable = checkpoint is not null && TilesStillPresent(outDir, checkpoint, logger);
            // Charged per session CONSIDERED, so the per-item figure is the cost of deciding one
            // session's fate. It is not free: TilesStillPresent stats a sample of that session's tiles
            // on the output disk, which measured ~1.4 s per session on a spindle.
            overhead.Record(ResumeCheckStage, resumeStart, items: 1);
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

                // THE CHEAP PATH, and the reason masters are retained at all. A forced re-measure only
                // needs the master plus the per-sub metrics, and the metrics are already in the prior
                // record, so with a retained master on disk there is nothing left to read from the
                // archive: minutes instead of re-registering every exported session.
                //
                // Requires a PRIOR RECORD, which is what keeps the two intents apart. --force-psf has
                // one by definition (that is what makes its record stale), so it takes this path. A
                // --regen-psf gap-fill has none, so its sub metrics exist nowhere and it must
                // re-register; that is a property of the data, not a limitation worth flagging.
                if (options.RetainSessionMasters
                    && psfBySession.TryGetValue(session.Id, out var priorPsf)
                    && RetainedMasterStore.TryRead(outDir, session.Id, out var retainedMaster, logger))
                {
                    try
                    {
                        progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} re-measuring PSF from retained master ...");
                        var (_, retainedWidth, retainedHeight) = retainedMaster.Shape;

                        // Strategy and train label come from the record, never guessed: the retained
                        // file IS whatever integrator produced it, and relabelling a drizzled master as
                        // AHD would corrupt exactly the per-channel comparison this report exists for.
                        var remeasured = await DatasetPsfNoiseReport.MeasureMasterAsync(
                            session.Id, priorPsf.OpticalTrain, retainedMaster,
                            retainedWidth, retainedHeight,
                            priorPsf.SubFwhm, priorPsf.SubHfd, priorPsf.SubEllipticity,
                            priorPsf.MasterStrategy,
                            logger: logger, cancellationToken: cancellationToken);

                        await DatasetPsfStore.AppendAsync(psfStorePath, remeasured, cancellationToken);
                        psfBySession[session.Id] = remeasured;
                        await WriteReportAsync(reportPath, psfBySession, sessionIds, logger, cancellationToken);
                        psfRemeasured++;
                        psfRemeasuredFromMaster++;
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Fall through to the full path rather than losing the session: a master that
                        // decodes but cannot be measured is worth one expensive retry, and the run
                        // already banked this session's tiles above.
                        logger?.LogWarning(ex, "  [{Session}] re-measure from the retained master failed; re-registering instead", session.Id);
                    }
                }

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
            var timings = new StageTimings();
            var sessionStart = StageTimings.Start();
            try
            {
                var calibrateStart = StageTimings.Start();
                var calibrator = await CalibrationResolver.ResolveAsync(
                    session, calGroups, masterCache, options.RequireGainMatch, options.MaxDarkTemperatureDelta, logger, cancellationToken);
                // Time only, no items: the masters cache means the first session to need a given
                // master pays to build it and every later one pays a read, so a per-session item
                // count would divide a shared cost by whichever session happened to come first. The
                // wall time is still worth having, because it is where a cold masters cache shows up.
                timings.Record(StageNames.Calibrate, calibrateStart);

                // A training sample needs dark subtraction: an uncalibrated N2N pair shares the
                // sensor's fixed-pattern dark signal (correlated between the two subs), so skip a
                // session with no resolved dark rather than poison the set. Opt-in so the prior
                // register-everything behaviour + existing tests are unchanged.
                if (options.RequireDarkCalibration && calibrator?.Dark is null)
                {
                    skippedNoDark++;
                    logger?.LogWarning("  [{Session}] SKIPPED -- no master dark resolved (RequireDarkCalibration)", session.Id);
                    progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} SKIPPED: no dark calibration");
                    // No census: nothing has been measured yet, and measuring purely to describe a
                    // session we are dropping for a calibration reason would cost a full pass over
                    // its lights for no decision.
                    await DatasetSkipStore.RecordAsync(skipStorePath, new DatasetSkipStore.SkippedSession(
                        SessionId: session.Id,
                        Reason: "no-master-dark",
                        Survivors: session.Lights.Length,
                        Registered: 0,
                        SkippedTooFewStars: 0,
                        SkippedNoQuadFit: 0,
                        ReferenceFile: null,
                        ReferenceStars: 0,
                        ReferenceQuads: 0,
                        Census: null), logger, cancellationToken);
                    continue;
                }

                var reg = await SessionRegistrar.RegisterAsync(
                    session, calibrator, scratchRoot,
                    options.QualityRejectSigma, options.QualityMaxRejectFraction, options.MinSubsPerSession,
                    hotPixelSigma: options.HotPixelSigma,
                    skipStorePath: skipStorePath,
                    timings: timings,
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
                    var retainStart = StageTimings.Start();
                    try
                    {
                        // Naming lives in RetainedMasterStore, which the re-measure path reads through:
                        // a reader that recomputed the path here would be one rename away from silently
                        // finding nothing and taking the expensive path, which reads as a slow run.
                        if (RetainedMasterStore.Write(
                            outDir, session.Id, reg.Master,
                            frameCount: reg.Subs.Length, strategy: reg.MasterStrategy, logger: logger))
                        {
                            mastersRetained++;
                            timings.Record(RetainStage, retainStart, items: 1,
                                pixels: (long)reg.CanvasWidth * reg.CanvasHeight * reg.Master.ChannelCount);
                        }
                        else
                        {
                            // Already on disk: the time is real (it still had to look) but no master
                            // was written, so it must not be charged pixels it did not move.
                            timings.Record(RetainStage, retainStart);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        timings.Record(RetainStage, retainStart);
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
                    var exportStart = StageTimings.Start();
                    var export = await DatasetTileExporter.ExportAsync(
                        reg, outDir, options.TileSize, options.CellsPerSession, options.SubsPerCell, logger, cancellationToken);
                    // Items are TILES and pixels are TILE pixels, because that is what this stage
                    // repeats over and writes. Normalising it per input frame is what made it look
                    // like a compute stage: it fans a cell out to eleven tiles by default, and its
                    // real cost is creating that many small files.
                    timings.Record(ExportStage, exportStart, export.Rows.Length,
                        (long)export.Rows.Length * options.TileSize * options.TileSize * reg.Master.ChannelCount);
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
                var psfStart = StageTimings.Start();
                var psf = await DatasetPsfNoiseReport.MeasureSessionAsync(reg, logger: logger, cancellationToken: cancellationToken);
                // Persisting the measurement must not be able to fail the SESSION. By the time we get
                // here the tiles are written and their manifest rows are appended, so the session IS
                // part of the dataset; letting an I/O fault fall to the per-session catch marked a
                // complete session FAILED and left the run's counts reporting it as both registered
                // and failed. It cost a real session 65 of 68 into a four-hour bake, when a reader
                // outside the process collided with this append (see JsonLinesFile.AppendAsync).
                //
                // The measurement is not lost so much as deferred: it is recoverable by
                // RegenPsfForExportedSessions, which re-registers only the sessions the report does
                // not cover. Counting it into psfMissing is what makes that discoverable, because the
                // end-of-run warning names both the shortfall and the flag that fixes it.
                try
                {
                    await DatasetPsfStore.AppendAsync(psfStorePath, psf, cancellationToken);
                    psfBySession[session.Id] = psf;
                    await WriteReportAsync(reportPath, psfBySession, sessionIds, logger, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    psfMissing++;
                    logger?.LogWarning(ex,
                        "  [{Session}] tiles are exported but its PSF record could not be persisted; the session STANDS and is recoverable with --regen-psf.",
                        session.Id);
                }
                timings.Record(PsfStage, psfStart, items: 1,
                    pixels: (long)reg.CanvasWidth * reg.CanvasHeight * reg.Master.ChannelCount);

                // Recorded only for a session that got all the way here, so the store holds costs
                // that are comparable to each other. A session that failed mid-pipeline has a
                // meaningless total and would drag the roll-up toward whatever stage it died in.
                var stages = timings.Snapshot();
                sessionTimings.Add(stages);
                var wall = Stopwatch.GetElapsedTime(sessionStart).TotalSeconds;
                await DatasetTimingStore.RecordAsync(timingStorePath, new DatasetTimingStore.SessionTiming(
                    SessionId: session.Id,
                    Camera: session.Camera,
                    Lights: session.Lights.Length,
                    Registered: reg.Subs.Length,
                    CanvasWidth: reg.CanvasWidth,
                    CanvasHeight: reg.CanvasHeight,
                    MasterStrategy: reg.MasterStrategy.ToString(),
                    WallSeconds: wall,
                    Stages: stages), logger, cancellationToken);
                logger?.LogInformation("  [{Session}] timing: {Timing}", session.Id, StageTimings.Describe(stages));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger?.LogError(ex, "  [{Session}] FAILED -- skipped", session.Id);
                progress?.Report($"[dataset] ({idx}/{sessions.Length}) {session.Id} FAILED: {ex.Message} -- skipped");
            }
            finally
            {
                TryDelete(scratchRoot, logger);
            }
        }

        // Run-level roll-up: one table instead of a log-parsing exercise. Charged against the loop's
        // OWN wall time rather than the sum of the stages, so the unaccounted row is visible; a
        // growing gap there means the stage boundaries have drifted from where the time actually
        // goes, which is the one thing a timing table has to be able to say about itself.
        if (sessionTimings.Count > 0)
        {
            // Run-level overhead folded in LAST so it sorts after the per-session stages, which is
            // where a reader looks for it: the stages describe the work, this describes the loop.
            var rollup = StageTimings.Merge([.. sessionTimings, overhead.Snapshot()]);
            var loopSeconds = Stopwatch.GetElapsedTime(loopStart).TotalSeconds;
            logger?.LogInformation("Stage roll-up over {Sessions} session(s):\n{Table}",
                sessionTimings.Count, StageTimings.DescribeTable(rollup, loopSeconds));
            progress?.Report($"[dataset] stage roll-up ({sessionTimings.Count} sessions, {loopSeconds / 60.0:F1} min):\n"
                + StageTimings.DescribeTable(rollup, loopSeconds));
        }

        // 4. PSF/noise distribution report, rebuilt from the checkpoint store so it covers every
        //    session ever measured into this output dir, not just this run's.
        await WriteReportAsync(reportPath, psfBySession, sessionIds, logger, cancellationToken);

        TryDelete(scratchRoot, logger);
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
            $"{(psfRemeasured > 0 ? $" ({psfRemeasured} PSF re-measured, {psfRemeasuredFromMaster} from retained masters)" : "")} -> {totalTiles} tiles " +
            $"({failed} failed, {skippedNoDark} skipped-no-dark); " +
            $"PSF report covers {psfBySession.Count(kv => sessionIds.Contains(kv.Key))}/{sessions.Length}; " +
            $"{(options.RetainSessionMasters ? $"{mastersRetained} master(s) retained; " : "")}" +
            $"parity {(parityChecked ? parityMaxDiff == 0.0 ? "OK" : $"DIFF {parityMaxDiff}" : "n/a")}");
        return new RunResult(
            sessions.Length, registered, failed, skippedNoDark, resumed, totalTiles, testSessions.Length,
            parityChecked, parityMaxDiff, manifestPath, splitPath, reportPath, psfStorePath, psfMissing, psfRemeasured,
            psfRemeasuredFromMaster);
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
        var onDisk = FileEnumeration.CountFiles(dir, DatasetTileExporter.TileExtension, recursive: false);
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

    /// <summary>
    /// Best-effort scratch hygiene, and it is <b>never allowed to throw</b>. This runs in the
    /// per-session <c>finally</c> of an unattended multi-hour bake, so an exception escaping here
    /// takes down every session still to come, after the work is already done.
    ///
    /// <para>Catching <see cref="IOException"/> alone was not enough, and the gap is easy to miss:
    /// <see cref="UnauthorizedAccessException"/> is a <em>sibling</em> of it, not a subclass, and it
    /// is what Windows reports for a file in the tree that is open or already pending delete; a
    /// sharing violation surfaces as <see cref="IOException"/>, so the two most likely reasons a
    /// scratch wipe fails were split across the caught and the uncaught case. Observed: a bake
    /// finished a session's tiles, PSF record and report, then died on
    /// <c>Access to the path 'warped_0135.fits' is denied</c>.</para>
    /// </summary>
    private static void TryDelete(string dir, ILogger? logger = null)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not remove scratch {Dir}; leaving it behind.", dir);
        }
    }

    /// <summary>
    /// Filename of the exclusive run lock taken on an output directory. Sits inside it; the scratch
    /// root's own lock deliberately does not (see <see cref="AcquireRunLock"/>).
    /// </summary>
    internal const string RunLockFileName = ".build.lock";

    /// <summary>
    /// Takes an exclusive lock held for the whole run, so a second build over the same directory
    /// fails immediately with an explanation instead of corrupting the first.
    ///
    /// <para><b>Why this is worth a lock file.</b> Two builds sharing state do not merely race, they
    /// destroy each other's work silently and in a way that reads like a bug in the pipeline: the
    /// per-session scratch is wiped in a <c>finally</c>, so one run deletes the warped subs the other
    /// is still tiling (<c>FileNotFoundException: warped_0113.fits</c>, thrown from the tile exporter,
    /// where nothing about the real cause is visible), and both append to the one manifest and PSF
    /// store. Diagnosing that from the artifacts afterwards is genuinely hard, because the surviving
    /// outputs look complete: the run that won wrote a full manifest and a valid PSF record, and the
    /// run that lost reported a failure pointing at a file it never touched.</para>
    ///
    /// <para><see cref="FileOptions.DeleteOnClose"/> so the lock cannot outlive the process even on a
    /// hard kill (the kernel closes the handle), which matters for a job that is expected to be
    /// stopped and resumed.</para>
    /// </summary>
    /// <param name="lockPath">Where to put the lock file. For a directory whose CONTENTS get deleted
    /// the lock must live outside it, or the open handle blocks the very wipe it is protecting.</param>
    /// <param name="subject">What is being locked, for the error message.</param>
    /// <param name="option">CLI option that points the run elsewhere, for the error message.</param>
    private static FileStream AcquireRunLock(string lockPath, string subject, string option)
    {
        var dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        try
        {
            return new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Another dataset build already holds {subject}. Two builds sharing it wipe each " +
                $"other's per-session scratch and both append to the same manifest, so the run that " +
                $"loses fails on a file it never wrote. Wait for the other run to finish, or point " +
                $"this one elsewhere with {option}.", ex);
        }
    }
}
