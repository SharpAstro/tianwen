using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Linq;
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
        var discoverOnlyOpt = new Option<bool>("--discover-only")
        {
            Description = "Stop after session discovery and print the inventory (no tiles written).",
        };

        var buildCommand = new Command("build", "Build the training tile set from raw archive lights.")
        {
            Options =
            {
                archiveRootOpt, outOpt,
                minExposureOpt, maxExposureOpt, excludeInstrumeOpt, minSubsOpt,
                tileSizeOpt, cellsOpt, subsPerCellOpt, discoverOnlyOpt,
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
                MinSubsPerSession = parseResult.GetValue(minSubsOpt),
                TileSize = parseResult.GetValue(tileSizeOpt),
                CellsPerSession = parseResult.GetValue(cellsOpt),
                SubsPerCell = parseResult.GetValue(subsPerCellOpt),
            };

            consoleHost.WriteScrollable($"[dataset] scanning {roots.Length} root(s) for raw lights ...");
            var (sessions, stats) = await SessionDiscovery.DiscoverAsync(options, logger, ct);

            consoleHost.WriteScrollable(
                $"[dataset] scanned {stats.Scanned} FITS: {stats.Sessions} sessions / {stats.Lights} lights kept; " +
                $"dropped {stats.NotLight} non-light, {stats.ExposureOutOfRange} exposure-out-of-range, " +
                $"{stats.InstrumentExcluded} excluded-instrument, {stats.PathExcluded} excluded-path, " +
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

            // Later P0 stages (quality gate -> calibrate -> register -> integrate -> tile export ->
            // stats -> split) attach here as they land; until then be explicit rather than silent.
            consoleHost.WriteScrollable("[dataset] tile export stages are not implemented yet — run with --discover-only for the inventory.");
            return 2;
        });

        return new Command("dataset", "Training-dataset tooling (see docs/plans/ai-denoise-deconv.md).")
        {
            Subcommands = { buildCommand },
        };
    }
}
