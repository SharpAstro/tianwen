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
    ICelestialObjectDB catalogDb,
    TianWen.Lib.Imaging.Enhancement.SharpenPipeline sharpenPipeline)
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
        var formatOpt = new Option<ImageOutputFormat>("--output-format")
        {
            Description = "2D-viewer companion alongside master_*.fits + master_*_autocrop.fits. 'png' (default) = stretched 8-bit-per-channel RGBA via MasterPreviewRenderer (SPCC + WB + auto-stretch + sRGB ICC). 'jxr' = JPEG XR with float-true HDR pixels (BD32F mono / BD16F RGB) -- writes the master FITS verbatim with NO SPCC / stretch baked in; SPCC diagnostics are skipped for the JXR path. 'none' = no companion (just the FITS outputs). Windows Photos has no JXR codec; open .jxr in PixInsight / GIMP+plugin / Affinity / IrfanView+plugin.",
            DefaultValueFactory = _ => ImageOutputFormat.Png,
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
        var qualityRejectSigmaOpt = new Option<float?>("--quality-reject-sigma")
        {
            Description = "Enable per-frame quality filtering at this sigma threshold: a frame is dropped from integration when its median HFD or ellipticity exceeds median + sigma * 1.4826 * MAD of the session. An 80% keep floor caps rejection at the worst 20% by severity. 3.0 is a conservative starting value -- catches clear outliers (bloated low-altitude frames, wind-trailed frames) without biting into the body of the distribution. Off by default.",
        };
        var referenceFrameHintOpt = new Option<string?>("--reference-frame")
        {
            Description = "Debug knob: pin the reference frame to the first candidate whose path contains this case-insensitive substring (e.g. '_0233' to pin to that filename). Falls back to the composite-quality score picker when unset or no match. Use to isolate per-frame artifacts that correlate with reference choice -- a frame near the session's temporal middle keeps per-frame rotation residuals symmetric, which balances per-channel drizzle coverage.",
        };
        var noBayerDrizzleOpt = new Option<bool>("--no-bayer-drizzle")
        {
            Description = "Opt out of drizzle auto-selection. On RGGB sensors with >= 60 matched frames the selector picks BayerDrizzle / TilePipelinedDrizzle by default (3-5x faster than the standard AHD-debayer path on big-N sessions); this flag forces the standard path instead. Useful for A/B against a reference master, or when you specifically want kappa-sigma rejection rather than drizzle's per-cell coverage map. --strategy overrides still win -- forcing BayerDrizzle bypasses this flag.",
        };
        var includeIntegrationsOpt = new Option<bool>("--include-integrations")
        {
            Description = "Keep frames with a non-zero FITS STACK_N header (integrated masters from a previous run) as scan inputs. Default behaviour is to drop them since stale masters in adjacent output-*/ dirs otherwise pollute the next session's grouping. Pass this flag for two-stage mosaic stacking: integrate each panel separately, then re-run with --include-integrations against the panel masters to produce the final mosaic. .rejection.fits sidecars are ALWAYS dropped regardless of this flag.",
        };
        var enhanceOpt = new Option<bool>("--enhance")
        {
            Description = "Run the canonical AI sharpen pipeline (gradient correction + remove stars + sharpen stars + deconvolve + denoise + recombine) against each master after integration. Writes master_*_sharpened.fits and (when autocrop is active) master_*_sharpened_autocrop.fits alongside the raw masters; the linear masters are never overwritten. Requires the ONNX models materialised via tools/tianwen-ai-models-fetch.ps1.",
        };
        var enhanceBlendOpt = new Option<float>("--enhance-blend")
        {
            Description = "Uniform AI strength for the sharpen pass in [0, 1]. 0 = each AI step is a no-op (master passes through unmodified); 1 = full AI output. Applied to the stellar-sharpen, non-stellar deconv, and denoise steps. Implies --enhance.",
            DefaultValueFactory = _ => 1.0f,
        };

        var stackCommand = new Command("stack", "Stack a folder of FITS lights into a master frame.")
        {
            Arguments = { dataRootArg },
            Options =
            {
                outputOpt, groupFilterOpt, groupExcludeOpt, strategyOpt,
                centroidDebayerOpt, stackDebayerOpt,
                snrMinOpt, minStarsOpt, quadStarsOpt,
                formatOpt, noPlateSolveOpt,
                drizzlePixfracOpt, drizzleMinFramesOpt,
                splitByPierSideOpt, hotPixelSigmaOpt,
                qualityRejectSigmaOpt, referenceFrameHintOpt,
                noBayerDrizzleOpt, includeIntegrationsOpt,
                enhanceOpt, enhanceBlendOpt,
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
            var disableBayerDrizzle = parseResult.GetValue(noBayerDrizzleOpt);
            // Drizzle options now apply for both forced drizzle AND auto-
            // picked drizzle, so build them whenever drizzle could be
            // selected (anything except a non-drizzle forced strategy).
            // The pixfrac / min-frames flags stay no-ops only when the
            // user has BOTH forced a non-drizzle strategy AND set them
            // -- the previous warning fired too eagerly under auto-pick.
            DrizzleOptions? drizzleOptions = null;
            var forcedNonDrizzle = forcedStrategy is { } fs
                && fs != IntegrationStrategyKind.BayerDrizzle
                && fs != IntegrationStrategyKind.TilePipelinedDrizzle;
            if (!forcedNonDrizzle)
            {
                drizzleOptions = new DrizzleOptions(Pixfrac: pixfrac, MinFrameCount: drizzleMinFrames);
            }
            else if (pixfrac != 1.0f || drizzleMinFrames != 60)
            {
                consoleHost.WriteScrollable(
                    $"[stack] warning: --drizzle-* options ignored when --strategy={forcedStrategy} (non-drizzle)");
            }
            // --enhance-blend is the implicit gate for --enhance: passing a
            // blend < 1 without --enhance still enables the pipeline (the
            // blend value would otherwise be silently ignored, which would
            // be more confusing than auto-on).
            var enhanceBlendArg = Math.Clamp(parseResult.GetValue(enhanceBlendOpt), 0f, 1f);
            var enhanceArg = parseResult.GetValue(enhanceOpt) || enhanceBlendArg < 1.0f;
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
                HotPixelSigma: parseResult.GetValue(hotPixelSigmaOpt),
                QualityRejectSigma: parseResult.GetValue(qualityRejectSigmaOpt),
                ReferenceFrameHint: parseResult.GetValue(referenceFrameHintOpt),
                DisableBayerDrizzle: disableBayerDrizzle,
                IncludeIntegrations: parseResult.GetValue(includeIntegrationsOpt),
                Enhance: enhanceArg,
                EnhanceBlend: enhanceBlendArg);

            var format = parseResult.GetValue(formatOpt);
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
                progress: progress,
                sharpenPipeline: options.Enhance ? sharpenPipeline : null);
            // Only the PNG path needs the renderer (SPCC + auto-stretch +
            // sRGB ICC). JXR writes the master FITS verbatim via
            // Image.WriteJxrAsync -- no SPCC, no stretch -- so the renderer
            // is unused on that path. None of course needs neither.
            var renderer = format == ImageOutputFormat.Png ? new MasterPreviewRenderer(catalogDb, rendererLogger) : null;

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

                // JXR companion path: short-circuit before the PNG renderer
                // block. Both master + autocrop are written as float-true
                // .jxr next to the FITS, no SPCC / stretch involved.
                if (format == ImageOutputFormat.Jxr && result.MasterFitsPath is { } masterFitsForJxr)
                {
                    var autocropFitsForJxr = Path.Combine(
                        Path.GetDirectoryName(masterFitsForJxr) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(masterFitsForJxr) + "_autocrop.fits");
                    if (Image.TryReadFitsFile(masterFitsForJxr, out var masterImageForJxr, out _) && masterImageForJxr is not null)
                    {
                        try
                        {
                            var jxrPath = Path.ChangeExtension(masterFitsForJxr, ".jxr");
                            await masterImageForJxr.WriteJxrAsync(jxrPath, DebayerAlgorithm.VNG, ct);
                            consoleHost.WriteScrollable($"[stack] {result.GroupSlug}: wrote {Path.GetFileName(jxrPath)} (JXR HDR)");
                        }
                        catch (Exception ex)
                        {
                            consoleHost.WriteError($"[stack] {result.GroupSlug}: master JXR failed: {ex.Message}");
                        }
                    }
                    if (File.Exists(autocropFitsForJxr)
                        && Image.TryReadFitsFile(autocropFitsForJxr, out var cropImageForJxr, out _)
                        && cropImageForJxr is not null)
                    {
                        try
                        {
                            var cropJxrPath = Path.ChangeExtension(autocropFitsForJxr, ".jxr");
                            await cropImageForJxr.WriteJxrAsync(cropJxrPath, DebayerAlgorithm.VNG, ct);
                            consoleHost.WriteScrollable($"[stack] {result.GroupSlug}: wrote {Path.GetFileName(cropJxrPath)} (JXR HDR)");
                        }
                        catch (Exception ex)
                        {
                            consoleHost.WriteError($"[stack] {result.GroupSlug}: autocrop JXR failed: {ex.Message}");
                        }
                    }
                }

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

                    SpccDiagnostics? spcc = null;
                    if (Image.TryReadFitsFile(masterPath, out var masterImage, out var wcs) && masterImage is not null)
                    {
                        var pngPath = result.PreviewPngPath ?? Path.ChangeExtension(masterPath, ".png");
                        try
                        {
                            spcc = await renderer.RenderAsync(
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

                    // Emit SPCC summary + per-gate funnel as part of the deterministic stack
                    // output. The renderer also logs at Information level for the file logger,
                    // but Release console is Warning-floored so we surface it here through
                    // IConsoleHost instead.
                    //
                    // Funnel reads: "of <detected> stars FindStarsAsync reported, breakdown by
                    // gate to the photometric kappa-sigma stage" -- lets us answer "why are we
                    // losing X% of stars" by which counter dominates (no-cand = catalog missing,
                    // tol-miss = WCS distortion, no-bv = Tycho-2 photometry gaps, k-rej =
                    // kappa-sigma outliers after the match).
                    if (spcc is { } s)
                    {
                        consoleHost.WriteScrollable(
                            $"[stack] {result.GroupSlug}: SPCC WB=({s.WbR:F3}, {s.WbG:F3}, {s.WbB:F3}) " +
                            $"{s.FinalMatches}/{s.InitialMatches} Tycho-2 matches " +
                            $"({s.Iterations} iters, {s.Elapsed.TotalMilliseconds:F0} ms)");

                        var f = s.Funnel;
                        var kRej = s.InitialMatches - s.FinalMatches;
                        var gate = f.MagGateActive ? $"zp={f.ZeroPoint:F2}" : "off";
                        // Probe stats are NaN when the field was too sparse for adaptive sizing;
                        // surface "probe=off" in that case so the log is unambiguous.
                        var probe = float.IsNaN(f.ProbeMedianArcsec)
                            ? "off"
                            : $"med={f.ProbeMedianArcsec:F1}\" mad={f.ProbeMadArcsec:F1}\"";
                        consoleHost.WriteScrollable(
                            $"[stack] {result.GroupSlug}: SPCC funnel " +
                            $"detected={f.Detected} wcs-fail={f.WcsFail} no-cand={f.NoCandidates} " +
                            $"tol-miss={f.TolMissed} rej-mag={f.RejMagDiff} no-bv={f.NoBmv} no-v={f.NoVmag} " +
                            $"accepted={f.Accepted} k-rej={kRej} " +
                            $"tol={f.EffectiveRadiusArcsec:F1}\" probe={probe} mag-gate={gate}");

                        // Per-quadrant tol-miss + rej-mag localisation -- a corner heavy on
                        // tol-miss% points at lens distortion (SIP); a corner heavy on rej-mag
                        // points at dense-field close-pair contamination that the magnitude
                        // gate caught (or at zero-point bias if every quadrant rises together).
                        consoleHost.WriteScrollable(
                            $"[stack] {result.GroupSlug}: SPCC by-quadrant (det/tol-miss/rej-mag/acc)  " +
                            $"TL={f.TL.Detected}/{f.TL.TolMissed}/{f.TL.RejMagDiff}/{f.TL.Accepted}  " +
                            $"TR={f.TR.Detected}/{f.TR.TolMissed}/{f.TR.RejMagDiff}/{f.TR.Accepted}  " +
                            $"BL={f.BL.Detected}/{f.BL.TolMissed}/{f.BL.RejMagDiff}/{f.BL.Accepted}  " +
                            $"BR={f.BR.Detected}/{f.BR.TolMissed}/{f.BR.RejMagDiff}/{f.BR.Accepted}");
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
                            // Discard the autocrop's SpccDiagnostics return: it computes the
                            // same WB on the same stats source we already reported above.
                            _ = await renderer.RenderAsync(
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
