using System;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary-stack &lt;ser-file&gt;</c> -- end-to-end planetary lucky-imaging stack of a single
/// SER video: grade the frames by sharpness, keep the best N%, align (global disk-COM + phase correlation,
/// then feature-driven alignment points + a per-AP displacement mesh), integrate with per-AP "best-of"
/// quality weighting, and optionally wavelet-sharpen. Wraps <see cref="LuckyImagingStacker"/>. Writes the
/// linear integrated master as FITS, an optional wavelet-sharpened master FITS, and a stretched PNG preview
/// (auto-stretch + sky-bg white balance via <see cref="MasterPreviewRenderer"/>; no plate-solve / SPCC --
/// a planet has no field stars).
/// </summary>
internal sealed class PlanetaryStackSubCommand(
    IConsoleHost consoleHost,
    MasterPreviewRenderer previewRenderer)
{
    private enum QualityMetric
    {
        Laplacian,
        Gradient,
    }

    public Command Build()
    {
        var serArg = new Argument<string>("ser-file")
        {
            Description = "Path to the .ser planetary video to stack.",
        };

        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output directory for master_*.fits / *.png. Defaults to the SER file's directory.",
        };
        var labelOpt = new Option<string?>("--label")
        {
            Description = "Filename prefix for this run's outputs (e.g. 'k10_ap400'), so multiple experiments can share one output folder without colliding. Empty = no prefix.",
        };
        var keepOpt = new Option<double>("--keep")
        {
            Description = "Fraction of frames to keep, sharpest first (lucky imaging). 0.25 = best 25%.",
            DefaultValueFactory = _ => 0.25,
        };
        var qualityOpt = new Option<QualityMetric>("--quality")
        {
            Description = "Sharpness metric for grading + per-AP best-of. Laplacian variance (default) or Sobel gradient energy.",
            DefaultValueFactory = _ => QualityMetric.Laplacian,
        };
        var globalOpt = new Option<bool>("--global")
        {
            Description = "Use whole-disk translation only (the cheap global align), skipping alignment points + the mesh warp. Faster, but no per-region distortion correction.",
        };
        var drizzleOpt = new Option<float>("--drizzle")
        {
            Description = "Bayer drizzle at this output scale (e.g. 1.5 = Drizzle1.5). Forward-scatters raw CFA samples onto an upscaled grid with NO interpolation/demosaic -- sharper than the mesh-warp path and recovers sub-Bayer resolution. 0 (default) = off (mesh/translate integrator). Bayer source only; alignment is whole-disk global.",
            DefaultValueFactory = _ => 0f,
        };
        var drizzlePixfracOpt = new Option<float>("--drizzle-pixfrac")
        {
            Description = "Drizzle drop size in (0, 1]. 1.0 (default) = full unit drop (robust coverage). Lower (0.6-0.8) is sharper but needs more frames. Ignored unless --drizzle > 0.",
            DefaultValueFactory = _ => 1.0f,
        };
        var noPerPointOpt = new Option<bool>("--no-per-point")
        {
            Description = "Disable per-AP best-of weighting (each output pixel drawn more from frames locally sharp there). Folds frames in with their global quality weight only. Ignored under --global.",
        };
        var noSignalGateOpt = new Option<bool>("--no-signal-gate")
        {
            Description = "Disable the signal-confidence gate on the per-AP best-of weighting. The gate keeps best-of on the bright disk but uses an unbiased mean in faint regions; disabling it lets the local-sharpness weight amplify the faint halo (use only for A/B comparison). Ignored under --global / --no-per-point.",
        };
        var noSharpenOpt = new Option<bool>("--no-sharpen")
        {
            Description = "Skip wavelet sharpening. By default a mild a-trous sharpen (PlanetaryDefault, or --sharpen-gains) is applied to a separate master_*_sharpened.fits and to the PNG; the raw linear master is never sharpened.",
        };
        var sharpenGainsOpt = new Option<string?>("--sharpen-gains")
        {
            Description = "Override the wavelet per-scale gains as a comma list, finest scale first (e.g. '2,1.8,1.4,1.1,1'). Length sets the scale count. Default = PlanetaryDefault (5 scales). Ignored under --no-sharpen.",
        };
        var noPngOpt = new Option<bool>("--no-png")
        {
            Description = "Skip the stretched PNG preview (just the linear master FITS outputs).",
        };

        // Advanced alignment knobs (sensible defaults; only touch for tuning).
        var tileSizeOpt = new Option<int>("--align-tile")
        {
            Description = "Advanced: phase-correlation tile edge for global alignment. 0 = auto-size to the disk.",
        };
        var apSpacingOpt = new Option<int>("--ap-spacing")
        {
            Description = "Advanced: alignment-point grid cell spacing (px). Smaller = more APs.",
            DefaultValueFactory = _ => 24,
        };
        var maxApOpt = new Option<int>("--max-ap")
        {
            Description = "Advanced: maximum number of alignment points to track.",
            DefaultValueFactory = _ => 64,
        };
        var patchSizeOpt = new Option<int>("--ap-patch")
        {
            Description = "Advanced: power-of-two patch edge phase-correlated per alignment point.",
            DefaultValueFactory = _ => 32,
        };
        var meshSpacingOpt = new Option<float>("--mesh-spacing")
        {
            Description = "Advanced: displacement-mesh node spacing (px). Smaller = finer distortion correction.",
            DefaultValueFactory = _ => 24f,
        };

        var command = new Command("planetary-stack", "Stack a planetary SER video into a sharpened lucky-imaging master.")
        {
            Arguments = { serArg },
            Options =
            {
                outputOpt, labelOpt, keepOpt, qualityOpt, globalOpt, drizzleOpt, drizzlePixfracOpt,
                noPerPointOpt, noSignalGateOpt,
                noSharpenOpt, sharpenGainsOpt, noPngOpt,
                tileSizeOpt, apSpacingOpt, maxApOpt, patchSizeOpt, meshSpacingOpt,
            },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var serPath = parseResult.GetValue(serArg)!;
            if (!File.Exists(serPath))
            {
                consoleHost.WriteError($"SER file does not exist: {serPath}");
                return 1;
            }

            var keep = parseResult.GetValue(keepOpt);
            if (keep is <= 0 or > 1)
            {
                consoleHost.WriteError($"--keep must be in (0, 1]; got {keep}.");
                return 1;
            }

            var outputDir = parseResult.GetValue(outputOpt)
                ?? Path.GetDirectoryName(Path.GetFullPath(serPath))
                ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(outputDir);

            var metric = parseResult.GetValue(qualityOpt);
            var useGlobal = parseResult.GetValue(globalOpt);
            var drizzleScale = parseResult.GetValue(drizzleOpt);
            var useDrizzle = drizzleScale > 0f;

            // Parse the optional wavelet gains override before doing any heavy work so a typo fails fast.
            var sharpen = !parseResult.GetValue(noSharpenOpt);
            WaveletSharpenOptions? sharpenOptions = null;
            if (sharpen)
            {
                var gainsArg = parseResult.GetValue(sharpenGainsOpt);
                if (string.IsNullOrWhiteSpace(gainsArg))
                {
                    sharpenOptions = WaveletSharpenOptions.PlanetaryDefault;
                }
                else if (TryParseGains(gainsArg, out var gains))
                {
                    // Match PlanetaryDefault's grain control: soft-threshold the two finest scales so custom
                    // gains do not amplify limb / sensor noise.
                    var denoise = System.Collections.Immutable.ImmutableArray.CreateBuilder<float>(gains.Length);
                    for (var i = 0; i < gains.Length; i++)
                    {
                        denoise.Add(i == 0 ? 0.005f : i == 1 ? 0.0025f : 0f);
                    }

                    sharpenOptions = new WaveletSharpenOptions { Gains = gains, DenoiseThresholds = denoise.MoveToImmutable() };
                }
                else
                {
                    consoleHost.WriteError($"--sharpen-gains must be a comma list of floats (e.g. '2,1.8,1.4'); got '{gainsArg}'.");
                    return 1;
                }
            }

            var options = new PlanetaryStackOptions
            {
                KeepFraction = keep,
                QualityEstimator = metric == QualityMetric.Gradient
                    ? new GradientEnergyEstimator()
                    : new LaplacianEnergyEstimator(),
                AlignTileSize = parseResult.GetValue(tileSizeOpt),
                AlignmentPointSpacing = parseResult.GetValue(apSpacingOpt),
                MaxAlignmentPoints = parseResult.GetValue(maxApOpt),
                AlignmentPatchSize = RoundUpToPowerOfTwo(parseResult.GetValue(patchSizeOpt)),
                MeshNodeSpacing = parseResult.GetValue(meshSpacingOpt),
                PerPointQualityWeighting = !parseResult.GetValue(noPerPointOpt),
                PerPointSignalGate = !parseResult.GetValue(noSignalGateOpt),
                Drizzle = drizzleScale > 0f
                    ? new PlanetaryDrizzleOptions(drizzleScale, parseResult.GetValue(drizzlePixfracOpt))
                    : null,
                // The raw integrated master stays linear/unsharpened (downstream-friendly); the sharpen
                // pass is applied separately below so we can emit both the raw and sharpened masters.
            };

            var label = parseResult.GetValue(labelOpt);
            var prefix = string.IsNullOrWhiteSpace(label) ? "" : label.Trim() + "_";
            var baseName = Path.GetFileNameWithoutExtension(serPath);
            var sw = Stopwatch.StartNew();

            PlanetaryStackResult result;
            using (var stream = SerFrameStream.Open(serPath))
            {
                consoleHost.WriteScrollable(
                    $"[planetary] {baseName}: {stream.FrameCount} frames, {stream.Width}x{stream.Height}, layout {stream.Layout}");
                var mode = useDrizzle ? $"Bayer drizzle x{drizzleScale:0.0#}"
                    : useGlobal ? "global-translate"
                    : "alignment-point mesh";
                consoleHost.WriteScrollable(
                    $"[planetary] grading + {mode} stack, keeping best {keep:P0}...");

                var stacker = new LuckyImagingStacker();
                result = useDrizzle ? await stacker.StackDrizzleAsync(stream, options, ct)
                    : useGlobal ? await stacker.StackGlobalAsync(stream, options, ct)
                    : await stacker.StackAsync(stream, options, ct);
            }

            var master = result.Master;
            consoleHost.WriteScrollable(
                $"[planetary] {baseName}: stacked {result.FramesUsed}/{result.FramesGraded} frames " +
                $"(reference #{result.ReferenceIndex}) in {sw.Elapsed.TotalSeconds:F1}s");

            var masterFits = Path.Combine(outputDir, $"{prefix}master_{baseName}.fits");
            master.WriteToFitsFile(masterFits);
            consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(masterFits)} (linear master, {master.ChannelCount}ch {master.Width}x{master.Height})");

            // The display image is the sharpened master when sharpening is on, else the raw master.
            var display = master;
            if (sharpenOptions is { } so)
            {
                display = WaveletSharpen.Sharpen(master, so);
                var sharpenedFits = Path.Combine(outputDir, $"{prefix}master_{baseName}_sharpened.fits");
                display.WriteToFitsFile(sharpenedFits);
                consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(sharpenedFits)} (wavelet-sharpened, {so.ScaleCount} scales)");
            }

            if (!parseResult.GetValue(noPngOpt))
            {
                var pngPath = Path.Combine(outputDir, $"{prefix}master_{baseName}.png");
                try
                {
                    // No WCS / catalog: MasterPreviewRenderer skips SPCC and falls back to auto-stretch +
                    // sky-bg white balance, which is exactly right for a planet (no field stars to solve).
                    await previewRenderer.RenderAsync(
                        display,
                        display.ImageMeta,
                        wcs: null,
                        statsSource: display,
                        pngPath,
                        statsWcs: null,
                        ct: ct);
                    consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(pngPath)} (stretched preview)");
                }
                catch (Exception ex)
                {
                    consoleHost.WriteError($"[planetary] PNG render failed: {ex.Message}");
                }
            }

            consoleHost.WriteScrollable($"[planetary] done in {sw.Elapsed.TotalSeconds:F1}s -> {outputDir}");
            return 0;
        });

        return command;
    }

    // The AP matcher FFTs each patch, so the patch edge must be a power of two; round up rather than throw.
    private static int RoundUpToPowerOfTwo(int value)
    {
        if (value <= 1)
        {
            return 1;
        }

        var p = 1;
        while (p < value)
        {
            p <<= 1;
        }

        return p;
    }

    private static bool TryParseGains(string arg, out System.Collections.Immutable.ImmutableArray<float> gains)
    {
        var parts = arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            gains = default;
            return false;
        }

        var builder = System.Collections.Immutable.ImmutableArray.CreateBuilder<float>(parts.Length);
        foreach (var part in parts)
        {
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var g))
            {
                gains = default;
                return false;
            }

            builder.Add(g);
        }

        gains = builder.MoveToImmutable();
        return true;
    }
}
