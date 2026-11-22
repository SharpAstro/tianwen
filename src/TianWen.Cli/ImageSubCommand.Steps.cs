using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.StarRemoval;
using TianWen.Lib.Imaging.Workbench;

namespace TianWen.Cli;

/// <summary>
/// The steps of a mono colour composition, one verb each, PixInsight's way: run them one at a time and look at every
/// result, or run <c>image combine</c>, which runs the same routines in the same order (<see cref="ColourComposition"/>).
/// </summary>
/// <remarks>
/// <para><b>A step writes its result on its input's scale.</b> Nothing is divided by its own peak on the way out, so two
/// files a step wrote still relate as their inputs did: a red and an H-alpha master through the star remover keep the
/// continuum scale measured between them. A step handed several masters puts them through its enhancer on ONE scale, as
/// the recipe does, so the verbs run one by one give the recipe's image (<c>ColourCompositionTests</c>).</para>
/// <para><b>What a later step needs travels in the file.</b> A linear fit's slope is <c>FLUXSCAL</c>
/// (<see cref="ImageMeta.FluxScale"/>), which <c>image add-line</c> reads; the LRGB scale is measured from the planes it is
/// applied to. The continuum scale is the one number carried by hand: <c>image continuum --dry-run</c> on the masters
/// WITH their stars says it, and <c>image add-line --scale</c> takes it.</para>
/// </remarks>
internal sealed partial class ImageSubCommand
{
    // -------- tianwen image linear-fit ---------------------------------

    private Command BuildLinearFitCommand()
    {
        var referenceArg = new Argument<string>("reference") { Description = "The master the others are put on the scale of (green, as a rule)." };
        var targetsArg = new Argument<string[]>("masters") { Description = "The masters to fit onto it.", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS, with one master only. Default: <master>_linearfit.fits." };
        var rejectHighOpt = new Option<double>("--reject-high")
        {
            Description = "Pixels at or above this fraction of either image's peak carry no weight (PixInsight's default).",
            DefaultValueFactory = _ => LinearFit.DefaultRejectHigh,
        };
        var cmd = new Command("linear-fit", "Put mono masters on one master's scale (PixInsight's LinearFit): a least-absolute-deviation "
            + "line over the pixels both hold below --reject-high of their peaks. The slope goes into FLUXSCAL, which image add-line reads.")
        {
            Arguments = { referenceArg, targetsArg },
            Options = { outputOpt, rejectHighOpt },
        };
        // The fit itself is the operation an edit list runs (docs/plans/stacking-workbench.md, W1), so this verb and a
        // project's step are one routine. It fits one master onto the reference, so several are several runs of it.
        var linearFit = new LinearFitOperation();
        cmd.SetAction(async (parseResult, ct) =>
        {
            var reference = parseResult.Required(referenceArg);
            var targets = parseResult.Required(targetsArg);
            var settings = new LinearFitSettings { RejectHigh = parseResult.GetValue(rejectHighOpt) };
            foreach (var target in targets)
            {
                if (await RunStepAsync("linear-fit", linearFit, settings, [target, reference],
                        StepOutput(target, "_linearfit", parseResult.GetValue(outputOpt), targets.Length), ct) != 0)
                {
                    return 1;
                }
            }
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image match-psf ----------------------------------

    private Command BuildMatchPsfCommand()
    {
        var mastersArg = new Argument<string[]>("masters") { Description = "Masters on one grid.", Arity = ArgumentArity.OneOrMore };
        var cmd = new Command("match-psf", "Blur every sharper master to the widest one's star width (PsfMatch), the fallback where no "
            + "deblurrer serves; writes <master>_psf.fits. Use the result for the colour and the measurements, never for a luminance.")
        {
            Arguments = { mastersArg },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var paths = parseResult.Required(mastersArg);
            if (!TryReadMasters(paths, out var masters, out var wcs) || !OneGrid(masters))
            {
                return 1;
            }
            var (matched, report, target) = await PsfMatch.ToWidestAsync(masters, ct);
            for (var i = 0; i < matched.Length; i++)
            {
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[match-psf] {Path.GetFileName(paths[i])}: FWHM {report[i].FwhmBefore:F2} px, blurred by sigma {report[i].Sigma:F2} to {report[i].FwhmAfter:F2} (target {target:F2})"));
                WriteStep(matched[i], StepOutput(paths[i], "_psf", null, paths.Length), wcs, "match-psf");
            }
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image deblur -------------------------------------

    private Command BuildDeblurCommand()
    {
        var mastersArg = new Argument<string[]>("masters") { Description = "FITS images to deblur; several go through on one scale.", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS, with one input only. Default: <input>_deblur.fits." };
        var headroomOpt = new Option<float>("--headroom")
        {
            Description = "The brightest input pixel goes to the deblurrer at 1/headroom of its ceiling, so no sharpened star clips.",
            DefaultValueFactory = _ => NarrowbandCombination.DefaultDeblurHeadroom,
        };
        var cmd = new Command("deblur",
            "Whole-frame deconvolution (RC-Astro BlurXTerminator) and nothing else, run before the stars come out. Linear in, linear "
            + "out, each result on its input's scale; several masters go through on one scale with --headroom below the ceiling.")
        {
            Arguments = { mastersArg },
            Options = { outputOpt, headroomOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!IEnhancerAvailability.Serves(deblurrer, 1, EnhanceOptions.Default))
            {
                consoleHost.WriteError("[deblur] no deblurrer serves here. It comes from RC-Astro: install the rc-astro CLI and license BlurXTerminator, or set RC_ASTRO_CLI.");
                return 3;
            }
            var paths = parseResult.Required(mastersArg);
            if (!TryReadMasters(paths, out var masters, out var wcs) || !OneGrid(masters))
            {
                return 1;
            }
            var (deblurred, atCeiling) = await NarrowbandCombination.DeblurAsync(masters, deblurrer, Math.Max(1f, parseResult.GetValue(headroomOpt)), ct);
            for (var i = 0; i < deblurred.Length; i++)
            {
                var before = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(masters[i], ct));
                var after = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(deblurred[i], ct));
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[deblur] {Path.GetFileName(paths[i])}: FWHM {before:F2} px -> {after:F2} ({deblurrer.Name})"));
                WriteStep(deblurred[i], StepOutput(paths[i], "_deblur", parseResult.GetValue(outputOpt), paths.Length), wcs, "deblur");
            }
            if (atCeiling > 0)
            {
                consoleHost.WriteScrollable($"[deblur] {atCeiling} pixels reached the deblurrer's ceiling and are clipped: raise --headroom.");
            }
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image remove-stars -------------------------------

    private Command BuildRemoveStarsCommand()
    {
        var mastersArg = new Argument<string[]>("masters") { Description = "FITS images to split; several go through on one scale.", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "The starless FITS, with one input only. Default: <input>_starless.fits." };
        var noStarsOpt = new Option<bool>("--no-stars") { Description = "Write the starless plate only, not the stars (the input less its starless plate, written beside the starless output: <input>_stars.fits by default, <name>_stars.fits beside an -o of <name>_starless.fits or <name>.fits)." };
        var modelOpt = new Option<string[]>("--model")
        {
            Description = "TianWen's own star-remover weights instead of the registered remover: ONNX files of the R2 line, each with its "
                + "contract beside it, averaged together (docs/plans/star-remover-training.md). None ship; this is the path to try them.",
            AllowMultipleArgumentsPerToken = true,
        };
        var viewsOpt = new Option<int>("--views")
        {
            Description = "With --model: how many of a tile's eight flips and quarter turns each model sees, turned back and averaged (1, 2, 4 or 8).",
            DefaultValueFactory = _ => 8,
        };
        var monoAsRgbOpt = new Option<bool>("--mono-as-rgb") { Description = "With --model: put a mono master in all three channels (the models are one-shot colour and none was measured on mono)." };
        var noProtectCoresOpt = new Option<bool>("--no-protect-cores")
        {
            Description = "Do not protect cores. By default the brightest part of each galaxy or nebula is kept out of the stars, whichever "
                + "remover runs: a nucleus like M31's or a bright knot a remover takes as a star stays in the starless plate (#1658). On a "
                + "plate-solved master the catalogued stars inside it are still removed (#2242, see --core-catalogue); on an unsolved "
                + "one they stay with the core. Give this, with --keep-overshoot, to read the remover's own behaviour.",
        };
        var coreCatalogueOpt = CoreCatalogueOption();
        var protectCoresOpt = new Option<bool>("--protect-cores")
        {
            Description = "Accepted and ignored: core protection is the default (see --no-protect-cores).",
        };
        var noFillDigsOpt = new Option<bool>("--no-fill-digs")
        {
            Description = "With --model: do not fill the digs. By default the holes TianWen's own models dig under the stars they take are "
                + "filled to the sky around them, and the little light they leave is lowered a little (#2204; image fill-digs is the step "
                + "alone). RC-Astro's plate is never touched. Give this, with --keep-overshoot, to read the models' own output.",
        };
        var keepOvershootOpt = new Option<bool>("--keep-overshoot")
        {
            Description = "Leave the plate as the remover wrote it. By default it is held at or under its input, so the stars layer is "
                + "never negative and plate plus stars is the input (#2433). Give this, with --no-protect-cores and --no-fill-digs, "
                + "to read the remover's own overshoot: a score of the light a plate added over its input needs it.",
        };
        var blackPointSigmaOpt = new Option<double?>(BlackPointOption.Name)
        {
            Description = "With --model, for measurement only: stretch the frame with each channel's black point this many sky-noise "
                + "sigma under its median, whatever the models' contracts say (#2309). A model is otherwise stretched as its "
                + "contract states it was trained: at that depth, or from each channel's minimum where the contract is silent. "
                + "This runs a model outside its contract, and the log says so.",
        };
        var cmd = new Command("remove-stars", "Star removal (RC-Astro StarXTerminator, or TianWen's own weights named with --model): the starless "
            + "plate and the stars it took out, each on its input's scale, so image add-stars puts them back exactly. Several masters "
            + "go through on one scale, so a scale measured between two of them still holds between their plates.")
        {
            Arguments = { mastersArg },
            Options = { outputOpt, noStarsOpt, modelOpt, viewsOpt, monoAsRgbOpt, noProtectCoresOpt, protectCoresOpt, coreCatalogueOpt, noFillDigsOpt, keepOvershootOpt, blackPointSigmaOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var paths = parseResult.Required(mastersArg);
            var noProtect = parseResult.GetValue(noProtectCoresOpt);
            if (!TryResolveCoreProtection(noProtect, parseResult.GetValue(protectCoresOpt), out var coreOptions))
            {
                consoleHost.WriteScrollable("[remove-stars] --protect-cores and --no-protect-cores contradict each other; give one");
                return 1;
            }
            var blackPointSigma = parseResult.GetValue(blackPointSigmaOpt);
            if (BlackPointOption.Refusal(blackPointSigma, applies: parseResult.GetValue(modelOpt) is { Length: > 0 },
                    needs: "--black-point-sigma sets the stretch of TianWen's own models; it needs --model") is { } blackPointRefusal)
            {
                consoleHost.WriteScrollable($"[remove-stars] {blackPointRefusal}");
                return 1;
            }
            if (!TryReadMasters(paths, out var masters, out var wcs) || !OneGrid(masters))
            {
                return 1;
            }
            // Weights named on the command line are a remover of this run's own, and closed with it; otherwise the registered one.
            using var own = parseResult.GetValue(modelOpt) is { Length: > 0 } models
                ? OnnxTianWenStarRemover.Open(models, parseResult.GetValue(viewsOpt), parseResult.GetValue(monoAsRgbOpt), logger, blackPointSigma)
                : null;
            IStarRemover remover = own ?? starRemover;
            consoleHost.WriteScrollable($"[remove-stars] {paths.Length} master(s) through {remover.Name}");
            var catalogStars = coreOptions is null
                ? null
                : await CoreCatalogueAsync("remove-stars", parseResult.GetValue(coreCatalogueOpt), wcs, masters[0].Width, masters[0].Height, ct);
            var (starless, stars, cores, filled) = await NarrowbandCombination.SplitStarsAsync(
                masters, remover, coreOptions, fillDigs: !parseResult.GetValue(noFillDigsOpt), catalogStars,
                holdAtOrUnderPlane: !parseResult.GetValue(keepOvershootOpt), ct);
            for (var i = 0; i < starless.Length; i++)
            {
                if (filled is not null)
                {
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"[remove-stars] {Path.GetFileName(paths[i])}: dig-fill step covered {filled[i]:P1} of the pixels"));
                }
                if (cores?[i] is { } mask)
                {
                    SayCores("remove-stars", paths[i], mask);
                }
                var dst = StepOutput(paths[i], "_starless", parseResult.GetValue(outputOpt), paths.Length);
                WriteStep(starless[i], dst, wcs, "remove-stars");
                if (!parseResult.GetValue(noStarsOpt))
                {
                    WriteStep(stars[i], StarsOutput(dst), wcs, "remove-stars");
                }
            }
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image protect-cores ------------------------------

    private Command BuildProtectCoresCommand()
    {
        var inputOpt = new Option<string>("--input") { Description = "The master the stars were removed from.", Required = true };
        var starlessOpt = new Option<string>("--starless") { Description = "Its starless plate as the remover left it (image remove-stars --no-protect-cores --keep-overshoot).", Required = true };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS. Default: <starless>_cores.fits." };
        var catalogueOpt = CoreCatalogueOption();
        var cmd = new Command("protect-cores", "The step image remove-stars runs after its remover, alone (ExtendedCoreMask, #1658): the brightest "
            + "part of each galaxy or nebula takes the input back, so a nucleus or knot the remover took as a star is in the starless plate "
            + "again. On a plate-solved master the catalogued stars inside a core stay removed (#2242). No remover runs.")
        {
            Options = { inputOpt, starlessOpt, outputOpt, catalogueOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var starlessPath = parseResult.Required(starlessOpt);
            // The plate is read as written (its absent pixels are NaN, and an exact zero in it is the remover's value).
            if (!TryReadMasters([parseResult.Required(inputOpt)], out var input, out var wcs)
                || !TryReadMasters([starlessPath], out var starless, out _, maskAbsent: false)
                || !OneGrid([input[0], starless[0]]))
            {
                return 1;
            }
            var catalogStars = await CoreCatalogueAsync("protect-cores", parseResult.GetValue(catalogueOpt), wcs, input[0].Width, input[0].Height, ct);
            var mask = ExtendedCoreMask.Find(input[0], ExtendedCoreMask.Options.Default, catalogStars);
            SayCores("protect-cores", starlessPath, mask);
            WriteStep(mask.Apply(input[0], starless[0]), StepOutput(starlessPath, "_cores", parseResult.GetValue(outputOpt), 1), wcs, "protect-cores");
            return 0;
        });
        return cmd;
    }

    // Which star catalogue core protection asks: the installed ASTAP database and, without one, the embedded Tycho-2
    // (auto); the embedded one alone; or none, and every star inside a core then stays with it.
    internal static Option<string> CoreCatalogueOption()
    {
        var option = new Option<string>("--core-catalogue")
        {
            Description = "The star catalogue asked for the stars inside a protected core, which then stay removed (a plate-solved master only): "
                + "auto (the star database ASTAP installs, else the embedded Tycho-2), embedded (Tycho-2 alone), or none (every star inside a "
                + "core stays in the starless plate with it).",
            DefaultValueFactory = _ => "auto",
        };
        option.AcceptOnlyFromAmong("auto", "embedded", "none");
        return option;
    }

    // The catalogued stars of a solved frame, for core protection; null with one line saying why where there are none.
    private async Task<IReadOnlyList<FrameStar>?> CoreCatalogueAsync(string verb, string? choice, WCS? wcs, int width, int height, CancellationToken ct)
    {
        if (choice == "none")
        {
            return null;
        }
        // A header's pointing, or the matrix made up from its pixel scale and angle, is read as a WCS too and is no solution.
        if (wcs is not { } solution || !CatalogFrameStars.IsSolved(solution))
        {
            consoleHost.WriteScrollable($"[{verb}] the master carries no plate solution, so stars inside a protected core stay with it "
                + "(tianwen solve writes one)");
            return null;
        }
        if (choice == "embedded" && objectDb is null)
        {
            // Only where this command was built without the catalogue; the program always hands it one.
            consoleHost.WriteScrollable($"[{verb}] the embedded catalogue is not at hand here, so stars inside a protected core stay with it");
            return null;
        }
        var installed = choice == "embedded" ? null : TianWen.Lib.Astrometry.Catalogs.AstapStarDatabase.FindInstalled();
        var (stars, why) = await CatalogFrameStars.ReadAsync(solution, width, height, installed, objectDb, ct);
        if (stars is null)
        {
            consoleHost.WriteScrollable($"[{verb}] no catalogued stars ({why}), so stars inside a protected core stay with it");
            return null;
        }
        consoleHost.WriteScrollable($"[{verb}] catalogued stars from {stars.Source}");
        return stars.Stars;
    }

    private void SayCores(string verb, string path, ExtendedCoreMask mask)
    {
        var name = Path.GetFileName(path);
        if (mask.Cores.Length == 0)
        {
            consoleHost.WriteScrollable($"[{verb}] {name}: no extended source bright enough to protect");
            return;
        }
        consoleHost.WriteScrollable($"[{verb}] {name}: protected " + string.Join("; ", mask.Cores.Select(c =>
            string.Create(CultureInfo.InvariantCulture, $"({c.X}, {c.Y}), peak {c.PeakSigma:F0} sigma, {c.CoreAreaPx} px of {c.SourceAreaPx}"))));
        if (mask.StarsLeftOut is { } found)
        {
            consoleHost.WriteScrollable(found.CalibrationStars == 0
                ? $"[{verb}] {name}: too few catalogued stars show on the frame to read a zero point, so the stars inside the cores stay with them"
                : string.Create(CultureInfo.InvariantCulture,
                    $"[{verb}] {name}: {found.Stars} of {found.Peaks} compact peaks inside the cores are catalogued stars and stay removed "
                    + $"(zero point {found.ZeroPoint:F2} over {found.CalibrationStars} stars, scatter {found.ZeroPointScatter:F2} mag; "
                    + $"a star of magnitude 10 reaches {found.ReachAtMagnitude10:F0} px)"));
        }
    }

    // -------- tianwen image fill-digs ----------------------------------

    private Command BuildFillDigsCommand()
    {
        var inputOpt = new Option<string>("--input") { Description = "The master the stars were removed from.", Required = true };
        var starlessOpt = new Option<string>("--starless") { Description = "Its starless plate as the models left it (image remove-stars --model ... --no-fill-digs --keep-overshoot).", Required = true };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS. Default: <starless>_filled.fits." };
        var keepOvershootOpt = new Option<bool>("--keep-overshoot")
        {
            Description = "Leave the filled plate where it stands over the input. By default it is then held at or under the input, as "
                + "image remove-stars holds it (#2433), so this step on the plate remove-stars --no-protect-cores --no-fill-digs "
                + "--keep-overshoot wrote gives what remove-stars --no-protect-cores writes.",
        };
        var cmd = new Command("fill-digs", "The step image remove-stars runs after TianWen's own models, alone (DigFill, #2204): where a star was "
            + "taken, a hole dug under it is filled to the sky around it and light left standing is lowered a little, each bounded by "
            + "what the model took there; then the plate is held at or under the input, as remove-stars does last. No model runs. "
            + "Not for an RC-Astro plate, which has no digs to fill.")
        {
            Options = { inputOpt, starlessOpt, outputOpt, keepOvershootOpt },
        };
        var fillDigs = new FillDigsOperation();
        cmd.SetAction(async (parseResult, ct) =>
        {
            var starlessPath = parseResult.Required(starlessOpt);
            // The plate is read as written there: remove-stars wrote its absent pixels as NaN, and an exact zero in it
            // is a value the model gave, not the absent ring a master marks with zero.
            return await RunStepAsync("fill-digs", fillDigs, new FillDigsSettings { KeepOvershoot = parseResult.GetValue(keepOvershootOpt) },
                [starlessPath, parseResult.Required(inputOpt)],
                StepOutput(starlessPath, "_filled", parseResult.GetValue(outputOpt), 1), ct);
        });
        return cmd;
    }

    // -------- tianwen image denoise ------------------------------------

    private Command BuildDenoiseCommand()
    {
        var mastersArg = new Argument<string[]>("masters") { Description = "FITS images to denoise; several go through on one scale.", Arity = ArgumentArity.OneOrMore };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS, with one input only. Default: <input>_denoise.fits." };
        var colourOpt = new Option<bool>("--colour") { Description = "Three mono planes (red, green, blue) denoised together as ONE colour image, so the denoiser sees their colour." };
        var strengthOpt = new Option<float?>("--strength") { Description = "Strength in [0, 1]. Default: the denoiser's own (NoiseXTerminator's is set from each plate's noise)." };
        var cmd = new Command("denoise",
            "Noise reduction (RC-Astro NoiseXTerminator where licensed, else the in-house N2N denoiser, which takes colour only) and "
            + "nothing else, each result on its input's scale. Hand it STARLESS planes (image remove-stars): the stars are never "
            + "denoised, as the split program inside image sharpen never does.")
        {
            Arguments = { mastersArg },
            Options = { outputOpt, colourOpt, strengthOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var paths = parseResult.Required(mastersArg);
            if (!TryReadMasters(paths, out var masters, out var wcs) || !OneGrid(masters))
            {
                return 1;
            }
            var options = parseResult.GetValue(strengthOpt) is { } strength
                ? new EnhanceOptions(Tuning: new EnhanceTuning(DenoiseStrength: Math.Clamp(strength, 0f, 1f)))
                : EnhanceOptions.Default;
            var colour = parseResult.GetValue(colourOpt);
            if (colour && masters.Count != 3)
            {
                consoleHost.WriteError("[denoise] --colour takes three mono planes: red, green and blue.");
                return 1;
            }
            if (!IEnhancerAvailability.Serves(denoiser, colour ? 3 : masters[0].ChannelCount, options))
            {
                consoleHost.WriteError("[denoise] no denoiser serves this input here. Register one with AddRcAstroAi() (NoiseXTerminator, else TianWen's own N2N model on colour data).");
                return 3;
            }
            var denoised = colour
                ? await NarrowbandCombination.DenoiseColourAsync(masters[0], masters[1], masters[2], denoiser, options, ct)
                : await NarrowbandCombination.DenoiseAsync(masters, denoiser, options, ct);
            for (var i = 0; i < denoised.Length; i++)
            {
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[denoise] {Path.GetFileName(paths[i])}: noise {SyntheticLuminance.BlockNoise(masters[i]):G4} -> {SyntheticLuminance.BlockNoise(denoised[i]):G4} over {SyntheticLuminance.NoiseBlockPx} px blocks ({denoiser.Name}, strength {ColourComposition.DescribeStrength(options)})"));
                WriteStep(denoised[i], StepOutput(paths[i], "_denoise", parseResult.GetValue(outputOpt), paths.Length), wcs, "denoise");
            }
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image add-line -----------------------------------

    private Command BuildAddLineCommand()
    {
        var broadbandOpt = new Option<string>("--broadband") { Description = "The broadband master the line goes into (red, for H-alpha).", Required = true };
        var lineOpt = new Option<string>("--line") { Description = "The narrowband master (H-alpha).", Required = true };
        var scaleOpt = new Option<double>("--scale")
        {
            Description = "The continuum scale between the two, as image continuum --dry-run says it on the masters WITH their stars "
                + "(on starless plates the only structure the two share is the line, and a fit there takes it out).",
            Required = true,
        };
        var weightOpt = new Option<double>("--weight")
        {
            Description = "How much of the pure line goes in, in the broadband's own units: 1 adds it once more at the line master's "
                + "signal to noise. Times what a line value is worth there, read off the two files (exposures, FLUXSCAL).",
            DefaultValueFactory = _ => 1.0,
        };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS. Default: <broadband>_line.fits." };
        var cmd = new Command("add-line", "A narrowband line's emission into a broadband channel (the HaRGB step): the line's continuum taken out "
            + "against the broadband at --scale, the rest added. Run it on starless plates, so no star is left a pit or a ring.")
        {
            Options = { broadbandOpt, lineOpt, scaleOpt, weightOpt, outputOpt },
        };
        var addLine = new AddLineOperation();
        cmd.SetAction(async (parseResult, ct) =>
        {
            var broadbandPath = parseResult.Required(broadbandOpt);
            return await RunStepAsync("add-line", addLine,
                new AddLineSettings { Scale = parseResult.GetValue(scaleOpt), Weight = parseResult.GetValue(weightOpt) },
                [broadbandPath, parseResult.Required(lineOpt)],
                StepOutput(broadbandPath, "_line", parseResult.GetValue(outputOpt), 1), ct);
        });
        return cmd;
    }

    // -------- tianwen image luminance ----------------------------------

    private Command BuildLuminanceCommand()
    {
        var redOpt = new Option<string>("--red") { Description = "The red master.", Required = true };
        var greenOpt = new Option<string>("--green") { Description = "The green master, whose scale the luminance is on.", Required = true };
        var blueOpt = new Option<string>("--blue") { Description = "The blue master.", Required = true };
        var measureOpt = new Option<string[]>("--measure-on")
        {
            Description = "Red, green and blue copies to read the star scales on (the image match-psf results), when the masters "
                + "themselves differ in star width; the noise is still read on the masters, and they are what is combined.",
            AllowMultipleArgumentsPerToken = true,
        };
        var outputOpt = new Option<string>("--output", "-o") { Description = "Output FITS.", Required = true };
        var cmd = new Command("luminance", "A synthetic luminance from red, green and blue masters WITH their stars (SyntheticLuminance): each "
            + "on green's scale by its stars' flux ratios, weighted by its noise over 4 px blocks there. Remove its stars after "
            + "(image remove-stars), denoise the starless plate, and give its detail to the starless channels (image lrgb).")
        {
            Options = { redOpt, greenOpt, blueOpt, measureOpt, outputOpt },
        };
        var luminance = new LuminanceOperation();
        cmd.SetAction(async (parseResult, ct) =>
        {
            // Green first: the luminance is on its scale, and it is the image the operation's edit is on.
            List<string> inputs = [parseResult.Required(greenOpt), parseResult.Required(redOpt), parseResult.Required(blueOpt)];
            if (parseResult.GetValue(measureOpt) is { Length: > 0 } measurePaths)
            {
                if (measurePaths.Length != 3)
                {
                    consoleHost.WriteError("[luminance] --measure-on takes red, green and blue on the masters' grid.");
                    return 1;
                }
                inputs.AddRange(measurePaths);
            }
            return await RunStepAsync("luminance", luminance, new NoSettings(), inputs, EnsureFitsExtension(parseResult.Required(outputOpt)), ct);
        });
        return cmd;
    }

    // -------- tianwen image lrgb ---------------------------------------

    private Command BuildLrgbCommand()
    {
        var luminanceOpt = new Option<string>("--luminance") { Description = "The (starless, denoised) luminance.", Required = true };
        var channelsArg = new Argument<string[]>("channels") { Description = "The (starless) channels to give its detail; each writes <channel>_lrgb.fits.", Arity = ArgumentArity.OneOrMore };
        var colourSigmaOpt = new Option<float>("--colour-sigma")
        {
            Description = "The blur in pixels a channel keeps its own colour above; below it, the luminance's detail.",
            DefaultValueFactory = _ => LuminanceDetail.DefaultColourSigma,
        };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS, with one channel only. Default: <channel>_lrgb.fits." };
        var cmd = new Command("lrgb", "Give each channel the luminance's fine detail (LuminanceDetail, LRGB in linear form): blur(channel) + "
            + "(L - blur(L)) / scale, the scale measured between the two by a linear fit. Run it on starless planes; the stars go back "
            + "after with image add-stars, in their own colour.")
        {
            Arguments = { channelsArg },
            Options = { luminanceOpt, colourSigmaOpt, outputOpt },
        };
        var lrgb = new LrgbOperation();
        cmd.SetAction(async (parseResult, ct) =>
        {
            var channels = parseResult.Required(channelsArg);
            var luminancePath = parseResult.Required(luminanceOpt);
            var settings = new LrgbSettings { ColourSigma = parseResult.GetValue(colourSigmaOpt) };
            // One channel a run of the operation, so the luminance is read for each.
            foreach (var channel in channels)
            {
                if (await RunStepAsync("lrgb", lrgb, settings, [channel, luminancePath],
                        StepOutput(channel, "_lrgb", parseResult.GetValue(outputOpt), channels.Length), ct) != 0)
                {
                    return 1;
                }
            }
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image add-stars ----------------------------------

    private Command BuildAddStarsCommand()
    {
        var starlessOpt = new Option<string>("--starless") { Description = "The starless plate.", Required = true };
        var starsOpt = new Option<string>("--stars") { Description = "The stars image remove-stars wrote beside it.", Required = true };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS. Default: <starless>_stars-back.fits." };
        var cmd = new Command("add-stars", "Put the stars back: the starless plate plus the stars image remove-stars took out (PixelMath's starless + stars).")
        {
            Options = { starlessOpt, starsOpt, outputOpt },
        };
        var addStars = new AddStarsOperation();
        cmd.SetAction(async (parseResult, ct) =>
        {
            var starlessPath = parseResult.Required(starlessOpt);
            // The stars are read as written there: a stars image is exactly zero wherever the remover left a pixel alone,
            // which is a value there, not the absent ring a master marks with zero.
            return await RunStepAsync("add-stars", addStars, new NoSettings(), [starlessPath, parseResult.Required(starsOpt)],
                StepOutput(starlessPath, "_stars-back", parseResult.GetValue(outputOpt), 1), ct);
        });
        return cmd;
    }

    // -------- shared -----------------------------------------------------

    /// <summary>Every path read as a master, absent pixels made absent (<see cref="MasterAlignment.MaskAbsent"/>: a master's
    /// uncovered ring is exact zero) unless <paramref name="maskAbsent"/> is false, and the first one's WCS; false, said,
    /// when one will not read. A step writes absent pixels as NaN, so masking its results again changes nothing.</summary>
    private bool TryReadMasters(IReadOnlyList<string> paths, out List<Image> masters, out WCS? wcs, bool maskAbsent = true)
    {
        masters = new List<Image>(paths.Count);
        wcs = null;
        foreach (var path in paths)
        {
            if (!Image.TryReadFitsFile(path, out var image, out var imageWcs))
            {
                consoleHost.WriteError($"Failed to read FITS file: {path}");
                return false;
            }
            wcs ??= imageWcs;
            if (maskAbsent)
            {
                masters.Add(MasterAlignment.MaskAbsent(image));
                image.Release();
            }
            else
            {
                masters.Add(image);
            }
        }
        return true;
    }

    private bool OneGrid(IReadOnlyList<Image> images)
    {
        foreach (var image in images)
        {
            if (image.Width != images[0].Width || image.Height != images[0].Height)
            {
                consoleHost.WriteError("the masters are not on one grid: put them there first (tianwen image align).");
                return false;
            }
        }
        return true;
    }

    // One run of the operation an edit list runs (docs/plans/stacking-workbench.md, W1), given files and said under the
    // verb's name, so the verb and a project's step are one routine. The exit code: 0, or 1 where it gave no result.
    private async Task<int> RunStepAsync<TSettings>(string verb, Operation<TSettings> operation, TSettings settings, IReadOnlyList<string> inputs, string output,
        CancellationToken ct)
        where TSettings : class, new()
    {
        if (operation.Refusal(settings) is { } refusal)
        {
            consoleHost.WriteError($"[{verb}] {refusal}");
            return 1;
        }
        var outcome = await operation.RunAsync(settings, [.. inputs.Select(OperationInput.FromFile)], output,
            line => consoleHost.WriteScrollable($"[{verb}] {line}"), ct);
        if (!outcome.Succeeded)
        {
            consoleHost.WriteError($"[{verb}] {outcome.Message}");
            return 1;
        }
        return 0;
    }

    // -o names the output of a step with one input; with several each goes beside its input.
    private static string StepOutput(string input, string suffix, string? output, int inputs)
        => output is not null && inputs == 1 ? EnsureFitsExtension(output) : DefaultOut(input, suffix);

    // Core protection is on unless --no-protect-cores; --protect-cores is a no-op kept for old command lines; both is an error.
    internal static bool TryResolveCoreProtection(bool noProtect, bool protect, out ExtendedCoreMask.Options? options)
    {
        options = noProtect ? null : ExtendedCoreMask.Options.Default;
        return !(noProtect && protect);
    }

    // The stars go beside the STARLESS output, wherever -o put it: a _starless name becomes _stars, any other gains it.
    // It used to fall back to the input's folder whenever -o's name ended in _starless, so a master read from a store
    // had its stars written into that store (an Omega Cen session master, 2026-10-07).
    internal static string StarsOutput(string starlessPath)
    {
        var stem = StripExtension(starlessPath);
        return (stem.EndsWith("_starless", StringComparison.OrdinalIgnoreCase) ? stem[..^"_starless".Length] : stem) + "_stars.fits";
    }

    // On the image's own scale, never divided by its own peak: a step's results must still relate as its inputs did.
    private void WriteStep(Image image, string path, WCS? wcs, string verb)
    {
        image.WriteToFitsFile(path, wcs, SharpenPipeline.SwModifyHeader());
        consoleHost.WriteScrollable($"[{verb}] wrote {path}");
    }
}
