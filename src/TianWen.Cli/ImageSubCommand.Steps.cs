using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

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
        cmd.SetAction((parseResult, ct) =>
        {
            var targets = parseResult.Required(targetsArg);
            if (!TryReadMasters([parseResult.Required(referenceArg), .. targets], out var masters, out var wcs) || !OneGrid(masters))
            {
                return Task.FromResult(1);
            }
            for (var i = 1; i < masters.Count; i++)
            {
                var fit = LinearFit.Measure(masters[i], masters[0], LinearFit.DefaultRejectLow, parseResult.GetValue(rejectHighOpt));
                if (!double.IsFinite(fit.Slope) || fit.Slope <= 0)
                {
                    consoleHost.WriteError($"[linear-fit] {targets[i - 1]}: no line fits ({fit.Pixels} pixels).");
                    return Task.FromResult(1);
                }
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[linear-fit] {Path.GetFileName(targets[i - 1])}: {fit.Offset:G5} + {fit.Slope:G5} x ({fit.Pixels} pixels, mean absolute deviation {fit.MeanAbsoluteDeviation:G4})"));
                WriteStep(LinearFit.Apply(masters[i], fit), StepOutput(targets[i - 1], "_linearfit", parseResult.GetValue(outputOpt), targets.Length), wcs, "linear-fit");
            }
            return Task.FromResult(0);
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
        var cmd = new Command("remove-stars", "Star removal (RC-Astro StarXTerminator; TianWen's own star remover is planned): the starless "
            + "plate and the stars it took out, each on its input's scale, so image add-stars puts them back exactly. Several masters "
            + "go through on one scale, so a scale measured between two of them still holds between their plates.")
        {
            Arguments = { mastersArg },
            Options = { outputOpt, noStarsOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var paths = parseResult.Required(mastersArg);
            if (!TryReadMasters(paths, out var masters, out var wcs) || !OneGrid(masters))
            {
                return 1;
            }
            consoleHost.WriteScrollable($"[remove-stars] {paths.Length} master(s) through {starRemover.Name}");
            var (starless, stars) = await NarrowbandCombination.SplitStarsAsync(masters, starRemover, ct);
            for (var i = 0; i < starless.Length; i++)
            {
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
        cmd.SetAction(parseResult =>
        {
            var broadbandPath = parseResult.Required(broadbandOpt);
            if (!TryReadMasters([broadbandPath, parseResult.Required(lineOpt)], out var masters, out var wcs) || !OneGrid(masters))
            {
                return 1;
            }
            var (withLine, weight) = NarrowbandCombination.AddLineFrom(masters[0], masters[1], parseResult.GetValue(scaleOpt), parseResult.GetValue(weightOpt));
            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                $"[add-line] continuum scale {parseResult.GetValue(scaleOpt):G5}; the line added at {weight:G4}"));
            WriteStep(withLine, StepOutput(broadbandPath, "_line", parseResult.GetValue(outputOpt), 1), wcs, "add-line");
            return 0;
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
        cmd.SetAction(async (parseResult, ct) =>
        {
            if (!TryReadMasters([parseResult.Required(redOpt), parseResult.Required(greenOpt), parseResult.Required(blueOpt)], out var masters, out var wcs) || !OneGrid(masters))
            {
                return 1;
            }
            List<Image>? measureOn = null;
            if (parseResult.GetValue(measureOpt) is { Length: > 0 } measurePaths)
            {
                if (measurePaths.Length != 3 || !TryReadMasters(measurePaths, out measureOn, out _) || !OneGrid([.. masters, .. measureOn]))
                {
                    consoleHost.WriteError("[luminance] --measure-on takes red, green and blue on the masters' grid.");
                    return 1;
                }
            }
            var (luminance, parts, noise) = await SyntheticLuminance.BuildAsync(masters, measureOn, reference: 1, ct);
            string[] names = ["red", "green", "blue"];
            for (var i = 0; i < parts.Length; i++)
            {
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[luminance] {names[i]}: scale {parts[i].Scale:G4} onto green ({parts[i].Stars} stars), noise {parts[i].ScaledNoise:G4} on green's scale, weight {parts[i].Weight:P1}"));
            }
            var best = Math.Min(parts[0].ScaledNoise, Math.Min(parts[1].ScaledNoise, parts[2].ScaledNoise));
            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                $"[luminance] noise {noise:G4} against the best single channel's {best:G4}: {best / noise:F2}x its signal to noise"));
            WriteStep(luminance, EnsureFitsExtension(parseResult.Required(outputOpt)), wcs, "luminance");
            return 0;
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
        var cmd = new Command("lrgb", "Give each channel the luminance's fine detail (LuminanceDetail, LRGB in linear form): blur(channel) + "
            + "(L - blur(L)) / scale, the scale measured between the two by a linear fit. Run it on starless planes; the stars go back "
            + "after with image add-stars, in their own colour.")
        {
            Arguments = { channelsArg },
            Options = { luminanceOpt, colourSigmaOpt },
        };
        cmd.SetAction(parseResult =>
        {
            var channels = parseResult.Required(channelsArg);
            if (!TryReadMasters([parseResult.Required(luminanceOpt), .. channels], out var planes, out var wcs) || !OneGrid(planes))
            {
                return 1;
            }
            for (var i = 1; i < planes.Count; i++)
            {
                var scale = LuminanceDetail.ScaleFor(planes[i], planes[0]);
                if (!double.IsFinite(scale))
                {
                    consoleHost.WriteError($"[lrgb] {channels[i - 1]}: no scale could be measured against the luminance.");
                    return 1;
                }
                // LuminanceDetail.Transfer's two halves, the scale checked first so a refusal is an answer, not a throw.
                var detailed = LuminanceDetail.Apply(planes[i], planes[0], scale, parseResult.GetValue(colourSigmaOpt));
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[lrgb] {Path.GetFileName(channels[i - 1])}: the luminance's detail at 1/{scale:G4}"));
                WriteStep(detailed, DefaultOut(channels[i - 1], "_lrgb"), wcs, "lrgb");
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
        cmd.SetAction(parseResult =>
        {
            var starlessPath = parseResult.Required(starlessOpt);
            // The stars are read as written: a stars image is exactly zero wherever the remover left a pixel alone, which
            // is a value there, not the absent ring a master marks with zero.
            if (!TryReadMasters([starlessPath], out var starless, out var wcs)
                || !TryReadMasters([parseResult.Required(starsOpt)], out var stars, out _, maskAbsent: false)
                || !OneGrid([starless[0], stars[0]]))
            {
                return 1;
            }
            WriteStep(NarrowbandCombination.WithStars(starless[0], stars[0]), StepOutput(starlessPath, "_stars-back", parseResult.GetValue(outputOpt), 1), wcs, "add-stars");
            return 0;
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

    // -o names the output of a step with one input; with several each goes beside its input.
    private static string StepOutput(string input, string suffix, string? output, int inputs)
        => output is not null && inputs == 1 ? EnsureFitsExtension(output) : DefaultOut(input, suffix);

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
