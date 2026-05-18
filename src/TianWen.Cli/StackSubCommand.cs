using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen stack &lt;data-root&gt;</c> -- end-to-end stacking against a
/// folder of FITS lights + calibration. Wraps
/// <see cref="StackingPipeline"/> with arg parsing and an optional PNG
/// preview render via <see cref="MasterPreviewRenderer"/>.
/// </summary>
internal sealed class StackSubCommand(
    IConsoleHost consoleHost,
    ILogger<StackingPipeline> pipelineLogger,
    ILogger<MasterPreviewRenderer> rendererLogger,
    ICelestialObjectDB catalogDb)
{
    public Command Build()
    {
        var dataRootArg = new Argument<string>("data-root")
        {
            Description = "Folder recursively scanned for FITS lights + bias/dark/flat calibration frames.",
        };

        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Where master_*.fits, master_*.png and the masters/ cache live. Defaults to <data-root>/output.",
        };
        var groupFilterOpt = new Option<string>("--group-filter")
        {
            Description = "Substring filter on light-group slug; only matching groups are processed. Empty = all.",
            DefaultValueFactory = _ => "",
        };
        var groupExcludeOpt = new Option<string>("--group-exclude")
        {
            Description = "Substring exclude on light-group slug. Empty = none.",
            DefaultValueFactory = _ => "",
        };
        var strategyOpt = new Option<IntegrationStrategyKind?>("--strategy")
        {
            Description = "Force a specific integration strategy. Default = let the selector pick.",
        };
        var centroidDebayerOpt = new Option<DebayerAlgorithm>("--centroid-debayer")
        {
            Description = "Debayer algorithm for the registration pass (star-detection accuracy).",
            DefaultValueFactory = _ => DebayerAlgorithm.VNG,
        };
        var stackDebayerOpt = new Option<DebayerAlgorithm>("--stack-debayer")
        {
            Description = "Debayer algorithm for the integration pass (colour fidelity).",
            DefaultValueFactory = _ => DebayerAlgorithm.AHD,
        };
        var snrMinOpt = new Option<float>("--snr-min")
        {
            Description = "FindStarsAsync SNR floor.",
            DefaultValueFactory = _ => 5f,
        };
        var minStarsOpt = new Option<int>("--min-stars")
        {
            Description = "FindStarsAsync retry floor (forces a second pass at a lower detection level).",
            DefaultValueFactory = _ => 2000,
        };
        var quadStarsOpt = new Option<int>("--quad-stars")
        {
            Description = "Top-K brightest stars used for quad fingerprints during registration.",
            DefaultValueFactory = _ => 500,
        };
        var noPngOpt = new Option<bool>("--no-png")
        {
            Description = "Skip the PNG preview render (just emit master FITS + autocrop FITS).",
        };
        var noPlateSolveOpt = new Option<bool>("--no-plate-solve")
        {
            Description = "Skip plate-solving the master (master FITS still written, just without WCS).",
        };
        var drizzlePixfracOpt = new Option<float>("--drizzle-pixfrac")
        {
            Description = "BayerDrizzle: linear drop size in [0, 1]. 1.0 = full unit-square drop (default). Ignored unless --strategy BayerDrizzle.",
            DefaultValueFactory = _ => 1.0f,
        };
        var drizzleMinFramesOpt = new Option<int>("--drizzle-min-frames")
        {
            Description = "BayerDrizzle: minimum matched frames required before the strategy runs. Defaults to 60; lowering it risks NaN holes in R and B channels. Ignored unless --strategy BayerDrizzle.",
            DefaultValueFactory = _ => 60,
        };
        var splitByPierSideOpt = new Option<bool>("--split-by-pierside")
        {
            Description = "Sub-partition each light group by FITS PIERSIDE (pre/post meridian flip) and write separate masters per pier side. Useful for diagnosing drizzle streaks tied to the flip, or for capture setups that don't update BayerOffset post-flip. Filenames pick up _pierE / _pierW / _pierUnknown suffixes.",
        };
        var hotPixelSigmaOpt = new Option<float>("--hot-pixel-sigma")
        {
            Description = "Threshold (Gaussian sigmas above dark master median) for hot-pixel masking. Flagged pixels are NaN'd in calibrated lights so integration ignores them. Default 8 (hot pixels typically score 100+). Pass 0 to disable masking.",
            DefaultValueFactory = _ => 8.0f,
        };

        var stackCommand = new Command("stack", "Stack a folder of FITS lights into a master frame.")
        {
            Arguments = { dataRootArg },
            Options =
            {
                outputOpt, groupFilterOpt, groupExcludeOpt, strategyOpt,
                centroidDebayerOpt, stackDebayerOpt,
                snrMinOpt, minStarsOpt, quadStarsOpt,
                noPngOpt, noPlateSolveOpt,
                drizzlePixfracOpt, drizzleMinFramesOpt,
                splitByPierSideOpt, hotPixelSigmaOpt,
            },
        };
        stackCommand.SetAction(async (parseResult, ct) =>
        {
            var dataRoot = parseResult.GetValue(dataRootArg)!;
            if (!Directory.Exists(dataRoot))
            {
                consoleHost.WriteError($"Data root does not exist: {dataRoot}");
                return 1;
            }

            var outputDir = parseResult.GetValue(outputOpt) ?? Path.Combine(dataRoot, "output");
            var forcedStrategy = parseResult.GetValue(strategyOpt);
            var pixfrac = parseResult.GetValue(drizzlePixfracOpt);
            var drizzleMinFrames = parseResult.GetValue(drizzleMinFramesOpt);
            DrizzleOptions? drizzleOptions = null;
            if (forcedStrategy is IntegrationStrategyKind.BayerDrizzle)
            {
                drizzleOptions = new DrizzleOptions(Pixfrac: pixfrac, MinFrameCount: drizzleMinFrames);
            }
            else if (pixfrac != 1.0f || drizzleMinFrames != 60)
            {
                consoleHost.WriteScrollable(
                    "[stack] warning: --drizzle-* options ignored when --strategy != BayerDrizzle");
            }
            var options = new StackingOptions(
                DataRoot: dataRoot,
                OutputDir: outputDir,
                GroupFilter: parseResult.GetValue(groupFilterOpt) ?? "",
                GroupExclude: parseResult.GetValue(groupExcludeOpt) ?? "",
                ForcedStrategy: forcedStrategy,
                CentroidDebayerAlg: parseResult.GetValue(centroidDebayerOpt),
                StackDebayerAlg: parseResult.GetValue(stackDebayerOpt),
                SnrMin: parseResult.GetValue(snrMinOpt),
                MinStars: parseResult.GetValue(minStarsOpt),
                QuadStars: parseResult.GetValue(quadStarsOpt),
                DrizzleOptions: drizzleOptions,
                SplitByPierSide: parseResult.GetValue(splitByPierSideOpt),
                HotPixelSigma: parseResult.GetValue(hotPixelSigmaOpt));

            var noPng = parseResult.GetValue(noPngOpt);
            var skipPlateSolve = parseResult.GetValue(noPlateSolveOpt);

            // Forward in-flight pipeline progress to the console. Per-group
            // result lines still go through the foreach below; this hook is
            // for the long-running stages (Integrating in particular) where
            // a single group can sit silent for minutes otherwise. We rate-
            // limit Integrating ticks to one line per ~5% strategy progress
            // to avoid flooding the terminal on tile-pipelined strategies
            // that report per-strip.
            var lastIntegrationPct = -1;
            var progress = new Progress<StackingProgress>(p =>
            {
                switch (p.Phase)
                {
                    case StackingPhase.Scanning:
                        consoleHost.WriteScrollable("[stack] scanning...");
                        break;
                    case StackingPhase.BuildingMasters:
                        consoleHost.WriteScrollable("[stack] building calibration masters...");
                        break;
                    case StackingPhase.Registering when p.TotalItems > 0:
                        consoleHost.WriteScrollable($"[stack] {p.GroupSlug}: register {p.CompletedItems}/{p.TotalItems}");
                        break;
                    case StackingPhase.Integrating when p.Integration is { } integ && integ.TotalItems > 0:
                        var pct = (int)(100.0 * integ.CompletedItems / integ.TotalItems);
                        if (pct >= lastIntegrationPct + 5 || pct == 100)
                        {
                            lastIntegrationPct = pct;
                            consoleHost.WriteScrollable($"[stack] {p.GroupSlug}: integrate {integ.CompletedItems}/{integ.TotalItems} ({pct}%)");
                        }
                        break;
                    case StackingPhase.PostProcessing:
                        consoleHost.WriteScrollable($"[stack] {p.GroupSlug}: plate-solve + write");
                        lastIntegrationPct = -1; // reset for the next group
                        break;
                }
            });

            // The DI-registered ICelestialObjectDB is a singleton but its
            // catalog grid stays empty until InitDBAsync runs (loads
            // Tycho-2 + IC/NGC etc into memory). The GUI / FitsViewer
            // trigger this via their own startup paths; the CLI has been
            // resolving the uninitialised DB and silently feeding it to
            // the master plate-solve, which then returned matched=0/0
            // because the catalog grid was empty (no stars to match
            // against, regardless of how many image stars FindStars
            // detected). Fire-and-forget here so the load (a few hundred
            // ms to seconds depending on disk) overlaps with the scan /
            // register / integrate phases (60-90s typically). The
            // post-processor calls InitDBAsync again just before the
            // CatalogPlateSolver -- since the underlying init is
            // idempotent and singleton-cached, that second call is
            // instant when the background load has completed and
            // otherwise blocks the minimal necessary window. Skipped
            // entirely under --no-plate-solve.
            if (!skipPlateSolve)
            {
                _ = catalogDb.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            }

            var pipeline = new StackingPipeline(
                options,
                pipelineLogger,
                catalogDb: skipPlateSolve ? null : catalogDb,
                progress: progress);
            var renderer = noPng ? null : new MasterPreviewRenderer(catalogDb, rendererLogger);

            var groupCount = 0;
            var integratedCount = 0;
            await foreach (var result in pipeline.RunAsync(ct))
            {
                groupCount++;
                if (!string.IsNullOrEmpty(result.SkipReason))
                {
                    consoleHost.WriteScrollable($"[stack] {result.GroupSlug}: SKIPPED ({result.SkipReason})");
                    continue;
                }
                integratedCount++;
                consoleHost.WriteScrollable(
                    $"[stack] {result.GroupSlug}: {result.FramesMatched}/{result.FramesAttempted} matched, " +
                    $"wrote {Path.GetFileName(result.MasterFitsPath)} in {result.Elapsed.TotalSeconds:F1}s");

                if (renderer is not null && result.Result is { } intResult && result.MasterFitsPath is { } masterPath)
                {
                    // The master FITS we just wrote has WCS embedded
                    // (if plate-solve ran). Re-read it so the renderer
                    // gets the exact pixels + WCS the file holds. Render
                    // both the full master and the autocrop variant (the
                    // autocrop is what most users actually look at -- it
                    // strips the NaN-edge ring -- so emitting just one
                    // PNG left half the deliverables off-disk).
                    //
                    // Load the autocrop FIRST so we can pass it as the
                    // stats source for the full-canvas render. Without
                    // this the full PNG's WB + bg-neut would be derived
                    // from the partial-coverage edges (drizzle's per-
                    // Bayer-position uncovered cells, or just the NaN
                    // ring around any registered stack) and diverge from
                    // the autocrop's stats; the two PNGs then end up
                    // with different colour casts on identical data.
                    var autocropFitsPath = Path.Combine(
                        Path.GetDirectoryName(masterPath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(masterPath) + "_autocrop.fits");
                    Image? cropImage = null;
                    WCS? cropWcs = null;
                    if (File.Exists(autocropFitsPath))
                    {
                        Image.TryReadFitsFile(autocropFitsPath, out cropImage, out cropWcs);
                    }

                    if (Image.TryReadFitsFile(masterPath, out var masterImage, out var wcs) && masterImage is not null)
                    {
                        var pngPath = result.PreviewPngPath ?? Path.ChangeExtension(masterPath, ".png");
                        try
                        {
                            await renderer.RenderAsync(
                                masterImage,
                                masterImage.ImageMeta,
                                wcs,
                                statsSource: cropImage,
                                pngPath,
                                statsWcs: cropWcs,
                                ct: ct);
                        }
                        catch (Exception ex)
                        {
                            consoleHost.WriteError($"[stack] {result.GroupSlug}: PNG render failed: {ex.Message}");
                        }
                    }
                    if (cropImage is not null)
                    {
                        // Pass cropImage as statsSource explicitly even
                        // though it equals master here -- keeps both call
                        // sites symmetric: "always use the autocrop region
                        // as the stats source, regardless of which image
                        // we're rendering pixels of."
                        var cropPngPath = Path.ChangeExtension(autocropFitsPath, ".png");
                        try
                        {
                            await renderer.RenderAsync(
                                cropImage,
                                cropImage.ImageMeta,
                                cropWcs,
                                statsSource: cropImage,
                                cropPngPath,
                                statsWcs: cropWcs,
                                ct: ct);
                        }
                        catch (Exception ex)
                        {
                            consoleHost.WriteError($"[stack] {result.GroupSlug}: autocrop PNG render failed: {ex.Message}");
                        }
                    }
                }
            }

            consoleHost.WriteScrollable($"[stack] done: {integratedCount} integrated / {groupCount} groups");
            return 0;
        });
        return stackCommand;
    }
}
