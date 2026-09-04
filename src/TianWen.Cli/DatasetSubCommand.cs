using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.AI.Imaging;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Dataset;
using TianWen.Lib.IO;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen dataset build</c> -- training-dataset builder (docs/plans/ai-denoise-deconv.md §2.4).
/// CLI contract: NO machine specifics; archive roots and the output dir are required parameters
/// with fail-fast validation; behavioural knobs carry portable defaults only.
/// </summary>
internal sealed class DatasetSubCommand(IConsoleHost consoleHost, IPlateSolverFactory? plateSolverFactory = null, ILogger<DatasetSubCommand>? logger = null)
{
    public Command Build()
    {
        var archiveRootOpt = new Option<string[]>("--archive-root")
        {
            Description = "Archive root scanned recursively for raw lights + calibration (repeatable; " +
                          "pass the canonical root first; it wins deduplication ties).",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var outOpt = new Option<string>("--out", "-o")
        {
            Description = "Output root for tiles/, manifest.jsonl, masters/ cache and stats/ (created if missing).",
            Required = true,
        };
        var minExposureOpt = new Option<double>("--min-exposure")
        {
            Description = "Minimum light exposure in seconds (shorter = planetary/lucky bursts, excluded).",
            DefaultValueFactory = _ => 10d,
        };
        var maxExposureOpt = new Option<double>("--max-exposure")
        {
            Description = "Maximum light exposure in seconds (longer = live-stack accumulations, excluded).",
            DefaultValueFactory = _ => 300d,
        };
        var excludeInstrumeOpt = new Option<string>("--exclude-instrume")
        {
            Description = "Case-insensitive wildcard on INSTRUME; matching frames are excluded " +
                          "(synthetic frames poison the noise model).",
            DefaultValueFactory = _ => "*simulator*",
        };
        var excludeObjectOpt = new Option<string>("--exclude-object")
        {
            Description = "Case-insensitive wildcard on OBJECT; matching lights are excluded " +
                          "(sessions are grouped by target, so e.g. '*vela*' drops one pointing " +
                          "cleanly even when it shares a dated LIGHT folder). Empty = no exclusion.",
            DefaultValueFactory = _ => "",
        };
        var excludePathOpt = new Option<string[]>("--exclude-path")
        {
            Description = "Case-insensitive wildcard(s) matched against each PATH SEGMENT; a frame " +
                          "under a matching directory is excluded (repeatable). Appended to the " +
                          "built-in processed-data exclusions. Use for deliberately-bad or " +
                          "processed folders, e.g. '*BAD LIGHT*'.",
            AllowMultipleArgumentsPerToken = true,
        };
        var minSubsOpt = new Option<int>("--min-subs")
        {
            Description = "Sessions with fewer gated lights are skipped.",
            DefaultValueFactory = _ => 10,
        };
        var tileSizeOpt = new Option<int>("--tile-size")
        {
            Description = "Tile edge length in pixels; must match the inference tiling contract.",
            DefaultValueFactory = _ => 256,
        };
        var cellsOpt = new Option<int>("--cells-per-session")
        {
            Description = "Upper bound of sampled grid cells per session (structure-biased).",
            DefaultValueFactory = _ => 300,
        };
        var subsPerCellOpt = new Option<int>("--subs-per-cell")
        {
            Description = "Sub tiles exported per sampled cell (any two form a Noise2Noise pair).",
            DefaultValueFactory = _ => 8,
        };
        var testFractionOpt = new Option<double>("--test-fraction")
        {
            Description = "Fraction of sessions held out as the pinned TEST split (by session, never by tile).",
            DefaultValueFactory = _ => 0.15d,
        };
        var requireDarkOpt = new Option<bool>("--require-dark")
        {
            Description = "Skip any session that resolves no master dark (instead of registering it " +
                          "uncalibrated). An uncalibrated N2N pair shares the sensor's fixed-pattern " +
                          "dark signal, so it is not a valid training sample; use this to drop e.g. a " +
                          "camera whose dark library is missing from the archive.",
        };
        var requireGainMatchOpt = new Option<bool>("--require-gain-match")
        {
            Description = "Reject a dark whose gain is known and differs from the lights (not just " +
                          "score-penalise it). The fixed-pattern amplitude a dark subtracts is gain-" +
                          "dependent, so a wrong-gain dark mis-scales it. Pairs with --require-dark to " +
                          "skip a session left with no same-gain dark. Unknown gain stays a wildcard; " +
                          "flats are unaffected. ON by default; pass '--require-gain-match false' to " +
                          "deliberately accept wrong-gain darks.",
            DefaultValueFactory = _ => true,
        };
        var maxDarkDeltaTOpt = new Option<double?>("--max-dark-delta-t")
        {
            Description = "Reject a dark whose sensor temperature is known and further than this many " +
                          "degrees C from the lights (not just score-penalise it). Dark current roughly " +
                          "doubles per 6 C, so a dark far off temperature under-subtracts badly and the " +
                          "residual fixed pattern stays correlated between the two subs of an N2N pair. " +
                          "Without this, temperature only weights the score, so a lone badly-mismatched " +
                          "dark still wins and the session is recorded as calibrated. Pairs with " +
                          "--require-dark to skip a session left with none. Unknown temperature stays a " +
                          "wildcard. Omit for no limit (the right value depends on the sensor).",
        };
        var hotPixelSigmaOpt = new Option<float>("--hot-pixel-sigma")
        {
            Description = "Bad-pixel masking sigma, one knob for both producers (their UNION is the " +
                          "shipped mask), matching the stacker's flag of the same name. It is the " +
                          "per-frame outlier sigma of the session-derived registration map (built " +
                          "whenever the session's own dither lets it be; flags defects at the " +
                          "lights' own exposure/gain/temperature, no dark needed) AND the STARTING " +
                          "CEILING for the dark-derived map, whose detector walks it DOWN to a defect " +
                          "budget -- so raising it does not tighten the dark mask the way it reads; " +
                          "sigma multiplies a quantized MAD and is not portable between darks of the " +
                          "same sensor at different gains. Pass 0 to disable masking entirely, which " +
                          "is how to run the mask-on vs mask-off control: comparing a bake against an " +
                          "EARLIER bake cannot attribute a change to the mask, because every other " +
                          "commit in between moved too. Only BayerDrizzle sessions can show a " +
                          "difference at all (a rejection-integrated master already sigma-clips hot " +
                          "pixels). Default 8.",
            DefaultValueFactory = _ => 8f,
        };
        var softwareOpt = new Option<string>("--software")
        {
            Description = "Case-insensitive wildcard on SWCREATE; only LIGHTS authored by matching " +
                          "software are kept (e.g. '*N.I.N.A.*' to exclude SharpCap planetary/EAA " +
                          "captures). Applies to lights only; calibration frames resolve regardless " +
                          "of authoring tool. Empty = no filter.",
            DefaultValueFactory = _ => "",
        };
        var discoverOnlyOpt = new Option<bool>("--discover-only")
        {
            Description = "Stop after session discovery and print the inventory (no tiles written).",
        };
        var regenPsfOpt = new Option<bool>("--regen-psf")
        {
            Description = "Re-measure PSF/noise stats for sessions whose tiles are already exported " +
                          "but which have no record in stats/psf-sessions.jsonl, so the report covers " +
                          "them. Leaves their tiles untouched. Costs a full re-registration per " +
                          "session (the profile is measured on the session master), which is why a " +
                          "plain --resume reports them as missing instead of doing this by default.",
        };
        var forcePsfOpt = new Option<bool>("--force-psf")
        {
            Description = "Re-measure PSF/noise stats for EVERY exported session, replacing records " +
                          "that already exist. Tiles are left untouched. Unlike --regen-psf, which " +
                          "only fills gaps and is therefore cheap and idempotent, this is what an " +
                          "estimator change needs: the records that are wrong are exactly the ones " +
                          "that already exist. Reads the RETAINED session master where one is on " +
                          "disk, which is the normal case and costs minutes; it only falls back to " +
                          "a full re-registration per session for masters that were never retained. " +
                          "Still never implied. The store is last-wins by session id, so the " +
                          "superseded records stay in the file and remain readable for comparison. " +
                          "Use with --resume and the SAME roots and gates as the original run.",
        };
        var resumeOpt = new Option<bool>("--resume")
        {
            Description = "Continue a stopped run: keep the existing manifest as the checkpoint and " +
                          "skip every session already fully exported to it whose tiles are still on " +
                          "disk (the interrupted session re-runs cleanly). Use the SAME roots and " +
                          "gates as the stopped run.",
        };
        var scratchRootOpt = new Option<string>("--scratch-root")
        {
            Description = "Parent for the per-session warped-sub scratch (a '_scratch' subdirectory " +
                          "is created and deleted there; the parent is never touched). Default: " +
                          "beside the output. Scratch is the build's dominant I/O and is pure churn: " +
                          "every sub is warped to a ~117 MB float32 FITS, read back by the " +
                          "integrator, then deleted. Putting it on a fast local disk when the " +
                          "archive lives on a slow one is often the single biggest speedup available. " +
                          "Size for the largest SESSION, not the archive: ~40 GB for 300 subs.",
            DefaultValueFactory = _ => "",
        };

        var buildCommand = new Command("build", "Build the training tile set from raw archive lights.")
        {
            Options =
            {
                archiveRootOpt, outOpt,
                minExposureOpt, maxExposureOpt, excludeInstrumeOpt, excludeObjectOpt, excludePathOpt, minSubsOpt,
                tileSizeOpt, cellsOpt, subsPerCellOpt, testFractionOpt, requireDarkOpt, requireGainMatchOpt, maxDarkDeltaTOpt, hotPixelSigmaOpt, softwareOpt, discoverOnlyOpt, resumeOpt, regenPsfOpt, forcePsfOpt, scratchRootOpt,
            },
        };
        buildCommand.SetAction(async (parseResult, ct) =>
        {
            var roots = parseResult.GetValue(archiveRootOpt)!;
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                {
                    consoleHost.WriteError($"Archive root does not exist: {root}");
                    return 1;
                }
            }
            var outDir = parseResult.GetValue(outOpt)!;
            var minExposure = parseResult.GetValue(minExposureOpt);
            var maxExposure = parseResult.GetValue(maxExposureOpt);
            if (minExposure <= 0 || maxExposure <= minExposure)
            {
                consoleHost.WriteError($"Invalid exposure range [{minExposure}, {maxExposure}] s.");
                return 1;
            }

            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [.. roots],
                OutputDir = outDir,
                MinExposure = TimeSpan.FromSeconds(minExposure),
                MaxExposure = TimeSpan.FromSeconds(maxExposure),
                ExcludeInstrumePattern = parseResult.GetValue(excludeInstrumeOpt)!,
                ExcludeObjectPattern = parseResult.GetValue(excludeObjectOpt)!,
                MinSubsPerSession = parseResult.GetValue(minSubsOpt),
                TileSize = parseResult.GetValue(tileSizeOpt),
                CellsPerSession = parseResult.GetValue(cellsOpt),
                SubsPerCell = parseResult.GetValue(subsPerCellOpt),
                TestFraction = parseResult.GetValue(testFractionOpt),
                RequireDarkCalibration = parseResult.GetValue(requireDarkOpt),
                RequireGainMatch = parseResult.GetValue(requireGainMatchOpt),
                MaxDarkTemperatureDelta = parseResult.GetValue(maxDarkDeltaTOpt),
                HotPixelSigma = parseResult.GetValue(hotPixelSigmaOpt),
                SoftwareIncludePattern = parseResult.GetValue(softwareOpt)!,
                ScratchRoot = parseResult.GetValue(scratchRootOpt)!,
                Resume = parseResult.GetValue(resumeOpt),
                RegenPsfForExportedSessions = parseResult.GetValue(regenPsfOpt),
                ForcePsfRemeasure = parseResult.GetValue(forcePsfOpt),
            };

            // User path exclusions append to the built-in processed-data defaults (never replace them).
            var extraExcludePaths = parseResult.GetValue(excludePathOpt);
            if (extraExcludePaths is { Length: > 0 })
            {
                options = options with { ExcludePathSegments = options.ExcludePathSegments.AddRange(extraExcludePaths) };
            }

            consoleHost.WriteScrollable($"[dataset] scanning {roots.Length} root(s) for raw lights ...");
            var (sessions, stats) = await SessionDiscovery.DiscoverAsync(options, logger, ct);

            consoleHost.WriteScrollable(
                $"[dataset] scanned {stats.Scanned} FITS: {stats.Sessions} sessions / {stats.Lights} lights kept; " +
                $"dropped {stats.NotLight} non-light, {stats.ExposureOutOfRange} exposure-out-of-range, " +
                $"{stats.InstrumentExcluded} excluded-instrument, {stats.SoftwareExcluded} excluded-software, " +
                $"{stats.ObjectExcluded} excluded-object, " +
                $"{stats.PathExcluded} excluded-path, " +
                (stats.NoSessionDir > 0
                    ? $"{stats.NoSessionDir} no-session-dir (frames sitting under a frame-type folder " +
                      "directly at the root: point --archive-root one level deeper, at the tree that " +
                      "holds session directories), "
                    : "") +
                $"{stats.ProductExcluded} products, {stats.Duplicates} duplicates, " +
                $"{stats.SessionsTooSmall} too-small sessions");

            // The filter census, printed before the per-session lines: it is what tells you whether
            // a night needs a sidecar (a large "(no FILTER header)" count) and whether one filter is
            // spelled two ways, both of which are invisible in the session list itself.
            var byFilter = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var session in sessions)
            {
                var key = session.FilterName.Length > 0 ? session.FilterName : "(no FILTER header)";
                byFilter[key] = byFilter.GetValueOrDefault(key) + session.Lights.Length;
            }
            if (byFilter.Count > 0)
            {
                consoleHost.WriteScrollable(
                    "[dataset] filters: " + string.Join(", ", byFilter.Select(kv => $"{kv.Key} x{kv.Value}")));
            }

            if (stats.Sidecar is { IsEmpty: false } sc)
            {
                consoleHost.WriteScrollable(
                    $"[dataset] {FrameMetaSidecarResolver.FileName}: {sc.Files} file(s), " +
                    $"filled {sc.FilterFilled} frame filter(s)" +
                    (sc.FilterAlreadyPresent > 0 ? $", {sc.FilterAlreadyPresent} frame(s) already had one" : "") +
                    (sc.Malformed > 0 ? $", {sc.Malformed} MALFORMED (ignored)" : ""));
            }

            foreach (var session in sessions)
            {
                var first = session.Lights[0].Meta;
                consoleHost.WriteScrollable(
                    $"[dataset]   {session.Id}: {session.Lights.Length} lights, " +
                    $"{first.ExposureDuration.TotalSeconds:0}s g{first.Gain}");
            }

            if (parseResult.GetValue(discoverOnlyOpt))
            {
                return 0;
            }

            // Full build: scan -> sessions + calibration groups -> pinned split -> per session
            // (resolve calibrator -> register -> export tiles) -> parity gate -> PSF/noise report.
            var progress = new Progress<string>(s => consoleHost.WriteScrollable(s));
            var result = await DatasetBuildRunner.RunAsync(options, logger, progress, ct);

            consoleHost.WriteScrollable(
                $"[dataset] {result.Registered}/{result.Sessions} sessions" +
                $"{(result.Resumed > 0 ? $" (+{result.Resumed} resumed)" : "")} -> {result.TotalTiles} tiles" +
                $"{(result.Failed > 0 ? $" ({result.Failed} FAILED, see log)" : "")}" +
                $"{(result.SkippedNoDark > 0 ? $" ({result.SkippedNoDark} skipped: no dark calibration)" : "")}" +
                $"{(result.PsfRemeasured > 0 ? $" ({result.PsfRemeasured} PSF re-measured)" : "")}; " +
                $"{result.TestSessions} test sessions held out; " +
                $"parity {(result.ParityChecked ? result.ParityMaxDiff == 0d ? "OK" : $"DIFF {result.ParityMaxDiff}" : "n/a")}");
            consoleHost.WriteScrollable($"[dataset] manifest: {result.ManifestPath}");
            consoleHost.WriteScrollable($"[dataset] split:    {result.SplitPath}");
            consoleHost.WriteScrollable($"[dataset] report:   {result.ReportPath}");
            consoleHost.WriteScrollable($"[dataset] psf store: {result.PsfStorePath}");
            if (result.PsfMissing > 0)
            {
                consoleHost.WriteScrollable(
                    $"[dataset] WARNING: {result.PsfMissing} session(s) have tiles but no PSF record, so the report does not " +
                    $"cover them. Re-run with --regen-psf to measure them (tiles are left untouched).");
            }

            // A non-zero parity diff means the stored tiles no longer equal the C# stretch of their
            // source -- train/inference skew. Fail the command so CI catches it.
            return result.ParityChecked && result.ParityMaxDiff != 0d ? 1 : 0;
        });

        return new Command("dataset", "Training-dataset tooling (see docs/plans/ai-denoise-deconv.md).")
        {
            Subcommands = { buildCommand, BuildReportCommand(consoleHost), BuildGradientReportCommand(), BuildDegradeCommand(), BuildCoverageCommand(consoleHost), BuildTagFilterCommand(), BuildTagObjectCommand(), BuildTagSiteElevationCommand() },
        };
    }

    /// <summary>
    /// <c>tianwen dataset coverage</c>: one TSV row per session stating what calibration a bake
    /// would resolve (dark / flat / pedestal / dark-scaling bias / bias availability / APP BPM
    /// presence, each with gain + offset + temperature + exposure + epoch). A sibling of
    /// <c>build</c> rather than a flag on it because it writes no tiles, needs no output dataset,
    /// and must include sessions a build would skip (the row FLAGS below-threshold sessions
    /// instead of hiding them).
    /// </summary>
    private Command BuildCoverageCommand(IConsoleHost consoleHost)
    {
        var archiveRootOpt = new Option<string[]>("--archive-root")
        {
            Description = "Archive root scanned recursively for lights + calibration (repeatable).",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var outOpt = new Option<string>("--out", "-o")
        {
            Description = "Directory the report files are written into (created if missing): " +
                          "calibration-coverage.tsv + calibration-coverage.md.",
            Required = true,
        };
        var softwareOpt = new Option<string>("--software")
        {
            Description = "Case-insensitive wildcard on SWCREATE; only LIGHTS authored by matching " +
                          "software get a row (e.g. '*N.I.N.A.*'). Calibration frames resolve " +
                          "regardless of authoring tool. Empty = no filter.",
            DefaultValueFactory = _ => "",
        };
        var minExposureOpt = new Option<double>("--min-exposure")
        {
            Description = "Minimum light exposure in seconds (the bake's own gate).",
            DefaultValueFactory = _ => 10d,
        };
        var maxExposureOpt = new Option<double>("--max-exposure")
        {
            Description = "Maximum light exposure in seconds (the bake's own gate).",
            DefaultValueFactory = _ => 300d,
        };
        var minSubsOpt = new Option<int>("--min-subs")
        {
            Description = "The bake threshold the below_bake_min_subs column is judged against. " +
                          "Sessions below it still get a row; this only sets where the flag flips.",
            DefaultValueFactory = _ => 10,
        };
        var requireGainMatchOpt = new Option<bool>("--require-gain-match")
        {
            Description = "Resolve darks under the strict gain gate (the production default). Pass " +
                          "'--require-gain-match false' to see what a lenient run would pick instead.",
            DefaultValueFactory = _ => true,
        };
        var maxDarkDeltaTOpt = new Option<double?>("--max-dark-delta-t")
        {
            Description = "Reject darks further than this many degrees C from the lights, as the " +
                          "bake would with the same flag. Omit for no limit.",
        };

        var command = new Command("coverage",
            "Per-session calibration coverage over the archive, resolved by the production matcher " +
            "(never a parallel scan): flats, dark-flats matching those flats, biases, darks/master " +
            "darks/BPMs, light counts, filter provenance, and gain/offset for each. Output is a " +
            "parsable TSV plus a markdown rollup.")
        {
            Options =
            {
                archiveRootOpt, outOpt, softwareOpt, minExposureOpt, maxExposureOpt, minSubsOpt,
                requireGainMatchOpt, maxDarkDeltaTOpt,
            },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var archiveRoots = parseResult.GetValue(archiveRootOpt)!;
            foreach (var root in archiveRoots)
            {
                if (!Directory.Exists(root))
                {
                    consoleHost.WriteError($"Archive root does not exist: {root}");
                    return 1;
                }
            }
            var outDir = parseResult.GetValue(outOpt)!;

            var options = new DatasetBuildOptions
            {
                ArchiveRoots = [.. archiveRoots.Select(Path.GetFullPath)],
                OutputDir = outDir,
                MinExposure = TimeSpan.FromSeconds(parseResult.GetValue(minExposureOpt)),
                MaxExposure = TimeSpan.FromSeconds(parseResult.GetValue(maxExposureOpt)),
                MinSubsPerSession = parseResult.GetValue(minSubsOpt),
                SoftwareIncludePattern = parseResult.GetValue(softwareOpt) ?? "",
                RequireGainMatch = parseResult.GetValue(requireGainMatchOpt),
                MaxDarkTemperatureDelta = parseResult.GetValue(maxDarkDeltaTOpt),
            };

            var result = await CalibrationCoverageReport.WriteAsync(
                options, outDir, logger,
                progress: new Progress<string>(line => consoleHost.WriteScrollable(line)),
                cancellationToken: ct);

            consoleHost.WriteScrollable($"[coverage] {result.Sessions} session(s) -> {result.TsvPath}");
            consoleHost.WriteScrollable($"[coverage] rollup: {result.SummaryPath}");
            return 0;
        });

        return command;
    }

    /// <summary>
    /// <c>tianwen dataset report</c> re-renders the PSF/noise report from what is already on disk.
    ///
    /// <para>A sibling command rather than a <c>build --report-only</c> flag, for one concrete
    /// reason: <c>build</c> requires <c>--archive-root</c>, and a re-render must work with the
    /// archive unmounted, since it never reads it. Threading an exemption through that required
    /// option would either weaken the guard for real builds or replace its error message with a
    /// hand-rolled one. This also drops fifteen build options that would all be meaningless here.</para>
    /// </summary>
    private static Command BuildReportCommand(IConsoleHost consoleHost)
    {
        var outOpt = new Option<string>("--out", "-o")
        {
            Description = "Dataset output root to re-render in place (the one a build wrote).",
            Required = true,
        };

        var command = new Command("report",
            "Re-render stats/psf-noise-report.md from stats/psf-sessions.jsonl. No archive scan, " +
            "nothing re-measured, no tile touched -- for when the report's INPUTS changed but the " +
            "measurements did not (a telescope alias, a rendering fix). Sessions come from the tile " +
            "manifest. To re-MEASURE, that is 'build --regen-psf' (fills gaps) or '--force-psf' " +
            "(replaces records).")
        {
            Options = { outOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var outDir = parseResult.GetValue(outOpt)!;
            if (!Directory.Exists(outDir))
            {
                consoleHost.WriteError($"Dataset output root does not exist: {outDir}");
                return 1;
            }

            var result = await DatasetBuildRunner.RunAsync(
                // No archive roots, and that is the feature: a re-render reads the output directory
                // only, so it works with the archive disk unmounted.
                new DatasetBuildOptions { ArchiveRoots = [], OutputDir = outDir, ReportOnly = true },
                progress: new Progress<string>(line => consoleHost.WriteScrollable(line)),
                cancellationToken: ct);

            consoleHost.WriteScrollable($"[dataset] report: {result.ReportPath}");
            if (result.PsfMissing > 0)
            {
                consoleHost.WriteScrollable(
                    $"[dataset] WARNING: {result.PsfMissing} session(s) have tiles but no PSF record, so the " +
                    $"report does not cover them. A re-render cannot fix that (the profile is measured on the " +
                    $"session master); use 'dataset build --regen-psf'.");
            }
            return 0;
        });

        return command;
    }

    /// <summary>
    /// <c>tianwen dataset gradient-report</c>: run the classical background fit over retained session
    /// masters and render <c>stats/gradient-report.md</c> (docs/plans/gradient-remover-training.md, G1).
    /// Sibling of <c>report</c>: reads masters, never the archive, never the tiles; accumulates in
    /// <c>stats/gradient-masters.jsonl</c> so a killed run keeps what it finished and a re-run only
    /// measures what is new.
    /// </summary>
    private Command BuildGradientReportCommand()
    {
        var mastersOpt = new Option<string[]>("--masters")
        {
            Description = "A directory of master FITS files (not recursive) or a single master; repeatable.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var outOpt = new Option<string>("--out", "-o")
        {
            Description = "Output root; the store and report land under <out>/stats.",
            Required = true,
        };
        var noSweepOpt = new Option<bool>("--no-sweep")
        {
            Description = "Skip the threshold sweep (eight extra fits per master) and record the default fit only.",
        };
        var noSolveOpt = new Option<bool>("--no-solve")
        {
            Description = "Do not plate-solve; the frame's horizon and Moon directions stay unknown.",
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "Re-measure masters already in the store (the new record wins; nothing is erased).",
        };

        var command = new Command("gradient-report",
            "Fit every master with the classical background extractor and report the gradient distribution " +
            "(amplitude in background sigma, direction against the horizon and the Moon, shape, and how far " +
            "the two reasoned thresholds move the model).")
        {
            Options = { mastersOpt, outOpt, noSweepOpt, noSolveOpt, forceOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var files = ImmutableArray.CreateBuilder<string>();
            foreach (var entry in parseResult.GetValue(mastersOpt) ?? [])
            {
                if (Directory.Exists(entry))
                {
                    files.AddRange(FileEnumeration.EnumerateFiles(entry, MasterExtensions, recursive: false));
                }
                else if (File.Exists(entry))
                {
                    files.Add(entry);
                }
                else
                {
                    consoleHost.WriteError($"--masters entry does not exist: {entry}");
                    return 1;
                }
            }
            if (files.Count == 0)
            {
                consoleHost.WriteError("No master FITS files found under --masters.");
                return 1;
            }

            var solve = !parseResult.GetValue(noSolveOpt);
            if (solve && plateSolverFactory is null)
            {
                consoleHost.WriteScrollable("[gradient] no plate solver is available on this host; running as --no-solve");
                solve = false;
            }

            var result = await DatasetGradientReport.RunAsync(
                new DatasetGradientReport.RunOptions(files.ToImmutable(), parseResult.GetValue(outOpt)!,
                    Sweep: !parseResult.GetValue(noSweepOpt), Solve: solve, Force: parseResult.GetValue(forceOpt)),
                plateSolverFactory, logger,
                progress: new Progress<string>(line => consoleHost.WriteScrollable(line)),
                cancellationToken: ct);

            consoleHost.WriteScrollable(
                $"[gradient] measured {result.Measured} ({result.Solved} solved), skipped {result.Skipped}, failed {result.Failed}; report: {result.ReportPath}");
            return result.Failed > 0 && result.Measured == 0 ? 2 : 0;
        });

        return command;
    }

    private static readonly string[] MasterExtensions = [".fits", ".fit", ".fits.gz", ".fit.gz", ".fz"];

    /// <summary>
    /// <c>tianwen dataset degrade</c>: the shared degradation exporter
    /// (docs/plans/model-training-roadmap.md section 1 item 3). It reads a bake's RETAINED LINEAR
    /// masters plus its P0 manifest, degrades each cell in linear units, and writes a cache whose
    /// manifest is P0-shaped, so the trainer's <c>--prepare</c> reads it with no change: slot 0 is the
    /// clean target, slots 1..8 are independent degradations of it.
    ///
    /// <para>A separate command from <c>build</c> because it needs no archive, no calibration and no
    /// registration: the expensive half already happened and its output is on disk.</para>
    /// </summary>
    private Command BuildDegradeCommand()
    {
        var bakeOpt = new Option<string>("--bake")
        {
            Description = "Dataset bake to read: it must hold tiles-manifest.jsonl and session-masters/.",
            Required = true,
        };
        var outOpt = new Option<string>("--out", "-o")
        {
            Description = "Where the degraded cache is written. Must not be the bake itself.",
            Required = true,
        };
        var modeOpt = new Option<string>("--mode")
        {
            Description = "noise (denoiser E1: inject noise only) or blur (deconvolver E2: PSF blur, then noise).",
            DefaultValueFactory = _ => "noise",
        };
        var shapeOpt = new Option<string>("--shape")
        {
            Description = "white (uncorrelated) or warped (correlated by a bilinear resample, as a registered stack's noise is). The H2 arms.",
            DefaultValueFactory = _ => "white",
        };
        var drawsOpt = new Option<int>("--draws")
        {
            Description = "Degraded draws per cell. Eight fills the trainer's sub slots.",
            DefaultValueFactory = _ => 8,
        };
        var cellsOpt = new Option<int>("--cells")
        {
            Description = "Cells per session, in canonical order, or 0 for every cell the bake has.",
            DefaultValueFactory = _ => 300,
        };
        var sessionsOpt = new Option<int>("--sessions")
        {
            Description = "Cap on sessions (0 = all). A smoke export is a handful.",
            DefaultValueFactory = _ => 0,
        };
        var seedOpt = new Option<int>("--seed")
        {
            Description = "Base seed; every (session, cell, draw) derives its own from it.",
            DefaultValueFactory = _ => 1,
        };
        var warpSigmaOpt = new Option<double>("--warp-sigma")
        {
            Description = "Warped shape only: extra per-realisation smoothing in pixels, standing in for a " +
                          "resampling kernel wider than bilinear. Calibrate it with --measure-shape against " +
                          "the bake's own real pairs; 0 is bilinear alone.",
            DefaultValueFactory = _ => 0d,
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "Re-export sessions already present in degradations.jsonl.",
        };
        var measureOpt = new Option<bool>("--measure-shape")
        {
            Description = "After exporting, measure band1/band0 of the injected draws and of the bake's own " +
                          "real sub and half-master pairs with the same code, which is the only way the numbers compare.",
        };

        var command = new Command("degrade",
            "Export degraded/clean training pairs from a bake's retained linear masters: inject noise " +
            "(denoiser) or blur then noise (deconvolver), through the P0 export path so both sides share one domain.")
        {
            Options = { bakeOpt, outOpt, modeOpt, shapeOpt, drawsOpt, cellsOpt, sessionsOpt, seedOpt, warpSigmaOpt, forceOpt, measureOpt },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var modeText = parseResult.GetValue(modeOpt) ?? "noise";
            if (!Enum.TryParse<DatasetDegradationExporter.DegradationMode>(modeText, ignoreCase: true, out var mode))
            {
                consoleHost.WriteError($"--mode must be noise or blur, got '{modeText}'");
                return 1;
            }
            var shapeText = parseResult.GetValue(shapeOpt) ?? "white";
            if (!Enum.TryParse<DatasetDegradationExporter.NoiseShape>(shapeText, ignoreCase: true, out var shape))
            {
                consoleHost.WriteError($"--shape must be white or warped, got '{shapeText}'");
                return 1;
            }

            var options = new DatasetDegradationExporter.Options(
                BakeRoot: parseResult.GetValue(bakeOpt)!,
                OutDir: parseResult.GetValue(outOpt)!,
                Mode: mode,
                Shape: shape,
                Draws: parseResult.GetValue(drawsOpt),
                CellsPerSession: parseResult.GetValue(cellsOpt),
                MaxSessions: parseResult.GetValue(sessionsOpt),
                Seed: parseResult.GetValue(seedOpt),
                WarpResampleSigma: parseResult.GetValue(warpSigmaOpt),
                Force: parseResult.GetValue(forceOpt));

            var result = await DatasetDegradationExporter.RunAsync(options, logger, ct);
            var degraded = result.Sessions.Sum(s => s.DegradedTiles);
            consoleHost.WriteScrollable(
                $"[degrade] {result.Sessions.Length} sessions, {degraded} degraded tiles + {result.Sessions.Sum(s => s.CleanTiles)} clean, " +
                $"skipped {result.Skipped}, failed {result.Failed}; worst clean-tile parity against the bake {result.WorstParity:E1}");

            if (parseResult.GetValue(measureOpt))
            {
                foreach (var m in await DatasetDegradationExporter.MeasureShapeAsync(options.OutDir, options.BakeRoot, cancellationToken: ct))
                {
                    consoleHost.WriteScrollable(
                        $"[degrade] shape {m.Label,-24} pairs {m.Pairs,4}  band0 {m.Band0:E2}  band1 {m.Band1:E2}  band2 {m.Band2:E2}  band1/band0 {m.Ratio:F3}");
                }
            }

            return result.Failed > 0 && result.Sessions.Length == 0 ? 2 : 0;
        });

        return command;
    }

    /// <summary>
    /// <c>tianwen dataset tag-filter</c> writes a FILTER card into frames whose capture software
    /// never recorded one (a hand-fitted filter: N.I.N.A. only models a motorised wheel).
    ///
    /// <para>The alternative is <c>.tianwen-meta.json</c>, which declares the same thing and writes
    /// nothing. This exists for when you want the fact in the frames themselves, where every other
    /// tool can see it. It edits the primary header surgically and copies every other byte verbatim
    /// (see <see cref="FitsHeaderEditor"/>), and it is a DRY RUN unless <c>--apply</c> is passed.</para>
    ///
    /// <para>A de-duplicated archive files some nights twice, as hard links to one frame, and those
    /// are refused by default. <c>--hard-links relink</c> brings the other names along; because a
    /// shared frame has no per-name header, that necessarily changes files outside <c>--path</c>, so
    /// the summary lists them and the dry run lists them first.</para>
    /// </summary>
    private Command BuildTagFilterCommand()
        => BuildTagCardCommand(
            label: "tag-filter",
            keyword: "FILTER",
            cardComment: "Filter name",
            valueOptionName: "--filter",
            valueDescription: "Filter name to write, exactly as the capture software would have (e.g. \"Optolong L-Ultimate 3nm\").",
            summary: "Write a FILTER card into frames that never recorded one (header-surgical; dry run by default).",
            defaultFrameTypes: ["Light", "Flat", "DarkFlat"],
            frameTypeDescription: "IMAGETYP values to tag. Defaults to Light+Flat+DarkFlat, the types a filter is " +
                                  "meaningful for, so bad-pixel maps and master darks sitting in the same folder are left alone.",
            refusalAdvice: "Pass --hard-links relink to bring the other names along (they name the same frame, so the " +
                           "same filter applies to all of them), or declare the filter with a .tianwen-meta.json " +
                           "sidecar instead, which writes nothing and breaks no links.",
            readCurrent: meta => meta.Filter.IdentityKey);

    /// <summary>
    /// <c>tianwen dataset tag-object</c> corrects the <c>OBJECT</c> card, which is the target's name as
    /// it was typed into the sequence and therefore the one piece of capture metadata with a spelling
    /// mistake in it.
    ///
    /// <para>It is not cosmetic. <c>LightGroupKey</c> partitions a folder of lights by <c>OBJECT</c>, so
    /// the card decides what groups with what and names the master that comes out; and for a comet the
    /// card is the only place the frame says which body it is, which <c>CometDesignation.TryParse</c>
    /// then has to read back. A typo is a target the catalog cannot resolve and a master filed under a
    /// misspelling forever.</para>
    ///
    /// <para>Unlike <c>tag-filter</c>, this one <b>relabels</b> rather than fills in a blank, so the
    /// safety is the other way round: <c>--overwrite-existing</c> is implied, and <c>--expect</c> is
    /// the guard, refusing any frame whose <c>OBJECT</c> is not exactly the string being corrected.
    /// Without it, pointing this at a folder that happens to hold two targets renames both.</para>
    /// </summary>
    private Command BuildTagObjectCommand()
        => BuildTagCardCommand(
            label: "tag-object",
            keyword: "OBJECT",
            cardComment: "Name of the object of interest",
            valueOptionName: "--object",
            valueDescription: "Object name to write. For a comet prefer a form its own catalog can read back, " +
                              "e.g. \"10P/Tempel 2\" -- CometDesignation.TryParse takes the designation off the front.",
            summary: "Correct the OBJECT card on frames whose target was mistyped at capture (header-surgical; dry run by default).",
            defaultFrameTypes: ["Light"],
            frameTypeDescription: "IMAGETYP values to relabel. Defaults to Light alone: a flat or a dark carries " +
                                  "whatever the capture software parked in OBJECT (\"FlatWizard\", \"Target\") and " +
                                  "naming a sky target on one would be a lie about what it is.",
            refusalAdvice: "Pass --hard-links relink to bring the other names along (they name the same frame, so the " +
                           "same target applies to all of them).",
            readCurrent: meta => meta.ObjectName,
            relabels: true);

    /// <summary>
    /// Corrects <c>SITEELEV</c>, the observing site's elevation in metres.
    ///
    /// <para>Unlike the other two this one applies to EVERY frame type, because the site is a
    /// property of where the rig stood and a dark was taken in the same place as a light. A comet
    /// stack is the consumer that cares: a topocentric ephemeris is asked from the site the frames
    /// record. Be clear about the stakes though -- an elevation wrong by 46 m moves a derived comet
    /// rate by 0.00002 px over a night, so this corrects the RECORD rather than fixing a measurement.
    /// Latitude and longitude are the site values that actually matter.</para>
    /// </summary>
    private Command BuildTagSiteElevationCommand()
        => BuildTagCardCommand(
            label: "tag-site-elevation",
            keyword: "SITEELEV",
            cardComment: "[m] Observation site elevation",
            valueOptionName: "--elevation",
            valueDescription: "Site elevation in METRES above mean sea level, e.g. 74. Written as a numeric card, "
                              + "right-justified and unquoted, so readers that type their cards get a number.",
            summary: "Correct the SITEELEV card on frames whose capture profile recorded the wrong site elevation "
                     + "(header-surgical; dry run by default).",
            defaultFrameTypes: ["Light", "Dark", "Flat", "Bias", "DarkFlat"],
            frameTypeDescription: "IMAGETYP values to amend. Defaults to every frame type, unlike tag-filter and "
                                  + "tag-object: where the rig STOOD is true of a dark, a bias and a dark-flat just as "
                                  + "much as of a light. DarkFlat is in the list deliberately -- it is a real captured "
                                  + "frame type in the standard CMOS flat workflow, and leaving it out made this claim "
                                  + "of \"every frame type\" quietly false.",
            refusalAdvice: "Pass --hard-links relink to bring the other names along (they name the same frame, so the "
                           + "same site applies to all of them).",
            readCurrent: meta => float.IsNaN(meta.SiteElevation)
                ? null
                : meta.SiteElevation.ToString(CultureInfo.InvariantCulture),
            relabels: true,
            numeric: true);

    /// <summary>Reads a FITS numeric card body (or a --expect value) in the invariant culture, which
    /// is the only correct reading: a header is ASCII and its numbers are never localised, so a
    /// machine set to a decimal-comma locale must not parse "74.0" as 740.</summary>
    private static bool TryParseCard(string? text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private Command BuildTagCardCommand(
        string label,
        string keyword,
        string cardComment,
        string valueOptionName,
        string valueDescription,
        string summary,
        string[] defaultFrameTypes,
        string frameTypeDescription,
        string refusalAdvice,
        Func<ImageMeta, string?> readCurrent,
        bool relabels = false,
        bool numeric = false)
    {
        var pathOpt = new Option<string>("--path")
        {
            Description = "Directory holding the frames to tag (see --recursive).",
            Required = true,
        };
        var valueOpt = new Option<string>(valueOptionName)
        {
            Description = valueDescription,
            Required = true,
        };
        var expectOpt = new Option<string?>("--expect")
        {
            Description = $"Only touch a frame whose current {keyword} already reads this"
                          + (numeric
                              ? ", compared as a NUMBER so 74 matches a card reading 74.0. "
                              : ", compared exactly. ")
                          + "The guard against amending a folder that turns out to be less uniform than you "
                          + "thought; the dry run reports every frame it refused and what that frame actually says.",
        };
        var recursiveOpt = new Option<bool>("--recursive") { Description = "Descend into subdirectories.", DefaultValueFactory = _ => true };
        var applyOpt = new Option<bool>("--apply") { Description = "Actually write. Omit for a dry run that reports what would change." };
        var overwriteOpt = new Option<bool>("--overwrite-existing")
        {
            Description = relabels
                ? $"Ignored: correcting {keyword} is by definition a replacement, so this is always on. Use --expect to bound it."
                : $"Also replace a {keyword} card that already has a value. Off by default: filling in what " +
                  "was never recorded is a different and far safer act than relabelling a frame that stated its own.",
        };
        var frameTypesOpt = new Option<string[]>("--frame-type")
        {
            Description = frameTypeDescription,
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => defaultFrameTypes,
        };
        var hardLinksOpt = new Option<FitsHeaderEditor.HardLinkPolicy>("--hard-links")
        {
            Description = "What to do with a frame that other paths also point at (a de-duplicated archive files " +
                          "one night twice). refuse: skip it and report where the other names are (default). " +
                          "relink: amend one name, then re-point the others at the amended frame, so all the names " +
                          "keep sharing one physical file. diverge: amend this name only and leave the others on " +
                          "the old header.",
            DefaultValueFactory = _ => FitsHeaderEditor.HardLinkPolicy.Refuse,
        };

        var command = new Command(label, summary)
        {
            pathOpt, valueOpt, expectOpt, recursiveOpt, applyOpt, overwriteOpt, frameTypesOpt, hardLinksOpt,
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(pathOpt)!;
            if (!Directory.Exists(path))
            {
                consoleHost.WriteError($"Directory does not exist: {path}");
                return 1;
            }
            var cardValue = parseResult.GetValue(valueOpt)!;
            // A numeric card is parsed ONCE, here, so a malformed number fails before a single file
            // is opened rather than 186 times inside the loop.
            var numericValue = 0.0;
            if (numeric && !TryParseCard(cardValue, out numericValue))
            {
                consoleHost.WriteError($"[{label}] {valueOptionName}: expected a number, got '{cardValue}'");
                return 1;
            }
            var expect = parseResult.GetValue(expectOpt);
            var apply = parseResult.GetValue(applyOpt);
            // A relabelling command replaces by definition: the card it corrects already has the wrong
            // value in it, so honouring the default here would skip every frame it exists to fix.
            var overwrite = relabels || parseResult.GetValue(overwriteOpt);
            var hardLinks = parseResult.GetValue(hardLinksOpt);

            var allowed = new HashSet<FrameType>();
            foreach (var name in parseResult.GetValue(frameTypesOpt)!)
            {
                if (FrameType.FromFITSValue(name) is { } ft)
                {
                    allowed.Add(ft);
                }
                else
                {
                    consoleHost.WriteError($"Unrecognised frame type: {name}");
                    return 1;
                }
            }

            var files = FileEnumeration.EnumerateFiles(path, FitsFolderFrameSource.FitsExtensions, parseResult.GetValue(recursiveOpt))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            consoleHost.WriteScrollable(
                $"[{label}] {(apply ? "APPLYING" : "DRY RUN")}: {keyword}='{cardValue}'"
                + (expect is null ? "" : $" where {keyword}='{expect}'")
                + $" over {files.Length} FITS file(s) under {path}");

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var failures = 0;
            var refused = 0;
            // Every other name a relink reaches, so the summary can say what was touched beyond the
            // files that were actually walked. A shared frame cannot be tagged under one name only,
            // so this list is the honest scope of the command and not a footnote.
            var reached = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // The --expect guard is checked here rather than inside the editor because it is a
                    // question about the CALLER's intent ("I believe this folder is all one target"),
                    // not about whether the frame can be amended. Reading the header first costs one
                    // 2880-byte block against a file we are about to rewrite anyway.
                    if (expect is not null)
                    {
                        var current = Image.TryReadFitsHeader(file, out var head)
                            ? readCurrent(head.Meta) : null;
                        // A numeric card is compared as a NUMBER, never as text: the same elevation is
                        // spelled 74, 74.0 and 7.4E1 by different capture software, and a string compare
                        // would refuse every frame while reporting a value that looks identical to the one
                        // asked for -- the most confusing possible failure.
                        var matches = numeric
                            ? TryParseCard(current, out var currentValue) && TryParseCard(expect, out var expectValue)
                                && Math.Abs(currentValue - expectValue) <= 1e-6 * Math.Max(1.0, Math.Abs(expectValue))
                            : string.Equals(current, expect, StringComparison.Ordinal);
                        if (!matches)
                        {
                            var reason = $"skipped (--expect: {keyword} is {current ?? "unreadable"})";
                            counts[reason] = counts.GetValueOrDefault(reason) + 1;
                            continue;
                        }
                    }

                    var result = numeric
                        ? await FitsHeaderEditor.SetNumericCardAsync(
                            file, keyword, numericValue, cardComment, allowed,
                            overwriteExisting: overwrite, hardLinks: hardLinks, apply: apply,
                            cancellationToken: ct)
                        : await FitsHeaderEditor.SetStringCardAsync(
                            file, keyword, cardValue, cardComment, allowed,
                            overwriteExisting: overwrite, hardLinks: hardLinks, apply: apply,
                            cancellationToken: ct);
                    var key = result.Outcome switch
                    {
                        FitsHeaderEditor.TagOutcome.Tagged => "tagged",
                        FitsHeaderEditor.TagOutcome.TaggedAndRelinked =>
                            $"tagged + {(apply ? "re-pointed" : "would re-point")} its other names",
                        FitsHeaderEditor.TagOutcome.AlreadyPresent => $"skipped (already has {result.ExistingValue})",
                        FitsHeaderEditor.TagOutcome.FrameTypeExcluded => $"skipped ({result.Detail})",
                        FitsHeaderEditor.TagOutcome.MultiplyLinked => "SKIPPED (hard-linked, see below)",
                        _ => $"UNREADABLE ({result.Detail})",
                    };
                    if (result.Outcome is FitsHeaderEditor.TagOutcome.MultiplyLinked)
                    {
                        refused++;
                    }
                    if (result.Outcome is FitsHeaderEditor.TagOutcome.TaggedAndRelinked)
                    {
                        foreach (var other in result.OtherLinks)
                        {
                            reached.Add(other);
                        }
                    }
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Report and continue: one locked or bad file must not abandon the rest, and the
                    // editor guarantees it left that original untouched.
                    failures++;
                    consoleHost.WriteError($"[{label}] FAILED {file}: {ex.Message}");
                }
            }

            foreach (var (key, count) in counts)
            {
                consoleHost.WriteScrollable($"[{label}]   {key}: {count}");
            }
            if (refused > 0)
            {
                consoleHost.WriteScrollable(
                    $"[{label}] {refused} file(s) are hard-linked, so another path holds the same frame and would " +
                    "keep the untagged header. That is how one night ends up filed twice and grouped as two " +
                    $"different sessions. {refusalAdvice}");
            }
            // Names already in the walked set are reported by their own outcome line, so what is left
            // is the honest answer to "what did this change that I did not point it at". Subtracting
            // them also makes a dry run and an --apply agree: without it the dry run counts a swept
            // sibling that the real run finds already tagged, and the two totals disagree for no
            // reason a reader could work out.
            reached.ExceptWith(files.Select(Path.GetFullPath));
            if (reached.Count > 0)
            {
                // Worth its own paragraph rather than a footnote: the command was given one directory
                // and this is what it touches beyond it. Unavoidable, since a shared frame has no
                // per-name header to differ in, but never something to discover afterwards.
                consoleHost.WriteScrollable(
                    $"[{label}] {reached.Count} further file(s) are OTHER NAMES for the same frames, outside the " +
                    $"{files.Length} walked, and are amended with them (a shared frame cannot carry two headers):");
                foreach (var other in reached.Take(20))
                {
                    consoleHost.WriteScrollable($"[{label}]     {other}");
                }
                if (reached.Count > 20)
                {
                    consoleHost.WriteScrollable($"[{label}]     ... and {reached.Count - 20} more");
                }
            }
            if (!apply)
            {
                consoleHost.WriteScrollable($"[{label}] nothing was written; re-run with --apply to commit.");
            }
            return failures > 0 ? 1 : 0;
        });

        return command;
    }
}
