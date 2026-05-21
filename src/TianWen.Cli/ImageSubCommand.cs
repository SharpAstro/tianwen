using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Cli;

/// <summary>
/// <c>tianwen image &lt;verb&gt;</c> -- single-image AI enhancement verbs.
/// Each verb takes a FITS file in, produces FITS file(s) out. Default output
/// path is <c>&lt;input&gt;_&lt;verb&gt;.fits</c>; explicit <c>-o</c> overrides.
/// </summary>
/// <remarks>
/// All verbs go through the AI4 NAFNet enhancers wired by
/// <c>services.AddTianWenAi()</c> in <c>Program.cs</c>. Input is normalised
/// to <c>[0, 1]</c> via <see cref="Image.ScaleFloatValuesToUnit"/> before
/// inference (the enhancers validate the range and would otherwise reject
/// the call); output is written at <c>BitDepth.Float32</c> in the same
/// normalised range. WCS headers from the input round-trip into every
/// output file so downstream plate-solve / stacking calls can still use
/// the same astrometric solution.
/// </remarks>
internal sealed class ImageSubCommand(
    IConsoleHost consoleHost,
    SharpenPipeline sharpenPipeline,
    IStarRemover starRemover,
    ILogger<ImageSubCommand>? logger = null)
{
    public Command Build()
    {
        var image = new Command("image", "Single-image AI enhancement verbs (sharpen, remove-stars).")
        {
            Subcommands =
            {
                BuildSharpenCommand(),
                BuildRemoveStarsCommand(),
            },
        };
        return image;
    }

    // -------- tianwen image sharpen ------------------------------------

    private Command BuildSharpenCommand()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "FITS file to sharpen.",
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output FITS path. Default: <input>_sharpened.fits (or per-plate <input>_{starless,stars,sharpened-stars,deconvolved-starless}.fits when --no-recombine is set).",
        };
        var modeOpt = new Option<string>("--mode")
        {
            Description = "Split/recombine math: 'additive' (default, linear-light correct) or 'screen' (matches NAFNet's stretched-space training identity).",
            DefaultValueFactory = _ => "additive",
        };
        var noStellarOpt = new Option<bool>("--no-stellar-sharpen")
        {
            Description = "Skip the stellar-sharpening pass. Stars pass through unmodified into the recombine step (or output as-is when --no-recombine).",
        };
        var noDeconvOpt = new Option<bool>("--no-deconv")
        {
            Description = "Skip the non-stellar deconvolution pass. Starless plate passes through unmodified.",
        };
        var noRecombineOpt = new Option<bool>("--no-recombine")
        {
            Description = "Don't recombine the processed plates. Each plate is written as a separate file (see --output).",
        };

        var cmd = new Command("sharpen", "Full AI4 NAFNet sharpen pipeline: remove stars, sharpen the stars-only plate, deconvolve the starless plate, recombine.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, modeOpt, noStellarOpt, noDeconvOpt, noRecombineOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            if (!File.Exists(input))
            {
                consoleHost.WriteError($"Input not found: {input}");
                return 1;
            }

            var modeStr = parseResult.GetValue(modeOpt) ?? "additive";
            RecombineMode mode = modeStr.ToLowerInvariant() switch
            {
                "additive" => RecombineMode.Additive,
                "screen" => RecombineMode.Screen,
                _ => (RecombineMode)(-1),
            };
            if ((int)mode < 0)
            {
                consoleHost.WriteError($"--mode must be 'additive' or 'screen', got '{modeStr}'");
                return 1;
            }

            var noStellar = parseResult.GetValue(noStellarOpt);
            var noDeconv = parseResult.GetValue(noDeconvOpt);
            var noRecombine = parseResult.GetValue(noRecombineOpt);
            var outputPath = parseResult.GetValue(outputOpt);

            if (!Image.TryReadFitsFile(input, out var src, out var wcs))
            {
                consoleHost.WriteError($"Failed to read FITS file: {input}");
                return 1;
            }

            // AI enhancers require [0, 1]. ScaleFloatValuesToUnit is a no-op
            // when MaxValue <= 1 (= already normalised) and otherwise produces
            // a fresh copy at unit range. Original `src` is unchanged.
            var normalised = src.ScaleFloatValuesToUnit();

            var request = new SharpenRequest(
                Source: normalised,
                RunStarRemoval: true,
                RunStellarSharpen: !noStellar,
                RunNonStellarDeconv: !noDeconv,
                Recombine: !noRecombine,
                Mode: mode);

            consoleHost.WriteScrollable(
                $"[sharpen] {input} {src.Width}x{src.Height}x{src.ChannelCount} mode={mode} " +
                $"stellar={!noStellar} deconv={!noDeconv} recombine={!noRecombine}");

            SharpenResult result;
            try
            {
                result = await sharpenPipeline.ProcessAsync(request, ct);
            }
            catch (Exception ex)
            {
                consoleHost.WriteError($"Sharpen failed: {ex.Message}");
                logger?.LogError(ex, "Sharpen pipeline failed for {Input}", input);
                return 2;
            }

            if (noRecombine)
            {
                // Per-plate output: derive each path from the explicit output (if
                // any) or from the input. Skip plates that weren't produced.
                var basePath = outputPath ?? StripExtension(input);
                WritePlate(result.Starless, basePath, "_starless", wcs);
                WritePlate(result.StarsOnly, basePath, "_stars", wcs);
                WritePlate(result.SharpenedStars, basePath, "_sharpened-stars", wcs);
                WritePlate(result.DeconvolvedStarless, basePath, "_deconvolved-starless", wcs);
            }
            else
            {
                var dst = outputPath ?? DefaultOut(input, "_sharpened");
                result.Final!.WriteToFitsFile(dst, wcs);
                consoleHost.WriteScrollable($"[sharpen] wrote {dst}");
            }

            // Release intermediates the orchestrator allocated.
            result.Starless?.Release();
            result.StarsOnly?.Release();
            result.SharpenedStars?.Release();
            result.DeconvolvedStarless?.Release();
            result.Final?.Release();
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image remove-stars -------------------------------

    private Command BuildRemoveStarsCommand()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "FITS file to extract a starless plate from.",
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output FITS path. Default: <input>_starless.fits.",
        };

        var cmd = new Command("remove-stars", "AI4 NAFNet star removal only. Produces a starless export.")
        {
            Arguments = { inputArg },
            Options = { outputOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            if (!File.Exists(input))
            {
                consoleHost.WriteError($"Input not found: {input}");
                return 1;
            }

            if (!Image.TryReadFitsFile(input, out var src, out var wcs))
            {
                consoleHost.WriteError($"Failed to read FITS file: {input}");
                return 1;
            }
            var normalised = src.ScaleFloatValuesToUnit();

            consoleHost.WriteScrollable(
                $"[remove-stars] {input} {src.Width}x{src.Height}x{src.ChannelCount}");

            Image starless;
            try
            {
                starless = await starRemover.EnhanceAsync(normalised, ct);
            }
            catch (Exception ex)
            {
                consoleHost.WriteError($"remove-stars failed: {ex.Message}");
                logger?.LogError(ex, "Star removal failed for {Input}", input);
                return 2;
            }

            var dst = parseResult.GetValue(outputOpt) ?? DefaultOut(input, "_starless");
            starless.WriteToFitsFile(dst, wcs);
            consoleHost.WriteScrollable($"[remove-stars] wrote {dst}");
            starless.Release();
            return 0;
        });
        return cmd;
    }

    // -------- helpers ---------------------------------------------------

    private void WritePlate(Image? plate, string basePath, string suffix, TianWen.Lib.Astrometry.WCS? wcs)
    {
        if (plate is null) return;
        var path = basePath.EndsWith(".fits", StringComparison.OrdinalIgnoreCase)
            ? StripExtension(basePath) + suffix + ".fits"
            : basePath + suffix + ".fits";
        plate.WriteToFitsFile(path, wcs);
        consoleHost.WriteScrollable($"[sharpen] wrote {path}");
    }

    private static string DefaultOut(string input, string suffix)
        => StripExtension(input) + suffix + ".fits";

    private static string StripExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? path : path[..^ext.Length];
    }
}
