using System;
using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Optics;
using TianWen.Lib.Imaging.Planetary;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen planetary-stack &lt;ser-file&gt;...</c> -- end-to-end planetary lucky-imaging stack of a SER video, or of a
/// run of them joined in time order (<see cref="PlanetaryFrameSequence"/>), each frame carried through the planet's rotation to
/// the run's middle first when the turn moves the disk's middle a pixel or more (docs/plans/planetary-restoration.md, R6 part 2):
/// grade the frames by sharpness, keep the best, align (global disk-COM + cross-correlation against a stack of the best frames,
/// then feature-driven alignment points + a per-AP displacement mesh), integrate with per-AP "best-of"
/// quality weighting, and optionally wavelet-sharpen. Every default is the measured best of R4 to R6 (the enhanced pipeline,
/// #1159); <c>--legacy</c> stacks as every master before it was. Wraps <see cref="LuckyImagingStacker"/>. Writes the
/// linear integrated master as FITS, an optional wavelet-sharpened master FITS, and a high-key planetary
/// PNG preview via <see cref="MasterPreviewRenderer.RenderPlanetaryAsync"/> (per-channel black point +
/// common-scale + gentle gamma -- the deep-sky MTF auto-stretch would blow a bright disk out to white;
/// no plate-solve / SPCC, a planet has no field stars).
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

    private enum Correlation
    {
        /// <summary>A plain cross-correlation, its peak climbed (R5's measured best on 8-bit frames).</summary>
        Plain,

        /// <summary>Phase correlation, every frequency weighted alike (every stack before the enhanced pipeline).</summary>
        Whitened,
    }

    // A stack left unsharpened keeps the best tenth: R4's raw optimum, 5 to 10 % of 3,000 frames, where each frame added blurs it.
    private const double UnsharpenedKeep = 0.1;

    private enum SharpenPreset
    {
        /// <summary>Boosts the finest band hardest (<see cref="WaveletSharpenOptions.PlanetaryDefault"/>).</summary>
        Default,

        /// <summary>Boosts the mid belt-structure band, holds the finest down (<see cref="WaveletSharpenOptions.Bandpass"/>).</summary>
        Bandpass,

        /// <summary>Boosts fine AND mid in one pass -- AutoStakkert-sharpen + bandpass (<see cref="WaveletSharpenOptions.Combo"/>).</summary>
        Combo,
    }

    public Command Build()
    {
        var serArg = new Argument<string[]>("ser-files")
        {
            Description = "The .ser planetary video to stack, or several of one run, joined in time order.",
            Arity = ArgumentArity.OneOrMore,
        };
        var derotateOpt = new Option<bool>("--derotate")
        {
            Description = "Carry every frame through the planet's rotation to the run's middle before it is stacked, however short the run (R6): over a run of minutes the belts move and the limb does not. Needs the frames' timestamps. The north is the one the run's first and last quarters agree on. Without it a run of Jupiter or Saturn is de-rotated when the planet's turn moves its disk's middle a pixel or more.",
        };
        var noDerotateOpt = new Option<bool>("--no-derotate")
        {
            Description = "Stack every frame as taken, however long the run.",
        };
        var planetOpt = new Option<string?>("--planet")
        {
            Description = "The planet whose rotation is taken out: jupiter or saturn. Read off the capture's file or folder name when not given.",
        };
        var legacyOpt = new Option<bool>("--legacy")
        {
            Description = "Stack as every master before the enhanced pipeline (#1159) was: the Laplacian keeping a quarter, phase correlation against the best frame, bilinear resampling, no de-rotation. An option given beside it still applies.",
        };
        var truthOpt = new Option<string?>("--truth")
        {
            Description = "A synthetic capture's truth (planetary-degrade's .truth.fits; a colour capture's .truth.r/.g/.b.fits beside it): every master written is scored against it, R3's band transfer and error and the limb's undershoot.",
        };
        var turnNorthOverOpt = new Option<bool>("--turn-north-over")
        {
            Description = "Under --derotate, turn the north the run agreed on over: the planet then turns backwards (a check, never a stack).",
        };

        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output directory for master_*.fits / *.png. Defaults to the SER file's directory.",
        };
        var labelOpt = new Option<string?>("--label")
        {
            Description = "Filename prefix for this run's outputs (e.g. 'k10_ap400'), so multiple experiments can share one output folder without colliding. Empty = no prefix.",
        };
        var keepOpt = new Option<double?>("--keep")
        {
            Description = "Fraction of frames to keep, sharpest first (lucky imaging). By default half when the master is sharpened (#1083: restoration divides the blur out, and every frame then lowers the noise) and a tenth under --no-sharpen (R4); a quarter under --legacy.",
        };
        var qualityOpt = new Option<QualityMetric?>("--quality")
        {
            Description = "Sharpness metric for grading + per-AP best-of: gradient (Sobel energy, the default; R4 ranks 8-bit frames by it at +0.87 against their true transfer) or laplacian (variance, +0.19; the --legacy metric).",
        };
        var globalOpt = new Option<bool>("--global")
        {
            Description = "Use whole-disk translation only (the cheap global align), skipping alignment points + the mesh warp. Faster, but no per-region distortion correction.",
        };
        var drizzleOpt = new Option<float>("--drizzle")
        {
            Description = "Bayer drizzle at this output scale (e.g. 1.5 = Drizzle1.5). Forward-scatters raw CFA samples onto an upscaled grid with NO interpolation/demosaic - recovers sub-Bayer resolution, and (by default) forward-scatters through the per-AP displacement mesh so it also gets the local seeing de-warp. 0 (default) = off (mesh/translate integrator). Bayer source only; see --drizzle-global to use whole-disk alignment instead.",
            DefaultValueFactory = _ => 0f,
        };
        var drizzlePixfracOpt = new Option<float>("--drizzle-pixfrac")
        {
            Description = "Drizzle drop size in (0, 1]. 1.0 (default) = full unit drop (robust coverage). Lower (0.6-0.8) is sharper but needs more frames. Ignored unless --drizzle > 0.",
            DefaultValueFactory = _ => 1.0f,
        };
        var drizzleGlobalOpt = new Option<bool>("--drizzle-global")
        {
            Description = "Drizzle with whole-disk global alignment only, skipping the per-AP displacement mesh. The mesh (on by default) forward-scatters each raw sample through the local seeing de-warp for sharper, more even detail; this flag is the cheaper whole-disk A/B baseline. Ignored unless --drizzle > 0.",
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
            Description = "Skip sharpening. By default the master is sharpened into a separate master_*_sharpened.fits and the PNG: by gains derived from the stack through the limb's edge when the telescope is given (--aperture-mm or --telescope, R8), else by PlanetaryDefault with the limb kept as stacked; the raw linear master is never sharpened.",
        };
        var sharpenPresetOpt = new Option<SharpenPreset?>("--sharpen-preset")
        {
            Description = "Sharpen by a fixed wavelet profile instead of the derived gains (an a-trous decomposition is a bank of frequency bands; the preset is the gain curve over them): 'default' boosts the finest band hardest; 'bandpass' boosts the mid belt-structure band and holds the finest down; 'combo' boosts fine AND mid in one pass. Ignored under --no-sharpen or when --sharpen-gains is given; --legacy sharpens by 'default'.",
        };
        var wavelengthOpt = new Option<string?>("--wavelength")
        {
            Description = "The filter's effective wavelength, nm, for the derived sharpening's diffraction: one for a mono capture (550, a broadband luminance, when not given), a comma list for a colour one's channels (610, 530, 460 when not given).",
        };
        var fixOpt = new Option<PlanetaryLimbFix?>("--limb-fix")
        {
            Description = "How the derived sharpening keeps the limb from ringing: plain, floored, bounded, limbchannel or feathered (the measured choice when not given).",
        };
        var pupil = PlanetaryMasterScore.PupilOptions();
        var sharpenGainsOpt = new Option<string?>("--sharpen-gains")
        {
            Description = "Override the wavelet per-scale gains as a comma list, finest scale first (e.g. '2,1.8,1.4,1.1,1'). Length sets the scale count. Takes precedence over --sharpen-preset. Ignored under --no-sharpen.",
        };
        var noPngOpt = new Option<bool>("--no-png")
        {
            Description = "Skip the stretched PNG preview (just the linear master FITS outputs).",
        };
        var pngGammaOpt = new Option<double>("--png-gamma")
        {
            Description = "Midtones gamma for the high-key planetary PNG preview. 1.0 = pure linear (planets need little stretching); lower lifts the belts. Default 0.75. Ignored under --no-png.",
            DefaultValueFactory = _ => 0.75,
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
        var correlationOpt = new Option<Correlation?>("--correlation")
        {
            Description = "How frames and points are registered: plain (a cross-correlation, its peak climbed; the default, R5: 3 times better placed on a single 8-bit frame) or whitened (phase correlation, the --legacy registration).",
        };
        var interpolationOpt = new Option<WarpInterpolation?>("--interpolation")
        {
            Description = "The kernel each frame is resampled by as it is stacked: lanczos3clamped (the default; R5 part 3: band 1's error 0.014 to 0.024 lower), lanczos3 or bilinear (the --legacy kernel). Not drizzle, which scatters.",
        };
        var referenceFramesOpt = new Option<int?>("--reference-frames")
        {
            Description = "Register against a stack of this many of the best frames (1,000 by default, as AutoStakkert does; R5 part 3), or 0 for the best frame alone (--legacy).",
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
                outputOpt, labelOpt, keepOpt, qualityOpt, globalOpt, drizzleOpt, drizzlePixfracOpt, drizzleGlobalOpt,
                noPerPointOpt, noSignalGateOpt,
                noSharpenOpt, sharpenPresetOpt, sharpenGainsOpt, wavelengthOpt, fixOpt, pupil.ApertureMm, pupil.Obstruction, pupil.Telescope, noPngOpt, pngGammaOpt,
                tileSizeOpt, apSpacingOpt, maxApOpt, patchSizeOpt, meshSpacingOpt, correlationOpt, interpolationOpt, referenceFramesOpt,
                derotateOpt, noDerotateOpt, planetOpt, turnNorthOverOpt, legacyOpt, truthOpt,
            },
        };

        command.SetAction(async (parseResult, ct) =>
        {
            var serPaths = parseResult.Required(serArg);
            if (serPaths.FirstOrDefault(path => !File.Exists(path)) is { } missing)
            {
                consoleHost.WriteError($"SER file does not exist: {missing}");
                return 1;
            }
            var serPath = serPaths[0];

            var legacy = parseResult.GetValue(legacyOpt);
            var baseline = legacy ? PlanetaryStackOptions.Legacy : new PlanetaryStackOptions();
            var keep = parseResult.GetValue(keepOpt)
                ?? (!legacy && parseResult.GetValue(noSharpenOpt) ? UnsharpenedKeep : baseline.KeepFraction);
            if (keep is <= 0 or > 1)
            {
                consoleHost.WriteError($"--keep must be in (0, 1]; got {keep}.");
                return 1;
            }
            var derotate = parseResult.GetValue(derotateOpt);
            if (derotate && parseResult.GetValue(noDerotateOpt))
            {
                consoleHost.WriteError("--derotate and --no-derotate ask for opposite things.");
                return 1;
            }
            var planetName = parseResult.GetValue(planetOpt)?.ToLowerInvariant();
            CatalogIndex? planet = planetName switch
            {
                null => PlanetaryCaptureName.Planet(serPaths[0]),
                "jupiter" => CatalogIndex.Jupiter,
                "saturn" => CatalogIndex.Saturn,
                _ => null,
            };
            if (planetName is not null && planet is null)
            {
                consoleHost.WriteError($"--planet {planetName}: jupiter or saturn.");
                return 1;
            }
            if (derotate && (planet is not { } named || !PhysicalEphemeris.Supports(named)))
            {
                consoleHost.WriteError("--derotate: name the planet to de-rotate (--planet jupiter or saturn); the capture's name does not say Jupiter or Saturn.");
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

            // Parse the optional wavelet gains override before doing any heavy work so a typo fails fast. A fixed profile (a preset, the
            // gains given, or --legacy) sharpens as every master before the enhanced pipeline; otherwise the sharpening is derived.
            var sharpen = !parseResult.GetValue(noSharpenOpt);
            WaveletSharpenOptions? sharpenOptions = null;
            if (sharpen)
            {
                var gainsArg = parseResult.GetValue(sharpenGainsOpt);
                if (string.IsNullOrWhiteSpace(gainsArg))
                {
                    sharpenOptions = parseResult.GetValue(sharpenPresetOpt) switch
                    {
                        SharpenPreset.Bandpass => WaveletSharpenOptions.Bandpass,
                        SharpenPreset.Combo => WaveletSharpenOptions.Combo,
                        SharpenPreset.Default => WaveletSharpenOptions.PlanetaryDefault,
                        _ => legacy ? WaveletSharpenOptions.PlanetaryDefault : null,
                    };
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

            var wavelengthText = parseResult.GetValue(wavelengthOpt);
            var telescope = PlanetaryMasterScore.PupilFrom(parseResult, pupil);

            // Every choice the options leave open is the baseline's: the pipeline's defaults, or the legacy recipe.
            var options = baseline with
            {
                KeepFraction = keep,
                QualityEstimator = metric switch
                {
                    QualityMetric.Gradient => new GradientEnergyEstimator(),
                    QualityMetric.Laplacian => new LaplacianEnergyEstimator(),
                    _ => baseline.QualityEstimator,
                },
                AlignTileSize = parseResult.GetValue(tileSizeOpt),
                AlignmentPointSpacing = parseResult.GetValue(apSpacingOpt),
                MaxAlignmentPoints = parseResult.GetValue(maxApOpt),
                AlignmentPatchSize = RoundUpToPowerOfTwo(parseResult.GetValue(patchSizeOpt)),
                MeshNodeSpacing = parseResult.GetValue(meshSpacingOpt),
                WhitenedCorrelation = parseResult.GetValue(correlationOpt) is { } correlation ? correlation == Correlation.Whitened : baseline.WhitenedCorrelation,
                Interpolation = parseResult.GetValue(interpolationOpt) ?? baseline.Interpolation,
                ReferenceFrames = parseResult.GetValue(referenceFramesOpt) ?? baseline.ReferenceFrames,
                PerPointQualityWeighting = !parseResult.GetValue(noPerPointOpt),
                PerPointSignalGate = !parseResult.GetValue(noSignalGateOpt),
                Drizzle = drizzleScale > 0f
                    ? new PlanetaryDrizzleOptions(drizzleScale, parseResult.GetValue(drizzlePixfracOpt),
                        AlignmentPointMesh: !parseResult.GetValue(drizzleGlobalOpt))
                    : null,
                // Asked for, every run is de-rotated; otherwise a run of a planet with a rotation model is, once its turn moves the
                // disk's middle a pixel (the stacker measures it), and never under --legacy or --no-derotate.
                Derotation = (derotate || !(legacy || parseResult.GetValue(noDerotateOpt))) && PlanetaryBestStack.DerotationFor(planet, always: derotate) is { } rotation
                    ? rotation with { TurnNorthOver = parseResult.GetValue(turnNorthOverOpt) }
                    : null,
                // The raw integrated master stays linear/unsharpened (downstream-friendly); the sharpen
                // pass is applied separately below so we can emit both the raw and sharpened masters.
            };

            var label = parseResult.GetValue(labelOpt);
            var prefix = string.IsNullOrWhiteSpace(label) ? "" : label.Trim() + "_";
            // A run is named by its first capture and how many follow it.
            var baseName = Path.GetFileNameWithoutExtension(serPaths.Order(StringComparer.OrdinalIgnoreCase).First())
                + (serPaths.Length > 1 ? $"+{serPaths.Length - 1}" : "");
            var sw = Stopwatch.StartNew();

            PlanetaryStackResult result;
            using (IPlanetaryFrameStream stream = serPaths.Length == 1 ? SerFrameStream.Open(serPath) : PlanetaryFrameSequence.OpenSer(serPaths))
            {
                consoleHost.WriteScrollable(
                    $"[planetary] {baseName}: {stream.FrameCount} frames{(serPaths.Length > 1 ? $" of {serPaths.Length} captures" : "")}, {stream.Width}x{stream.Height}, layout {stream.Layout}");
                var mode = useDrizzle ? $"Bayer drizzle x{drizzleScale:0.0#}"
                    : useGlobal ? "global-translate"
                    : "alignment-point mesh";
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[planetary] grading + {mode} stack{(legacy ? " (legacy)" : "")}, keeping the best {keep:P0} by {Describe(options.QualityEstimator)}, {(options.WhitenedCorrelation ? "phase" : "plain")} correlation against {(options.ReferenceFrames > 1 ? $"a stack of the best {options.ReferenceFrames}" : "the best frame")}, {options.Interpolation} resampling..."));

                var stacker = new LuckyImagingStacker();
                result = useDrizzle ? await stacker.StackDrizzleAsync(stream, options, ct)
                    : useGlobal ? await stacker.StackGlobalAsync(stream, options, ct)
                    : await stacker.StackAsync(stream, options, ct);
            }

            var master = result.Master;
            consoleHost.WriteScrollable(
                $"[planetary] {baseName}: stacked {result.FramesUsed}/{result.FramesGraded} frames " +
                $"(reference #{result.ReferenceIndex}) in {sw.Elapsed.TotalSeconds:F1}s");
            if (result.Epoch is null && result.TurnPx is { } turn)
            {
                consoleHost.WriteScrollable(double.IsNaN(turn)
                    ? "[planetary] stacked as taken: the frames carry no times to de-rotate by"
                    : string.Create(CultureInfo.InvariantCulture, $"[planetary] stacked as taken: the planet's turn moves its disk's middle {turn:0.00} px over the run, under the {PlanetaryBestStack.TurnWorthDerotatingPx:0.#} px a de-rotation is worth"));
            }
            if (result.Epoch is { } epoch && result.North is { } north)
            {
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[planetary] every frame carried to {epoch:yyyy-MM-dd HH:mm:ss.f} UTC, north at {north.NorthAngleDeg:0.0} deg (the run's quarters {north.AgreementAsFitted:0.00000} apart with the limb fit's north, {north.AgreementTurnedOver:0.00000} turned over)"));
            }

            var masterFits = Path.Combine(outputDir, $"{prefix}master_{baseName}.fits");
            master.WriteToFitsFile(masterFits);
            consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(masterFits)} (linear master, {master.ChannelCount}ch {master.Width}x{master.Height})");
            var truthPath = parseResult.GetValue(truthOpt);
            if (truthPath is not null)
            {
                PlanetaryMasterScore.AgainstTruth(consoleHost, master, truthPath, planet ?? CatalogIndex.Jupiter, "the stack");
            }

            // The display image is the sharpened master when sharpening is on, else the raw master.
            var display = master;
            if (sharpen)
            {
                var (sharpened, how) = Sharpened(master, sharpenOptions, planet, result.Epoch, wavelengthText, telescope, parseResult.GetValue(fixOpt));
                display = sharpened;
                var sharpenedFits = Path.Combine(outputDir, $"{prefix}master_{baseName}_sharpened.fits");
                display.WriteToFitsFile(sharpenedFits);
                consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(sharpenedFits)} ({how})");
                if (truthPath is not null)
                {
                    PlanetaryMasterScore.AgainstTruth(consoleHost, display, truthPath, planet ?? CatalogIndex.Jupiter, "the sharpened master");
                }
            }

            if (!parseResult.GetValue(noPngOpt))
            {
                var pngPath = Path.Combine(outputDir, $"{prefix}master_{baseName}.png");
                try
                {
                    // Planets are a bright disk on a dark sky: use the high-key PLANETARY stretch
                    // (per-channel black point + common scale + gentle gamma), NOT the deep-sky MTF
                    // auto-stretch, which targets a faint background and blows the disk out to a white blob.
                    await previewRenderer.RenderPlanetaryAsync(
                        display,
                        pngPath,
                        gamma: parseResult.GetValue(pngGammaOpt),
                        ct: ct);
                    consoleHost.WriteScrollable($"[planetary] wrote {Path.GetFileName(pngPath)} (high-key planetary preview)");
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

    private static string Describe(IFrameQualityEstimator estimator) => estimator switch
    {
        GradientEnergyEstimator => "the gradient",
        LaplacianEnergyEstimator => "the Laplacian",
        _ => estimator.GetType().Name,
    };

    // The master sharpened: by a fixed profile when one was asked for (a preset, gains, or --legacy), else as the pipeline sharpens it
    // (PlanetaryBestStack.Sharpen, the one routine the GUI's best stack runs too).
    private (Image Sharpened, string How) Sharpened(Image master, WaveletSharpenOptions? fixedProfile, CatalogIndex? planet, DateTimeOffset? epoch,
        string? wavelengthText, Pupil? telescope, PlanetaryLimbFix? fix)
    {
        if (fixedProfile is { } profile)
        {
            return (WaveletSharpen.Sharpen(master, profile), $"wavelet-sharpened, {profile.ScaleCount} scales");
        }
        System.Collections.Immutable.ImmutableArray<double> wavelengths = wavelengthText is null ? []
            : [.. PlanetaryMasterScore.Wavelengths(consoleHost, wavelengthText) ?? [550]];
        return PlanetaryBestStack.Sharpen(master, planet, epoch, telescope, wavelengths, fix);
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
