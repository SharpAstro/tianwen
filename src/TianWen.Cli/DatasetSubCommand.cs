using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
using TianWen.AI.Imaging;
using TianWen.Lib.Imaging.Dataset;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen dataset build</c> -- training-dataset builder (docs/plans/ai-denoise-deconv.md §2.4).
/// CLI contract: NO machine specifics — archive roots and the output dir are required parameters
/// with fail-fast validation; behavioural knobs carry portable defaults only.
/// </summary>
internal sealed class DatasetSubCommand(IConsoleHost consoleHost, ILogger<DatasetSubCommand>? logger = null)
{
    public Command Build()
    {
        var archiveRootOpt = new Option<string[]>("--archive-root")
        {
            Description = "Archive root scanned recursively for raw lights + calibration (repeatable; " +
                          "pass the canonical root first — it wins deduplication ties).",
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
                          "dark signal, so it is not a valid training sample — use this to drop e.g. a " +
                          "camera whose dark library is missing from the archive.",
        };
        var requireGainMatchOpt = new Option<bool>("--require-gain-match")
        {
            Description = "Reject a dark whose gain is known and differs from the lights (not just " +
                          "score-penalise it). The fixed-pattern amplitude a dark subtracts is gain-" +
                          "dependent, so a wrong-gain dark mis-scales it. Pairs with --require-dark to " +
                          "skip a session left with no same-gain dark. Unknown gain stays a wildcard; " +
                          "flats are unaffected.",
        };
        var softwareOpt = new Option<string>("--software")
        {
            Description = "Case-insensitive wildcard on SWCREATE; only LIGHTS authored by matching " +
                          "software are kept (e.g. '*N.I.N.A.*' to exclude SharpCap planetary/EAA " +
                          "captures). Applies to lights only — calibration frames resolve regardless " +
                          "of authoring tool. Empty = no filter.",
            DefaultValueFactory = _ => "",
        };
        var discoverOnlyOpt = new Option<bool>("--discover-only")
        {
            Description = "Stop after session discovery and print the inventory (no tiles written).",
        };
        var resumeOpt = new Option<bool>("--resume")
        {
            Description = "Continue a stopped run: keep the existing manifest as the checkpoint and " +
                          "skip every session already fully exported to it (the interrupted session " +
                          "re-runs cleanly). Use the SAME roots and gates as the stopped run.",
        };

        var buildCommand = new Command("build", "Build the training tile set from raw archive lights.")
        {
            Options =
            {
                archiveRootOpt, outOpt,
                minExposureOpt, maxExposureOpt, excludeInstrumeOpt, excludeObjectOpt, excludePathOpt, minSubsOpt,
                tileSizeOpt, cellsOpt, subsPerCellOpt, testFractionOpt, requireDarkOpt, requireGainMatchOpt, softwareOpt, discoverOnlyOpt, resumeOpt,
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
                SoftwareIncludePattern = parseResult.GetValue(softwareOpt)!,
                Resume = parseResult.GetValue(resumeOpt),
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
                $"{stats.ProductExcluded} products, {stats.Duplicates} duplicates, " +
                $"{stats.SessionsTooSmall} too-small sessions");
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
                $"{(result.SkippedNoDark > 0 ? $" ({result.SkippedNoDark} skipped: no dark calibration)" : "")}; " +
                $"{result.TestSessions} test sessions held out; " +
                $"parity {(result.ParityChecked ? result.ParityMaxDiff == 0d ? "OK" : $"DIFF {result.ParityMaxDiff}" : "n/a")}");
            consoleHost.WriteScrollable($"[dataset] manifest: {result.ManifestPath}");
            consoleHost.WriteScrollable($"[dataset] split:    {result.SplitPath}");
            consoleHost.WriteScrollable($"[dataset] report:   {result.ReportPath}");

            // A non-zero parity diff means the stored tiles no longer equal the C# stretch of their
            // source -- train/inference skew. Fail the command so CI catches it.
            return result.ParityChecked && result.ParityMaxDiff != 0d ? 1 : 0;
        });

        return new Command("dataset", "Training-dataset tooling (see docs/plans/ai-denoise-deconv.md).")
        {
            Subcommands = { buildCommand },
        };
    }
}
