using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Png;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Imaging.Sources;
using TianWen.Lib.Imaging.Stacking;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

/// <summary>Stretch selector for the stars-only plate (under
/// <c>--dual-stretch</c>). <see cref="StarStretch"/> = Frank Sackenheim's
/// fixed-curve stars stretch -- preserves star colour + shape, gentle
/// on highlights, the historical default. <see cref="Asinh"/> = Siril-style
/// hyperbolic-arcsin stretch driven by <c>--asinh-*</c> knobs; scales all
/// channels by the same luma-derived factor so chrominance (star colour)
/// is preserved by construction. MTF and GHS on stars were considered
/// but dropped: GHS is undocumented for stars-only plates (per gh-astro.co.uk)
/// and MTF on stars doesn't beat StarStretch.</summary>
/// <summary>Which background model <c>tianwen image flatten</c> fits. <see cref="Auto"/> takes the
/// registered <c>IGradientCorrector</c>, which is GraXpert BGE where its weights are installed and the
/// classical fit otherwise; <see cref="Classical"/> and <see cref="Graxpert"/> name one and refuse to
/// fall back, so a script gets the model it asked for on every machine.</summary>
public enum FlattenBackend { Auto, Classical, Graxpert }

public enum StarStretchMode { StarStretch, Asinh }

/// <summary>Stretch selector for the starless plate (under
/// <c>--dual-stretch</c>). <see cref="Mtf"/> = midtones-balance with
/// <c>--stretch-starless-median</c>, the historical default.
/// <see cref="Ghs"/> = Cranfield's Generalised Hyperbolic Stretch chain
/// driven by the <c>--ghs-*</c> family. <see cref="Asinh"/> = Siril-style
/// hyperbolic-arcsin stretch driven by <c>--asinh-*</c> knobs.</summary>
public enum StarlessStretchMode { Mtf, Ghs, Asinh }

/// <summary>Stretch selector for the single-plate (non-split) workflow.
/// Active only when <c>--dual-stretch</c> is NOT set and no per-plate
/// stretch flag was supplied. StarStretch is NOT a valid value here:
/// it's a stars-only curve and makes no sense on a recombined or
/// unsplit plate.</summary>
public enum CombinedStretchMode { Mtf, Ghs, Asinh }

/// <summary>Selector for the <c>--ghs-converge</c> axis: whether to
/// run <see cref="Image.ConvergeGhsStretchFactor"/> against the input
/// histogram (Auto, default) or apply the caller's <c>--ghs-lnd</c>
/// verbatim (Manual). Replaces the old <c>--ghs-starless &lt;manual|auto&gt;</c>
/// distinction now that "should GHS run" is decoupled into
/// <see cref="StarStretchMode"/> / <see cref="StarlessStretchMode"/> /
/// <see cref="CombinedStretchMode"/>.</summary>
public enum GhsConvergeMode { Auto, Manual }

/// <summary>Container picked for the 2D-viewer companion file emitted next
/// to FITS by the <c>image</c> + <c>stack</c> subcommands.
/// <list type="bullet">
///   <item><term><see cref="None"/></term><description>no companion is
///   written; only the FITS output.</description></item>
///   <item><term><see cref="Png"/></term><description>8-bit-per-channel
///   RGBA via <see cref="MasterPreviewRenderer"/> -- SPCC + WB +
///   auto-stretch + sRGB ICC are baked in. Smooth gradients can band
///   against the limited bit-depth. Default for subcommands where a
///   2D preview is the deliverable (<c>stack</c>, <c>image render</c>).</description></item>
///   <item><term><see cref="PngPq"/></term><description>16-bit PNG with
///   PNG-3 <c>cICP {9, 16, 0, 1}</c> = HDR10 (BT.2020 primaries + SMPTE
///   ST 2084 PQ transfer). Samples are sRGB-EOTF'd, gamut-converted to
///   BT.2020, scaled to <c>--png-pq-peak-nits</c> (default 1000), then
///   PQ-encoded. Modern Chrome / Edge / Firefox / Safari display this
///   as actual HDR on HDR monitors; SDR monitors tonemap it back.
///   <b>Viewer note:</b> Windows 11 Photos opens the file but ignores
///   the cICP HDR10 signalling -- the PQ-encoded samples are displayed
///   as if they were sRGB, which makes the result look washed-out /
///   muted (PQ allocates most code-value space to high luminance, so
///   "scene white" lands around 0.45-0.75 in PQ code and naive
///   display reads that as mid-grey). Affinity Photo honours cICP and
///   shows the file correctly as HDR. Status of the cICP
///   <c>{1, 16, 0, 1}</c> variant (sRGB primaries + PQ transfer,
///   narrow-gamut HDR) on Windows is unverified.</description></item>
///   <item><term><see cref="Jxr"/></term><description>JPEG XR (T.832)
///   with float-true HDR pixels -- BD32F mono / BD16F RGB via
///   <see cref="Image.WriteJxrAsync"/>; no banding because the file
///   preserves the floating-point dynamic range. JXR mode skips the
///   renderer's SPCC + stretch -- the file is the (post-pipeline) plate
///   verbatim, suitable for downstream HDR-aware tools that don't want
///   a baked-in tonemap. <b>Viewer note:</b> Windows Photos opens JXR
///   when the codestream uses YCbCr 4:4:4 internal colour format; the
///   current SharpAstro.Jxr writer emits NComponent (RGB) which Photos
///   rejects. YUV 4:4:4 writer support is being added upstream.</description></item>
///   <item><term><see cref="UltraHdr"/></term><description>Ultra HDR
///   (Android Ultra HDR v1 / Adobe hdrgm 1.0) gain-map JPEG. The SDR base
///   is the same stretched sRGB raster the <see cref="Png"/> path produces;
///   an attached quarter-res gain map recovers the highlights the MTF
///   stretch clipped (star / nebula / galaxy cores), so HDR-aware viewers
///   (Chrome, Android, Photoshop / ACR) show the core structure while the
///   faint background matches SDR, and legacy viewers see only the base.
///   <c>--png-pq-peak-nits</c> sets the linear display headroom (peak nits
///   / 203-nit BT.2408 SDR reference white) the recovered cores roll off
///   toward. A lossy baseline JPEG (display artifact only), never the
///   linear master.</description></item>
/// </list></summary>
public enum ImageOutputFormat { None, Png, PngPq, Jxr, Exr, UltraHdr }

/// <summary>
/// Gamut for PNG-PQ output. <see cref="Srgb"/> (default) keeps the
/// rendered samples in sRGB primaries and tags the file with cICP
/// <c>{1, 16, 0, 1}</c> ("narrow-gamut HDR"): the PQ transfer is still
/// applied so HDR-aware viewers expand luminance, but colour saturation
/// stays at sRGB strength regardless of whether the viewer applies the
/// BT.2020-to-display gamut tonemap correctly. <see cref="Bt2020"/>
/// performs the canonical sRGB-to-BT.2020 matrix conversion and tags
/// with cICP <c>{9, 16, 0, 1}</c> = HDR10 -- the spec-blessed signal
/// for true HDR content but relies on the viewer to apply the inverse
/// gamut matrix or colours look muted on consumer (sRGB / P3) displays.
/// </summary>
public enum PngPqGamut { Srgb, Bt2020 }

/// <summary>
/// <c>tianwen image &lt;verb&gt;</c> -- single-image enhancement + render
/// verbs. Each verb takes a FITS file in, produces FITS or PNG file(s)
/// out. Default output path is <c>&lt;input&gt;_&lt;verb&gt;.fits</c>;
/// explicit <c>-o</c> overrides.
/// </summary>
/// <remarks>
/// AI enhancers go through the role-typed pipeline wired by
/// <c>services.AddRcAstroAi()</c> (RC-Astro where licensed, TianWen's own models otherwise). Input is normalised to <c>[0, 1]</c>
/// via <see cref="Image.ScaleFloatValuesToUnit"/> before inference
/// (the enhancers validate the range and would otherwise reject the call);
/// output is written at <c>BitDepth.Float32</c> in the same normalised
/// range. WCS headers from the input round-trip into every output file
/// so downstream plate-solve / stacking calls still use the same
/// astrometric solution.
///
/// <para><c>tianwen image render</c> wraps
/// <see cref="MasterPreviewRenderer"/> -- the same component
/// <c>tianwen stack</c> uses to produce the <c>master_*.png</c>
/// companion file. SPCC color calibration is computed at render time
/// and baked into the PNG; it is NOT written into the source FITS. So
/// the same FITS rendered twice produces the same PNG, but the FITS
/// itself stays color-uncalibrated -- by design, so downstream tools
/// keep the linear data untouched.</para>
/// </remarks>
internal sealed class ImageSubCommand(
    IConsoleHost consoleHost,
    SharpenPipeline sharpenPipeline,
    IStarRemover starRemover,
    IGradientCorrector gradientCorrector,
    IBackgroundExtractor backgroundExtractor,
    MasterPreviewRenderer previewRenderer,
    // Optional, and nullable for the same reason SharpenPipeline's are: the deblurrer exists only
    // when RC-Astro is installed and licensed, and the denoiser only when a backend serves the role.
    // A verb that needs one says so at the point of use rather than failing composition.
    IImageDeblurrer? deblurrer = null,
    IDenoiseEnhancer? denoiser = null,
    ILogger<ImageSubCommand>? logger = null)
{
    public Command Build()
    {
        var image = new Command("image", "Single-image enhancement, render and measurement verbs (autocrop, sharpen, remove-stars, flatten, deblur, denoise, render, stats, sources), and the three that combine masters (align, continuum, combine).")
        {
            Subcommands =
            {
                BuildAutocropCommand(),
                BuildSharpenCommand(),
                BuildRemoveStarsCommand(),
                BuildFlattenCommand(),
                BuildDeblurCommand(),
                BuildDenoiseCommand(),
                BuildRenderCommand(),
                BuildStatsCommand(),
                BuildSourcesCommand(),
                BuildAlignCommand(),
                BuildContinuumCommand(),
                BuildCombineCommand(),
            },
        };
        return image;
    }

    // -------- tianwen image align --------------------------------------

    /// <summary>
    /// Masters of one target, from other nights or through other filters, put on the reference master's grid
    /// (<see cref="MasterAlignment"/>): what any per-pixel combination needs first, a colour image from mono filters or a
    /// narrowband line against its continuum (<c>image continuum</c>, #874).
    /// </summary>
    private Command BuildAlignCommand()
    {
        var referenceArg = new Argument<string>("reference") { Description = "The master whose grid the others are put on; it is not resampled." };
        var othersArg = new Argument<string[]>("masters")
        {
            Description = "The masters to put on the reference's grid. A pair meant to be subtracted (a line and its continuum) "
                + "is best put on a THIRD master's grid, so both are resampled alike.",
            Arity = ArgumentArity.OneOrMore,
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Directory for the aligned masters, each <name>_aligned.fits. Default: beside each master.",
        };
        var cmd = new Command("align", "Put masters of one target on one master's grid, matched by their stars and resampled once (Lanczos-3, clamped); "
            + "where a master does not reach is absent (NaN).")
        {
            Arguments = { referenceArg, othersArg },
            Options = { outputOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var referencePath = parseResult.Required(referenceArg);
            if (!Image.TryReadFitsFile(referencePath, out var reference, out var referenceWcs))
            {
                consoleHost.WriteError($"Failed to read FITS file: {referencePath}");
                return 1;
            }
            var outputDir = parseResult.GetValue(outputOpt);
            if (outputDir is not null)
            {
                Directory.CreateDirectory(outputDir);
            }
            var referenceStars = await MasterAlignment.FindStarsAsync(reference, ct);
            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                $"[align] reference {Path.GetFileName(referencePath)}: {reference.Width}x{reference.Height}, {referenceStars.Count} stars, "
                + $"median FWHM {MasterAlignment.MedianFwhm(referenceStars):F2} px"));
            var failed = 0;
            foreach (var path in parseResult.GetValue(othersArg) ?? [])
            {
                if (!Image.TryReadFitsFile(path, out var master, out _))
                {
                    consoleHost.WriteError($"Failed to read FITS file: {path}");
                    failed++;
                    continue;
                }
                var (aligned, reason) = await MasterAlignment.AlignAsync(reference, master, referenceStars, ct);
                master.Release();
                if (aligned is null)
                {
                    consoleHost.WriteError($"[align] {Path.GetFileName(path)}: not aligned, {reason}");
                    failed++;
                    continue;
                }
                var dst = Path.Combine(outputDir ?? Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                    Path.GetFileNameWithoutExtension(path) + "_aligned.fits");
                aligned.Image.WriteToFitsFile(dst, referenceWcs, SharpenPipeline.SwModifyHeader());
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[align] {Path.GetFileName(path)}: {aligned.Stars} stars, matched on {aligned.QuadStars} at tolerance {aligned.QuadTolerance:F2}, "
                    + $"rms {aligned.RmsPx:F3} px; scale {aligned.Scale:F5}, rotation {aligned.RotationDeg:F3} deg, "
                    + $"shift ({aligned.ToReference.M31:F1}, {aligned.ToReference.M32:F1}) px; median FWHM {aligned.MedianFwhm:F2} px -> {dst}"));
                aligned.Image.Release();
            }
            reference.Release();
            return failed == 0 ? 0 : 1;
        });
        return cmd;
    }

    // -------- tianwen image continuum ----------------------------------

    /// <summary>
    /// The continuum taken out of a narrowband master against a broadband one on the same grid
    /// (<see cref="ContinuumSubtractor"/>, docs/plans/narrowband-colour.md phase 0, #874): the scale by the flattest
    /// residual, the stars' flux ratio beside it as the cross-check, and the line written with its own background.
    /// </summary>
    private Command BuildContinuumCommand()
    {
        var lineOpt = new Option<string>("--line") { Description = "The narrowband master (Ha, OIII, SII).", Required = true };
        var continuumOpt = new Option<string>("--continuum")
        {
            Description = "The broadband master its continuum is read from (red for Ha and SII, green or blue for OIII), on the same grid "
                + "(image align).",
            Required = true,
        };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS. Default: <line>_pure.fits beside the line." };
        var scaleOpt = new Option<double?>("--scale") { Description = "Subtract with this scale rather than a measured one." };
        var methodOpt = new Option<string>("--method")
        {
            Description = "Which measured scale to subtract with: flattest (the least-deviation residual over the continuum's "
                + "signal) or photometric (the stars' median flux ratio). Both are always reported.",
            DefaultValueFactory = _ => "flattest",
        };
        var significanceOpt = new Option<double>("--significance")
        {
            Description = "A continuum pixel carries the flattest fit only this many of its noise above its median.",
            DefaultValueFactory = _ => ContinuumSubtractor.DefaultSignificance,
        };
        var dryRunOpt = new Option<bool>("--dry-run") { Description = "Report the scales and write nothing." };
        var cmd = new Command("continuum", "Subtract a narrowband master's continuum against a broadband master on the same grid: "
            + "line - k (continuum - median continuum), k measured two ways.")
        {
            Options = { lineOpt, continuumOpt, outputOpt, scaleOpt, methodOpt, significanceOpt, dryRunOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var linePath = parseResult.Required(lineOpt);
            var continuumPath = parseResult.Required(continuumOpt);
            var method = parseResult.GetValue(methodOpt) ?? "flattest";
            if (method is not ("flattest" or "photometric"))
            {
                consoleHost.WriteError($"--method must be flattest or photometric, got '{method}'");
                return 1;
            }
            if (!Image.TryReadFitsFile(linePath, out var line, out var lineWcs))
            {
                consoleHost.WriteError($"Failed to read FITS file: {linePath}");
                return 1;
            }
            if (!Image.TryReadFitsFile(continuumPath, out var continuum, out _))
            {
                consoleHost.WriteError($"Failed to read FITS file: {continuumPath}");
                line.Release();
                return 1;
            }
            if (line.Width != continuum.Width || line.Height != continuum.Height)
            {
                consoleHost.WriteError($"--line is {line.Width}x{line.Height} and --continuum {continuum.Width}x{continuum.Height}: put them on one grid first (tianwen image align).");
                line.Release();
                continuum.Release();
                return 1;
            }

            var maskedLine = MasterAlignment.MaskAbsent(line);
            var maskedContinuum = MasterAlignment.MaskAbsent(continuum);
            var flattest = ContinuumSubtractor.FlattestResidualScale(maskedLine, maskedContinuum,
                MasterAlignment.StarChannel(maskedLine), MasterAlignment.StarChannel(maskedContinuum), parseResult.GetValue(significanceOpt));
            var lineStars = await MasterAlignment.FindStarsAsync(maskedLine, ct);
            var continuumStars = await MasterAlignment.FindStarsAsync(maskedContinuum, ct);
            var (photometric, matched, spread) = ContinuumSubtractor.PhotometricScale(lineStars, continuumStars);
            var lineFwhm = MasterAlignment.MedianFwhm(lineStars);
            var continuumFwhm = MasterAlignment.MedianFwhm(continuumStars);
            consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                $"[continuum] flattest residual k {flattest.K:G5} over {flattest.Pixels} pixels (residual deviation {flattest.ResidualAad:G4} "
                + $"against the line's {flattest.LineAad:G4}); photometric k {photometric:G5} over {matched} stars (spread {spread:P1}); "
                + $"FWHM line {lineFwhm:F2} px, continuum {continuumFwhm:F2} px"));
            if (double.IsFinite(flattest.K) && double.IsFinite(photometric) && Math.Abs(flattest.K - photometric) > 0.1 * Math.Abs(photometric))
            {
                consoleHost.WriteScrollable("[continuum] the two scales differ by more than 10 percent: check the pairing, the grid and the two PSFs before trusting either.");
            }
            if (double.IsFinite(lineFwhm) && double.IsFinite(continuumFwhm) && Math.Abs(lineFwhm - continuumFwhm) > 0.15 * Math.Max(lineFwhm, continuumFwhm))
            {
                consoleHost.WriteScrollable("[continuum] the two masters' stars differ in width by more than 15 percent: subtracted stars will leave rings, and a fixed-aperture flux ratio reads the wider side low.");
            }

            var k = parseResult.GetValue(scaleOpt) ?? (method == "photometric" ? photometric : flattest.K);
            if (!double.IsFinite(k))
            {
                consoleHost.WriteError($"[continuum] no {method} scale could be measured; pass --scale.");
                line.Release();
                continuum.Release();
                return 1;
            }
            if (!parseResult.GetValue(dryRunOpt))
            {
                var dst = parseResult.GetValue(outputOpt)
                    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(linePath)) ?? ".", Path.GetFileNameWithoutExtension(linePath) + "_pure.fits");
                var pure = ContinuumSubtractor.Subtract(maskedLine, maskedContinuum, k);
                pure.WriteToFitsFile(dst, lineWcs, SharpenPipeline.SwModifyHeader());
                pure.Release();
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture, $"[continuum] subtracted with k {k:G5} -> {dst}"));
            }
            line.Release();
            continuum.Release();
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image combine ------------------------------------

    /// <summary>
    /// Three mono broadband masters on one grid made one colour image, with an H-alpha master's emission added to red once
    /// its continuum is out (<see cref="NarrowbandCombination"/>, docs/plans/narrowband-colour.md phase 2). With
    /// <c>--starless</c> the emission is added to starless planes and the broadband stars go back on top, so the single
    /// continuum scale leaves no pit or ring at a star.
    /// </summary>
    private Command BuildCombineCommand()
    {
        var redOpt = new Option<string>("--red") { Description = "The red master.", Required = true };
        var greenOpt = new Option<string>("--green") { Description = "The green master.", Required = true };
        var blueOpt = new Option<string>("--blue") { Description = "The blue master.", Required = true };
        var haOpt = new Option<string?>("--ha") { Description = "An H-alpha master whose emission goes into red, its continuum taken out against red." };
        var haScaleOpt = new Option<double?>("--ha-scale")
        {
            Description = "The continuum scale to subtract with, against red as given (as image continuum measures it). Default: "
                + "measured on the H-alpha and red masters WITH their stars (the flattest residual), never on starless plates, "
                + "where the only structure the two share is the line itself.",
        };
        var haWeightOpt = new Option<double>("--ha-weight")
        {
            Description = "How much of the pure H-alpha goes into red, in red's own units (the line scaled by the exposure ratio): "
                + "1 adds the line once more at the H-alpha master's signal to noise.",
            DefaultValueFactory = _ => 1.0,
        };
        var starlessOpt = new Option<bool>("--starless")
        {
            Description = "Remove the stars from every master first (the active star remover, RC-Astro StarXTerminator), add the "
                + "emission to the starless planes, and put the broadband stars back on top.",
        };
        var linearFitOpt = new Option<string>("--linear-fit")
        {
            Description = "Put red, green and blue on this channel's scale before anything else (LinearFit, PixInsight's): a "
                + "least-absolute-deviation line over the pixels both hold below 0.92 of their peaks. H-alpha is never fitted; "
                + "its addition to red follows red's fitted slope. 'none' leaves the masters as they are.",
            DefaultValueFactory = _ => "green",
        };
        linearFitOpt.AcceptOnlyFromAmong("red", "green", "blue", "none");
        var deblurOpt = new Option<bool>("--deblur")
        {
            Description = "Deblur every master (RC-Astro BlurXTerminator) before its stars are removed, all on one scale with the "
                + "brightest star at a quarter of the deblurrer's ceiling so no sharpened star clips, and report the star widths "
                + "after: BlurX brings the channels to nearly one width. Where no deblurrer serves, --match-psf instead.",
        };
        var matchPsfOpt = new Option<bool>("--match-psf")
        {
            Description = "The fallback without a deblurrer: blur every sharper master to the widest one's star width (PsfMatch) "
                + "for the colour channels, the continuum scale and the luminance's star scales. The synthetic luminance itself "
                + "is always made from the unblurred masters.",
        };
        var luminanceOpt = new Option<string?>("--luminance")
        {
            Description = "Also write the synthetic luminance here (SyntheticLuminance): the final red, green and blue (red with "
                + "its H-alpha) on green's photometric scale by their stars, weighted by their noise there.",
        };
        var lrgbOpt = new Option<bool>("--lrgb")
        {
            Description = "Give every channel its fine detail from the synthetic luminance (LuminanceDetail): its own colour above "
                + "--colour-sigma, the luminance's lower noise below it.",
        };
        var denoiseOpt = new Option<bool>("--denoise")
        {
            Description = "Denoise the STARLESS planes (the active denoiser, RC-Astro NoiseXTerminator where licensed); the stars "
                + "are never denoised, so it needs --starless. With --lrgb the luminance, which carries the detail, is denoised at "
                + "--denoise-strength and the colour more lightly at --colour-denoise-strength; without, the colour channels "
                + "carry the detail and take --denoise-strength. The luminance's weights are measured before anything is denoised.",
        };
        var denoiseStrengthOpt = new Option<float?>("--denoise-strength")
        {
            Description = "With --denoise, the strength in [0, 1] for the planes that carry the detail. Default: the denoiser's own "
                + "(NoiseXTerminator's is set from each plate's noise).",
        };
        var colourDenoiseStrengthOpt = new Option<float>("--colour-denoise-strength")
        {
            Description = "With --denoise and --lrgb, the strength in [0, 1] for the colour, whose detail below --colour-sigma the "
                + "luminance replaces anyway.",
            DefaultValueFactory = _ => 0.5f,
        };
        var colourSigmaOpt = new Option<float>("--colour-sigma")
        {
            Description = "With --lrgb, the blur in pixels the channels keep their own colour above.",
            DefaultValueFactory = _ => LuminanceDetail.DefaultColourSigma,
        };
        var outputOpt = new Option<string>("--output", "-o") { Description = "Output FITS (three channels, linear).", Required = true };
        var formatOpt = OutputFormatOption("2D-viewer companion alongside the FITS output; 'png' renders it through the master preview stretch.");
        var (pngPqPeakNitsOpt, pngPqGamutOpt) = HdrCompanionOptions();
        var cmd = new Command("combine", "One colour image from red, green and blue mono masters on one grid (image align), "
            + "optionally with H-alpha's emission added to red, its continuum subtracted.")
        {
            Options = { redOpt, greenOpt, blueOpt, haOpt, haScaleOpt, haWeightOpt, starlessOpt, linearFitOpt, deblurOpt, matchPsfOpt, luminanceOpt, lrgbOpt, colourSigmaOpt, denoiseOpt, denoiseStrengthOpt, colourDenoiseStrengthOpt, outputOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var paths = new List<string> { parseResult.Required(redOpt), parseResult.Required(greenOpt), parseResult.Required(blueOpt) };
            if (parseResult.GetValue(haOpt) is { } haPath)
            {
                paths.Add(haPath);
            }
            var masters = new List<Image>(paths.Count);
            WCS? wcs = null;
            foreach (var path in paths)
            {
                if (!Image.TryReadFitsFile(path, out var image, out var imageWcs))
                {
                    consoleHost.WriteError($"Failed to read FITS file: {path}");
                    return 1;
                }
                wcs ??= imageWcs;
                masters.Add(MasterAlignment.MaskAbsent(image));
                image.Release();
            }
            foreach (var m in masters)
            {
                if (m.Width != masters[0].Width || m.Height != masters[0].Height)
                {
                    consoleHost.WriteError("the masters are not on one grid: put them there first (tianwen image align).");
                    return 1;
                }
            }

            var names = new[] { "red", "green", "blue", "H-alpha" };
            var hasHa = masters.Count == 4;
            var denoise = parseResult.GetValue(denoiseOpt);
            if (denoise && !parseResult.GetValue(starlessOpt))
            {
                consoleHost.WriteError("[combine] --denoise needs --starless: the stars are never denoised.");
                return 1;
            }
            var detailDenoise = parseResult.GetValue(denoiseStrengthOpt) is { } strength
                ? new EnhanceOptions(Tuning: new EnhanceTuning(DenoiseStrength: Math.Clamp(strength, 0f, 1f)))
                : EnhanceOptions.Default;
            var colourDenoise = new EnhanceOptions(Tuning: new EnhanceTuning(
                DenoiseStrength: Math.Clamp(parseResult.GetValue(colourDenoiseStrengthOpt), 0f, 1f)));

            // One scale for the broadband channels first (PixInsight's LinearFit). H-alpha is left as it is: its sky and
            // its emission are not a broadband channel's, and the continuum scale is what relates it to red.
            var redSlope = 1.0;
            var fitTo = parseResult.GetValue(linearFitOpt) ?? "green";
            if (fitTo != "none")
            {
                var reference = Array.IndexOf(names, fitTo);
                for (var i = 0; i < 3; i++)
                {
                    if (i == reference)
                    {
                        continue;
                    }
                    var fit = LinearFit.Measure(masters[i], masters[reference]);
                    if (!double.IsFinite(fit.Slope) || fit.Slope <= 0)
                    {
                        consoleHost.WriteError($"[combine] no linear fit of {names[i]} onto {fitTo} ({fit.Pixels} pixels); pass --linear-fit none.");
                        return 1;
                    }
                    masters[i] = LinearFit.Apply(masters[i], fit);
                    if (i == 0)
                    {
                        redSlope = fit.Slope;
                    }
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"[combine] linear fit {names[i]} onto {fitTo}: {fit.Offset:G5} + {fit.Slope:G5} x {names[i]} ({fit.Pixels} pixels, mean absolute deviation {fit.MeanAbsoluteDeviation:G4})"));
                }
            }

            // Stars at one width. BlurX does it without giving up detail; the blur match, the fallback, gives it up, so its
            // planes serve the colour and the measurements while the luminance is made from the unblurred ones.
            IReadOnlyList<Image> sharp = masters;
            var colour = sharp;
            var colourBlurred = false;
            var matchPsf = parseResult.GetValue(matchPsfOpt);
            if (parseResult.GetValue(deblurOpt) && IEnhancerAvailability.Serves(deblurrer, 1, EnhanceOptions.Default))
            {
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[combine] deblurring {masters.Count} masters ({deblurrer.Name}), the brightest star at 1/{NarrowbandCombination.DefaultDeblurHeadroom:G3} of the ceiling"));
                var before = new double[masters.Count];
                for (var i = 0; i < masters.Count; i++)
                {
                    before[i] = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(masters[i], ct));
                }
                var (deblurred, atCeiling) = await NarrowbandCombination.DeblurAsync(masters, deblurrer, cancellationToken: ct);
                sharp = deblurred;
                colour = sharp;
                for (var i = 0; i < sharp.Count; i++)
                {
                    var after = MasterAlignment.MedianFwhm(await MasterAlignment.FindStarsAsync(sharp[i], ct));
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"[combine] {names[i]}: FWHM {before[i]:F2} px, deblurred to {after:F2}"));
                }
                if (atCeiling > 0)
                {
                    consoleHost.WriteScrollable($"[combine] {atCeiling} deblurred pixels reached the deblurrer's ceiling and are clipped.");
                }
            }
            else
            {
                if (parseResult.GetValue(deblurOpt))
                {
                    consoleHost.WriteScrollable("[combine] --deblur: no deblurrer serves here (RC-Astro BlurXTerminator, installed and licensed); matching the star widths by blurring instead.");
                    matchPsf = true;
                }
                if (matchPsf)
                {
                    var (matched, report, target) = await PsfMatch.ToWidestAsync(masters, ct);
                    colour = matched;
                    colourBlurred = true;
                    for (var i = 0; i < report.Length; i++)
                    {
                        consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                            $"[combine] {names[i]}: FWHM {report[i].FwhmBefore:F2} px, blurred by sigma {report[i].Sigma:F2} to {report[i].FwhmAfter:F2} (target {target:F2}), for the colour only"));
                    }
                }
            }

            // The continuum scale on the masters WITH their stars, which are what define it, at one width.
            var k = 0.0;
            if (hasHa)
            {
                k = parseResult.GetValue(haScaleOpt) is { } given
                    ? given / redSlope
                    : ContinuumSubtractor.FlattestResidualScale(colour[3], colour[0]).K;
                if (!double.IsFinite(k))
                {
                    consoleHost.WriteError("[combine] no continuum scale could be measured between H-alpha and red; pass --ha-scale.");
                    return 1;
                }
            }

            var starlessWanted = parseResult.GetValue(starlessOpt);
            var haWeight = parseResult.GetValue(haWeightOpt);
            var colourParts = await ComposeAsync(colour, report: true);

            var luminancePath = parseResult.GetValue(luminanceOpt);
            var lrgb = parseResult.GetValue(lrgbOpt);
            var final = colourParts.Full;
            if (luminancePath is not null || lrgb)
            {
                // The luminance from planes never blurred, composed as the colour was; its star scales from the colour
                // planes, whose stars are at one width, and its noise from the unblurred ones, which a blur would flatter.
                var sharpParts = colourBlurred ? await ComposeAsync(sharp, report: false) : colourParts;
                var parts = await SyntheticLuminance.MeasureAsync(colourParts.Full, reference: 1,
                    colourBlurred ? sharpParts.Full : null, ct);
                var luminance = SyntheticLuminance.Combine(sharpParts.Full, parts, reference: 1);
                var noise = SyntheticLuminance.BlockNoise(luminance);
                for (var i = 0; i < parts.Length; i++)
                {
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"[combine] luminance {names[i]}: scale {parts[i].Scale:G4} onto green ({parts[i].Stars} stars), noise {parts[i].ScaledNoise:G4} on green's scale over {SyntheticLuminance.NoiseBlockPx} px blocks, weight {parts[i].Weight:P1}"));
                }
                var best = Math.Min(parts[0].ScaledNoise, Math.Min(parts[1].ScaledNoise, parts[2].ScaledNoise));
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[combine] luminance noise {noise:G4} against the best single channel's {best:G4}: {best / noise:F2}x its signal to noise"));
                // The starless luminance, denoised with --denoise after the weights above were read; its stars, never.
                Image? starlessLuminance = null;
                if (colourParts.Stars is not null)
                {
                    starlessLuminance = SyntheticLuminance.Combine(sharpParts.Starless, parts, reference: 1);
                    if (denoise)
                    {
                        var denoisedLuminance = await DenoisedAsync(starlessLuminance, detailDenoise, "luminance");
                        if (luminancePath is not null)
                        {
                            luminance = NarrowbandCombination.WithStars(denoisedLuminance, NarrowbandCombination.Stars(luminance, starlessLuminance));
                        }
                        starlessLuminance = denoisedLuminance;
                    }
                }
                if (luminancePath is not null)
                {
                    luminance.ScaleFloatValuesToUnit().WriteToFitsFile(luminancePath, wcs, SharpenPipeline.SwModifyHeader());
                    consoleHost.WriteScrollable($"[combine] wrote {luminancePath}");
                }
                if (lrgb)
                {
                    var sigma = parseResult.GetValue(colourSigmaOpt);
                    if (colourParts.Stars is not { } colourStars || starlessLuminance is null)
                    {
                        consoleHost.WriteScrollable("[combine] --lrgb without --starless: the stars take the detail too and will show coloured rims.");
                        final = [LuminanceDetail.Apply(colourParts.Full[0], luminance, parts[0].Scale, sigma),
                            LuminanceDetail.Apply(colourParts.Full[1], luminance, parts[1].Scale, sigma),
                            LuminanceDetail.Apply(colourParts.Full[2], luminance, parts[2].Scale, sigma)];
                    }
                    else
                    {
                        // On the starless channels, where the colour noise is; the stars go back with their own colour.
                        var colourStarless = denoise ? await DenoisedColourAsync(colourParts.Starless, colourDenoise) : colourParts.Starless;
                        final = [NarrowbandCombination.WithStars(LuminanceDetail.Apply(colourStarless[0], starlessLuminance, parts[0].Scale, sigma), colourStars[0]),
                            NarrowbandCombination.WithStars(LuminanceDetail.Apply(colourStarless[1], starlessLuminance, parts[1].Scale, sigma), colourStars[1]),
                            NarrowbandCombination.WithStars(LuminanceDetail.Apply(colourStarless[2], starlessLuminance, parts[2].Scale, sigma), colourStars[2])];
                    }
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"[combine] every channel's detail below {sigma:G3} px taken from the luminance{(colourParts.Stars is null ? "" : ", the stars kept as they were")}"));
                }
            }
            if (denoise && !lrgb && colourParts.Stars is { } starsOnly)
            {
                // No luminance to carry the detail: the colour channels carry it, at the detail strength.
                var denoised = await DenoisedColourAsync(colourParts.Starless, detailDenoise);
                final = [NarrowbandCombination.WithStars(denoised[0], starsOnly[0]), NarrowbandCombination.WithStars(denoised[1], starsOnly[1]),
                    NarrowbandCombination.WithStars(denoised[2], starsOnly[2])];
            }
            var red = final[0];
            var green = final[1];
            var blue = final[2];
            var rgb = NarrowbandCombination.Rgb(red, green, blue).ScaleFloatValuesToUnit();
            var dst = parseResult.Required(outputOpt);
            rgb.WriteToFitsFile(dst, wcs, SharpenPipeline.SwModifyHeader());
            consoleHost.WriteScrollable($"[combine] wrote {dst}");
            await WriteCompanionAsync(rgb, dst, parseResult.GetValue(formatOpt), rgb.ImageMeta, wcs, "combine",
                useStretchedPng: false,
                peakNits: Math.Clamp(parseResult.GetValue(pngPqPeakNitsOpt), 1f, 10000f),
                gamutToBt2020: parseResult.GetValue(pngPqGamutOpt) == PngPqGamut.Bt2020, ct: ct);
            return 0;

            // A starless plane denoised, or handed back as it was where no denoiser serves a mono plane (the in-house one is
            // colour only), said either way with its noise over blocks before and after.
            async Task<Image> DenoisedAsync(Image starless, EnhanceOptions options, string what)
            {
                if (!IEnhancerAvailability.Serves(denoiser, 1, options))
                {
                    consoleHost.WriteScrollable($"[combine] --denoise: no denoiser serves a mono plane here; the {what} is left as it is.");
                    return starless;
                }
                var denoised = (await NarrowbandCombination.DenoiseAsync([starless], denoiser, options, ct))[0];
                consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                    $"[combine] denoised the starless {what} ({denoiser.Name}, strength {DescribeStrength(options)}): noise {SyntheticLuminance.BlockNoise(starless):G4} -> {SyntheticLuminance.BlockNoise(denoised):G4} over {SyntheticLuminance.NoiseBlockPx} px blocks"));
                return denoised;
            }

            // The starless red, green and blue denoised together as one colour image.
            async Task<Image[]> DenoisedColourAsync(Image[] starless, EnhanceOptions options)
            {
                if (!IEnhancerAvailability.Serves(denoiser, 3, options))
                {
                    consoleHost.WriteScrollable("[combine] --denoise: no denoiser serves a colour image here; the colour is left as it is.");
                    return starless;
                }
                var denoised = await NarrowbandCombination.DenoiseColourAsync(starless[0], starless[1], starless[2], denoiser, options, ct);
                for (var i = 0; i < 3; i++)
                {
                    consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                        $"[combine] denoised the starless {names[i]} ({denoiser.Name}, strength {DescribeStrength(options)}): noise {SyntheticLuminance.BlockNoise(starless[i]):G4} -> {SyntheticLuminance.BlockNoise(denoised[i]):G4}"));
                }
                return denoised;
            }

            static string DescribeStrength(EnhanceOptions options)
                => options.Tuning?.DenoiseStrength is { } s ? s.ToString("G3", CultureInfo.InvariantCulture) : "the denoiser's own";

            // Red (with the H-alpha's emission), green and blue from planes on one grid: with --starless the emission goes
            // into the starless red and each channel's own stars back on top. Starless is the three channels before the stars
            // go back (the full ones without --starless); Stars is null without it.
            async Task<(Image[] Full, Image[] Starless, Image[]? Stars)> ComposeAsync(IReadOnlyList<Image> source, bool report)
            {
                IReadOnlyList<Image> planes = source;
                Image[]? layerStars = null;
                if (starlessWanted)
                {
                    consoleHost.WriteScrollable($"[combine] removing stars from {source.Count} masters ({starRemover.Name})");
                    var starless = await NarrowbandCombination.StarlessAsync(source, starRemover, ct);
                    layerStars = [NarrowbandCombination.Stars(source[0], starless[0]), NarrowbandCombination.Stars(source[1], starless[1]),
                        NarrowbandCombination.Stars(source[2], starless[2])];
                    planes = starless;
                }

                var withLine = planes[0];
                if (hasHa)
                {
                    var pure = ContinuumSubtractor.Subtract(planes[3], planes[0], k);
                    var toRed = NarrowbandCombination.LineToBroadband(planes[3].ImageMeta, planes[0].ImageMeta);
                    // In red's own units the line is worth the exposure ratio; on red's fitted scale, its slope as well.
                    var weight = haWeight * toRed * redSlope;
                    withLine = NarrowbandCombination.AddLine(planes[0], pure, weight);
                    if (report)
                    {
                        consoleHost.WriteScrollable(string.Create(CultureInfo.InvariantCulture,
                            $"[combine] H-alpha continuum k {k:G5}; its emission added to red at {weight:G4} (weight {haWeight:G3} x exposure ratio {toRed:G3} x red's fitted slope {redSlope:G4})"));
                    }
                    pure.Release();
                }
                Image[] channels = [withLine, planes[1], planes[2]];
                Image[] full = layerStars is null
                    ? channels
                    : [NarrowbandCombination.WithStars(channels[0], layerStars[0]), NarrowbandCombination.WithStars(channels[1], layerStars[1]),
                        NarrowbandCombination.WithStars(channels[2], layerStars[2])];
                return (full, channels, layerStars);
            }
        });
        return cmd;
    }

    // -------- tianwen image autocrop -----------------------------------


    /// <summary>
    /// The auto-crop the viewer performs, as a verb.
    ///
    /// <para>A union canvas's border is partial coverage, and everything downstream is wrong over it:
    /// an enhancer reads the ring as structure, a stretch takes its statistics over pixels no exposure
    /// produced, and a preview renders the absent part black. <see cref="ViewerActions.ScanForCrop"/>
    /// already answers this exactly -- the drizzle weight plane from the <c>.rejection.fits</c> sidecar
    /// where one exists, <see cref="CoverageEdgeWalk"/> over the pixels otherwise -- and the viewer and
    /// <c>tianwen stack</c> have both used it for months. Only the <c>image</c> verbs could not reach
    /// it, so every caller outside those two re-invented the trim and got it wrong: a fixed
    /// "more than 2 percent absent" rule cannot see this edge at all, because these masters are absent
    /// about 1 percent EVERYWHERE from interior drizzle holes. On the HIP 80609 ASI533 master the left
    /// edge runs 100 / 84 / 63 / 42 / 20 / 2 percent absent over its first 25 columns; such a rule stops
    /// with the band still 20 percent empty.</para>
    ///
    /// <para>This calls that one implementation and nothing of its own.</para>
    /// </summary>
    private Command BuildAutocropCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "FITS master to crop." };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output FITS. Default: <input>_autocrop.fits beside the input.",
        };
        var dryRunOpt = new Option<bool>("--dry-run")
        {
            Description = "Report the rectangle and which tier answered, and write nothing.",
        };
        var marginOpt = new Option<double>("--margin")
        {
            Description = "Inset every edge by this FRACTION of the axis after the scan (0.01 = 1 percent). "
                + "Default 0, which is the viewer's own answer. Raise it when the crop feeds a MODEL rather "
                + "than an eye: the edge walk refuses an edge whose band never settles, and a refusal keeps "
                + "the partial-coverage ramp, which a background-extraction model then fits. GraXpert is "
                + "particular about this -- on the HIP 80609 master its declined left edge left green and "
                + "blue at 0.98 of the interior for the first ten columns after flattening, visible at any "
                + "hard stretch. A crop for a viewer should show every pixel that exists; a crop for a fit "
                + "should give up a margin rather than hand it a ramp.",
            DefaultValueFactory = _ => 0.0,
        };
        var trimDeclinedOpt = new Option<double>("--trim-declined")
        {
            Description = "When the walk LEAVES AN EDGE ALONE without calling it clean, trim it anyway. "
                + "An edge that SETTLED PAST THE LOSS CAP was measured, so it comes off at its own settle "
                + "depth and this fraction is never consulted for it. The fraction is the fallback for an "
                + "edge with no depth to read: one whose noise was still falling at the end of the search "
                + "window, or that could not be measured at all. Default 0.05, the walk's own cap. A "
                + "measured depth is deeper than that cap by definition, so a beyond-cap edge loses MORE "
                + "than this number, bounded by the search window at 0.10 of the axis. That is the point: "
                + "it is where the noise actually comes down, not a fraction chosen without seeing the "
                + "frame. "
                + "0 keeps the viewer's behaviour, which is to leave a refused edge alone: correct when a "
                + "person is looking at every pixel that exists, wrong when the crop feeds a background "
                + "model, because the ramp it keeps is exactly what the model then fits. Measured on the "
                + "QHY294C SMC master, whose left and right edges both declined: the outermost ~130 px sit "
                + "0.00003 linear under the plateau, which is 3 noise-MAD and renders as a 7-level band at "
                + "the preview stretch, dying out by 500 px of 4108. Only declined edges are touched; an "
                + "edge that answered is already trimmed to where it settled.",
            DefaultValueFactory = _ => 0.05,
        };

        var cmd = new Command("autocrop",
            "Crop a master to the rectangle its subs actually covered. Prefers the drizzle weight plane "
            + "from the <master>.rejection.fits sidecar, which states the covered area outright, and falls "
            + "back to the coverage edge walk over the pixels. The same scan the FITS viewer's auto-crop "
            + "and `tianwen stack` use. The WCS is translated with the crop, so the output still solves.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, dryRunOpt, marginOpt, trimDeclinedOpt },
        };

        cmd.SetAction((parseResult, ct) =>
        {
            var input = parseResult.Required(inputArg);
            if (!File.Exists(input))
            {
                consoleHost.WriteError($"Input not found: {input}");
                return Task.FromResult(1);
            }

            if (!Image.TryReadFitsFile(input, out var src, out var wcs))
            {
                consoleHost.WriteError($"Failed to read FITS file: {input}");
                return Task.FromResult(1);
            }

            var scan = ViewerActions.ScanForCrop(src, input, logger);
            var rect = scan.Rect;

            // What a refused edge loses is CoverageTrimPolicy's, not this verb's. The scan hands back a
            // viewer-shaped rectangle (every pixel that exists), so this re-applies the same verdicts
            // under the producing policy, which is the one the stacker uses. This verb and a master
            // written by `tianwen stack` therefore crop identically, which they did not while this
            // logic lived here: the CLI removed V1045 Ori's strip exactly and the bake kept it.
            var trimDeclined = Math.Clamp(parseResult.GetValue(trimDeclinedOpt), 0.0, 0.25);
            if (trimDeclined > 0 && scan is { Declined: true, Trims: { } trims } && rect.Width > 0 && rect.Height > 0)
            {
                var policy = CoverageTrimPolicy.Default with { DeclinedFraction = trimDeclined };
                var held = trims.Apply(rect, policy);
                if (held != rect)
                {
                    var edges = string.Join(", ", new[]
                    {
                        policy.Describe("left", trims.Left, policy.DepthFor(trims.Left, rect.Width)),
                        policy.Describe("top", trims.Top, policy.DepthFor(trims.Top, rect.Height)),
                        policy.Describe("right", trims.Right, policy.DepthFor(trims.Right, rect.Width)),
                        policy.Describe("bottom", trims.Bottom, policy.DepthFor(trims.Bottom, rect.Height)),
                    }.Where(e => e is not null));
                    consoleHost.WriteScrollable(
                        $"[autocrop] declined ({edges}): "
                        + $"{rect.Width}x{rect.Height} -> {held.Width}x{held.Height}");
                    rect = held;
                }
                else
                {
                    consoleHost.WriteScrollable($"[autocrop] trimming the declined edges would leave nothing; kept.");
                }
            }

            var margin = Math.Clamp(parseResult.GetValue(marginOpt), 0.0, 0.25);
            if (margin > 0)
            {
                var ix = (int)Math.Round(src.Width * margin);
                var iy = (int)Math.Round(src.Height * margin);
                var inset = new PixelRect(rect.X + ix, rect.Y + iy, rect.Width - 2 * ix, rect.Height - 2 * iy);
                if (inset.Width > 16 && inset.Height > 16)
                {
                    consoleHost.WriteScrollable(
                        $"[autocrop] margin {margin:P1}: {rect.Width}x{rect.Height} -> {inset.Width}x{inset.Height}");
                    rect = inset;
                }
                else
                {
                    consoleHost.WriteScrollable($"[autocrop] margin {margin:P1} would leave nothing; ignored.");
                }
            }
            var tier = scan.FromCoverage ? "coverage plane" : "edge walk";
            consoleHost.WriteScrollable(
                $"[autocrop] {src.Width}x{src.Height} -> {rect.Width}x{rect.Height} at ({rect.X},{rect.Y}) "
                + $"via the {tier}{(scan.Declined ? ", one or more edges DECLINED (band never settled)" : "")}");

            if (rect.Width <= 0 || rect.Height <= 0)
            {
                consoleHost.WriteError("The scan left nothing; refusing to write an empty crop.");
                return Task.FromResult(2);
            }
            if (parseResult.GetValue(dryRunOpt))
            {
                return Task.FromResult(0);
            }

            var cropped = src.Crop(rect);
            var dst = EnsureFitsExtension(parseResult.GetValue(outputOpt) ?? DefaultOut(input, "_autocrop"));
            cropped.WriteToFitsFile(dst, wcs?.CroppedTo(rect.X, rect.Y), SharpenPipeline.SwModifyHeader());
            consoleHost.WriteScrollable($"[autocrop] wrote {dst}");
            return Task.FromResult(0);
        });

        return cmd;
    }

    // The companion-file options, declared once instead of copied into every verb that offers them.
    private static Option<ImageOutputFormat> BuildCompanionFormatOption() => new("--output-format")
    {
        Description = "2D-viewer companion file alongside the FITS output. 'none' (default) = no companion. 'png' = 16-bit RGBA + cICP sRGB (SDR). 'png-pq' = 16-bit RGBA + cICP HDR10 PQ (HDR display). 'jxr' = JPEG XR with float-true HDR pixels.",
        DefaultValueFactory = _ => ImageOutputFormat.None,
        CustomParser = ParseOutputFormat,
    };

    private static Option<float> BuildPngPqPeakNitsOption() => new("--png-pq-peak-nits")
    {
        Description = "Peak luminance for HDR PQ output (--output-format png-pq). Range (0, 10000]. Default 1000.",
        DefaultValueFactory = _ => 1000f,
    };

    private static Option<PngPqGamut> BuildPngPqGamutOption() => new("--png-pq-gamut")
    {
        Description = "Colour primaries for HDR PQ output (--output-format png-pq). 'srgb' (default) keeps sRGB primaries, cICP {1, 16, 0, 1}. 'bt2020' applies sRGB-to-BT.2020 matrix, cICP {9, 16, 0, 1} = canonical HDR10.",
        DefaultValueFactory = _ => PngPqGamut.Srgb,
    };

    // -------- tianwen image deblur / denoise ---------------------------

    /// <summary>
    /// One enhancer role, as a verb. <c>remove-stars</c> and <c>flatten</c> already expose
    /// <see cref="IStarRemover"/> and <see cref="IGradientCorrector"/> this way; deblur and denoise
    /// are the two roles a user reaches for just as often and could only be had by running the whole
    /// <c>sharpen</c> program, which also removes stars and recombines. Running one step is a
    /// legitimate thing to want -- to see what it did on its own, to feed another tool, or to stop
    /// where a hand-processed flow wants to take over.
    /// </summary>
    private async Task<int> RunSingleRoleAsync(
        string verb, IImageEnhancer? enhancer, string missingHint,
        string input, string? output, ImageOutputFormat format,
        float peakNits, bool gamutToBt2020, CancellationToken ct)
    {
        if (!File.Exists(input))
        {
            consoleHost.WriteError($"Input not found: {input}");
            return 1;
        }
        if (enhancer is null)
        {
            consoleHost.WriteError($"No {verb} enhancer is registered. {missingHint}");
            return 3;
        }
        if (!Image.TryReadFitsFile(input, out var src, out var wcs))
        {
            consoleHost.WriteError($"Failed to read FITS file: {input}");
            return 1;
        }

        // AI enhancers require [0, 1]; a no-op when the master is already normalised.
        var normalised = src.ScaleFloatValuesToUnit();
        Image result;
        try
        {
            result = await enhancer.EnhanceAsync(normalised, ct);
        }
        catch (Exception ex)
        {
            consoleHost.WriteError($"{verb} failed: {ex.Message}");
            logger?.LogError(ex, "{Verb} failed for {Input}", verb, input);
            return 2;
        }

        var dst = EnsureFitsExtension(output ?? DefaultOut(input, "_" + verb));
        result.WriteToFitsFile(dst, wcs, SharpenPipeline.SwModifyHeader());
        consoleHost.WriteScrollable($"[{verb}] {enhancer.Name}: wrote {dst}");
        await WriteCompanionAsync(result, dst, format, src.ImageMeta, wcs, verb,
            useStretchedPng: false, peakNits: peakNits, gamutToBt2020: gamutToBt2020, ct: ct);
        return 0;
    }

    private Command BuildDeblurCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "FITS image to deblur." };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS. Default: <input>_deblur.fits." };
        var formatOpt = BuildCompanionFormatOption();
        var peakNitsOpt = BuildPngPqPeakNitsOption();
        var gamutOpt = BuildPngPqGamutOption();

        var cmd = new Command("deblur",
            "Whole-frame deconvolution (RC-Astro BlurXTerminator) and nothing else. This is the step that "
            + "heads the canonical BlurX-first flow, run on its own. Linear in, linear out; stars are NOT "
            + "split out first, which is the point -- BlurX works on the whole frame.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, formatOpt, peakNitsOpt, gamutOpt },
        };
        cmd.SetAction((parseResult, ct) => RunSingleRoleAsync(
            "deblur", deblurrer,
            "It comes from RC-Astro: install the rc-astro CLI and license BlurXTerminator, or set RC_ASTRO_CLI.",
            parseResult.Required(inputArg), parseResult.GetValue(outputOpt),
            parseResult.GetValue(formatOpt),
            Math.Clamp(parseResult.GetValue(peakNitsOpt), 1f, 10000f),
            parseResult.GetValue(gamutOpt) == PngPqGamut.Bt2020, ct));
        return cmd;
    }

    private Command BuildDenoiseCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "FITS image to denoise." };
        var outputOpt = new Option<string?>("--output", "-o") { Description = "Output FITS. Default: <input>_denoise.fits." };
        var formatOpt = BuildCompanionFormatOption();
        var peakNitsOpt = BuildPngPqPeakNitsOption();
        var gamutOpt = BuildPngPqGamutOption();

        var cmd = new Command("denoise",
            "Noise reduction (RC-Astro NoiseXTerminator where licensed, else the in-house N2N "
            + "denoiser, whichever serves the role) and nothing else. Inside `sharpen` this runs on the "
            + "STARLESS plate; here it runs on the frame as given, which is what you want when the input "
            + "is already starless or when you are judging the denoiser on its own.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, formatOpt, peakNitsOpt, gamutOpt },
        };
        cmd.SetAction((parseResult, ct) => RunSingleRoleAsync(
            "denoise", denoiser,
            "Register one with AddRcAstroAi() (NoiseXTerminator, else TianWen's own N2N model on colour data) or AddTianWenAi() (the N2N model alone).",
            parseResult.Required(inputArg), parseResult.GetValue(outputOpt),
            parseResult.GetValue(formatOpt),
            Math.Clamp(parseResult.GetValue(peakNitsOpt), 1f, 10000f),
            parseResult.GetValue(gamutOpt) == PngPqGamut.Bt2020, ct));
        return cmd;
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
            Description = "Output FITS path. Default: <input>_sharpened.fits (or per-plate <input>_{starless,stars,sharpened-stars,deconvolved-starless,denoised-starless}.fits when --no-recombine is set).",
        };
        var modeOpt = new Option<string>("--mode")
        {
            Description = "Split/recombine math: 'additive' (default, linear-light correct) or 'screen' (matches NAFNet's stretched-space training identity).",
            DefaultValueFactory = _ => "additive",
        };
        var stellarSharpenOpt = new Option<bool>("--stellar-sharpen")
        {
            Description = "Opt in to a stellar-sharpening pass on the extracted stars (default OFF), where a stellar sharpener is available: none ships today (the SETI Astro one was removed on 2026-09-26), so this is reported and ignored until TianWen's own does. Left off, stars pass through to StarStretch/recombine unmodified. Hard override: when a BlurX deblurrer is live (RC-Astro present) this pass is skipped even if requested, since the BlurX-first flow deblurs whole-frame before star extraction.",
        };
        var noGradientOpt = new Option<bool>("--no-gradient")
        {
            Description = "Skip the gradient correction that heads the canonical flow. It runs by default because "
                + "both canonical programs (SharpenRequest.Canonical / DeblurFirst) include it, and so do the viewer's "
                + "Enhance button and `tianwen stack --enhance`; this verb used to omit it, which is why the same "
                + "master came out differently here than in the viewer. Pass this for a plate already flattened "
                + "elsewhere (GraXpert, Siril, PixInsight DBE), where a second pass has nothing to remove.",
        };
        var noDeconvOpt = new Option<bool>("--no-deconv")
        {
            Description = "Skip the non-stellar deconvolution pass. Starless plate passes through unmodified.",
        };
        var noDenoiseOpt = new Option<bool>("--no-denoise")
        {
            Description = "Skip the noise-reduction pass on the starless plate. By default denoise runs AFTER deconv (PixInsight order) to suppress deconv-amplified grain.",
        };
        var noRecombineOpt = new Option<bool>("--no-recombine")
        {
            Description = "Don't recombine the processed plates. Each plate is written as a separate file (see --output).",
        };
        var formatOpt = OutputFormatOption(
            "2D-viewer companion file alongside each output FITS. 'none' (default) = FITS only. 'png' = 16-bit RGBA + cICP sRGB (SDR display-referred). 'png-pq' = 16-bit RGBA + cICP HDR10 (BT.2020 + PQ); Affinity Photo honours the cICP HDR signal and shows it correctly, but Windows 11 Photos ignores cICP and displays the PQ samples as sRGB (looks muted). 'jxr' = JPEG XR with float-true HDR pixels (BD32F mono / BD16F RGB); writes the post-pipeline plate verbatim, skips Reinhard highlight knee so >1.0 overshoots survive. Per-plate dual-stretch float TIFFs are unaffected.");
        var (pngPqPeakNitsOpt, pngPqGamutOpt) = HdrCompanionOptions();
        var stellarBlendOpt = new Option<float>("--stellar-blend")
        {
            Description = "AI strength for the stellar sharpening pass in [0, 1], applied only when --stellar-sharpen is set. 0 = stars untouched; 1 = full AI output.",
            DefaultValueFactory = _ => 1.0f,
        };
        var deconvBlendOpt = new Option<float>("--deconv-blend")
        {
            Description = "AI strength for the non-stellar deconvolution pass in [0, 1]. 0 = nebula untouched; 1 = full AI output. Nebula usually tolerates higher values than stellar sharpening.",
            DefaultValueFactory = _ => 1.0f,
        };
        var denoiseBlendOpt = new Option<float>("--denoise-blend")
        {
            Description = "AI strength for the denoise pass in [0, 1] (on the starless plate, or on the whole frame when no star remover serves). 0 = noise untouched; 1 = full AI output.",
            DefaultValueFactory = _ => 1.0f,
        };
        var denoiseVariantOpt = new Option<string>("--denoise-variant")
        {
            Description = "Denoise weight bundle: 'default', 'lite' or 'walking'. Only 'default' is served today: RC-Astro NoiseXTerminator has one model, and TianWen's own N2N model has one bundle and refuses the others (the SETI Astro lite and walking bundles were removed on 2026-09-26; a walking-noise model of our own is planned).",
            DefaultValueFactory = _ => "default",
        };
        var scnrOpt = new Option<string>("--scnr")
        {
            Description = "Subtractive Chromatic Noise Reduction on the stars plate only (preserves OIII / H-beta nebula green). Modes: 'none' (default), 'average' = pull G down to (R+B)/2, 'maximum' = pull G down to max(R,B). The starless plate is always untouched.",
            DefaultValueFactory = _ => "none",
        };
        var scnrAmountOpt = new Option<float>("--scnr-amount")
        {
            Description = "SCNR strength in [0, 1]. 1 = full neutralise. Ignored when --scnr is 'none'.",
            DefaultValueFactory = _ => 1.0f,
        };
        var dualStretchOpt = new Option<bool>("--dual-stretch")
        {
            Description = "Apply Frank Sackenheim's dual stretch: fixed-curve StarStretch on the stars plate (amount slider, no auto-targeting) + auto-target MTF on the starless/nebula plate. Output FITS is in stretched space; per-plate stretched float TIFFs (sRGB v4 ICC) are also written for post-processing in Photoshop / Affinity.",
        };
        var stretchStarsAmountOpt = new Option<double>("--stretch-stars-amount")
        {
            Description = "Frank StarStretch amount slider (factor = 3^amount, midtones = 1/(factor+1)). SAS Pro UI default is 5.0 for already-stretched input; on linear stars-only data 1.5-3.0 typically looks balanced. Implies --dual-stretch.",
            DefaultValueFactory = _ => 2.0,
        };
        var stretchStarlessMedianOpt = new Option<double>("--stretch-starless-median")
        {
            Description = "Auto-target MTF median for the starless / nebula plate (0 < tm < 1). 0.25 is the SAS Pro / PixInsight convention. Implies --dual-stretch.",
            DefaultValueFactory = _ => 0.25,
        };
        var starStretchModeOpt = new Option<StarStretchMode>("--star-stretch-mode")
        {
            Description = "Stretch type for the stars-only plate under --dual-stretch. 'starstretch' (default) = Frank Sackenheim's fixed-curve stars stretch (preserves star colour + shape, gentle on highlights, almost always correct). 'mtf' = midtones-balance reusing --stretch-stars-amount as the target. 'ghs' = full GHS chain (--ghs-* family); rarely useful on stars - it pinches cores. Setting this implies --dual-stretch.",
            DefaultValueFactory = _ => StarStretchMode.StarStretch,
        };
        var starlessStretchModeOpt = new Option<StarlessStretchMode>("--starless-stretch-mode")
        {
            Description = "Stretch type for the starless plate under --dual-stretch. 'mtf' (default) = midtones-balance with --stretch-starless-median. 'ghs' = Mike Cranfield's Generalised Hyperbolic Stretch chain (https://github.com/mikec1485/GHS) driven by --ghs-lnd / --ghs-b / --ghs-lp / --ghs-hp / --ghs-sp / --ghs-passes / --ghs-stages / --ghs-target / --ghs-target-value / --ghs-converge. GHS defaults match Paul (Polymath Astro)'s case-1 recipe: LnD=1.30, B=8.0 (hyperbolic), LP=0, HP=0.8, SP=auto (Image.EstimateRisingEdge), passes=1, stages=1. See docs/plans/ghs.md for the curve math. Setting this implies --dual-stretch.",
            DefaultValueFactory = _ => StarlessStretchMode.Mtf,
        };
        var stretchModeOpt = new Option<CombinedStretchMode>("--stretch-mode")
        {
            Description = "Stretch type applied to the recombined (non-split) plate. 'mtf' (default) = midtones-balance with --stretch-starless-median. 'ghs' = GHS chain with the --ghs-* family. Mutually exclusive with --dual-stretch / --star-stretch-mode / --starless-stretch-mode - those control the split workflow and there is no recombined plate to stretch.",
            DefaultValueFactory = _ => CombinedStretchMode.Mtf,
        };
        var ghsConvergeOpt = new Option<GhsConvergeMode>("--ghs-converge")
        {
            Description = "Whether to auto-tune GHS LnD via Image.ConvergeGhsStretchFactor against the input histogram (Auto, default) or apply --ghs-lnd verbatim (Manual). Honoured only when some stretch mode (--star/--starless/--stretch) is set to 'ghs'.",
            DefaultValueFactory = _ => GhsConvergeMode.Auto,
        };
        var ghsLnDOpt = new Option<double>("--ghs-lnd")
        {
            Description = "GHS stretch factor in the PixInsight slider convention - the value the script displays is ln(D + 1); internally D = exp(LnD) - 1. 0 = identity. Default 1.30 = D~2.67. Honoured when --ghs-converge=Manual; ignored when --ghs-converge=Auto (bisection solves LnD instead).",
            DefaultValueFactory = _ => 1.30,
        };
        var ghsBOpt = new Option<double>("--ghs-b")
        {
            Description = "GHS local stretch intensity (signed). B = 8 picks the hyperbolic / harmonic branch (Paul's case-1 default, lifts dim bg); B = -1 picks the logarithmic branch (good for case-2 local contrast on already-stretched input); B = 0 exponential; B < 0, != -1 power-with-negative-B. Larger |B| = more focused stretch around SP. Default 8.0. Honoured when --starless-stretch-mode=Ghs (or --stretch-mode=Ghs).",
            DefaultValueFactory = _ => 8.0,
        };
        var ghsLpOpt = new Option<double>("--ghs-lp")
        {
            Description = "GHS shadow protection point in [0, SP]. Below LP the curve is linear at the gradient evaluated at LP - preserves shadow texture. Default 0. Honoured when --starless-stretch-mode=Ghs (or --stretch-mode=Ghs).",
            DefaultValueFactory = _ => 0.0,
        };
        var ghsHpOpt = new Option<double>("--ghs-hp")
        {
            Description = "GHS highlight protection point in [SP, 1]. Above HP the curve is linear at the gradient evaluated at HP - prevents the upper tail from being compressed. Default 0.8 (Paul's recommendation). Honoured when --starless-stretch-mode=Ghs (or --stretch-mode=Ghs).",
            DefaultValueFactory = _ => 0.8,
        };
        var ghsSpOpt = new Option<double>("--ghs-sp")
        {
            Description = "GHS symmetry point - the input pixel value where the curve has maximum gradient (its inflection point). Pass a value in (0, 1) to override; default <=0 means auto-detect via Image.EstimateRisingEdge (histogram lift-off). Honoured when --starless-stretch-mode=Ghs (or --stretch-mode=Ghs).",
            DefaultValueFactory = _ => -1.0,
        };
        var ghsPassesOpt = new Option<int>("--ghs-passes")
        {
            Description = "How many times to apply the GHS curve. Default 1. Range [1, 10].",
            DefaultValueFactory = _ => 1,
        };
        var ghsAutoTargetValueOpt = new Option<double>("--ghs-target-value")
        {
            Description = "Target post-stretch value for --ghs-converge=Auto. Interpreted as median (PixInsight STF default) or as the bg-peak mode depending on --ghs-target. Default 0.25 (SAS Pro / PixInsight statistical-stretch convention). Honoured only when --ghs-converge=Auto.",
            DefaultValueFactory = _ => 0.25,
        };
        var ghsStagesOpt = new Option<int>("--ghs-stages")
        {
            Description = "Number of distinct GHS stages in the canonical Cranfield chain (gh-astro doc 2.7-2.9). 1 (default) = single GHS pass with caller's --ghs-* params. 2 = pass 1 -> BackgroundReduceStep ('linear prestretch') -> pass 2 (B=2.5, HP=0.95, LP=0, SP=auto, auto-converge on same target - redistributes contrast and pushes signal toward highlights). 3 = stages 2 + pass 3 (B=-1 log branch, HP=0.99, LP=0, SP=auto, LnD=0.5 fixed, no auto-converge - highlight refinement per case-2 recipe). Stages >= 2 force an implicit BackgroundReduceStep between passes 1 and 2 regardless of --no-reduce-bg. Implies --ghs-starless != off + --dual-stretch.",
            DefaultValueFactory = _ => 1,
        };
        var ghsAutoTargetOpt = new Option<Image.GhsConvergeTarget>("--ghs-target")
        {
            Description = "Which post-stretch metric --ghs-converge=Auto bisects against. 'median' is the PixInsight STF default; 'mode' targets the bg peak (Paul / Polymath Astro's recipe - lifts the histogram peak to ~0.25 instead of converging the median to it). Mode-target produces a visibly brighter result on typical linear astro frames because the median sits well above the mode (long signal tail). Default median for back-compat. Honoured only when --ghs-converge=Auto.",
            DefaultValueFactory = _ => Image.GhsConvergeTarget.Median,
        };
        var asinhBetaOpt = new Option<double>("--asinh-beta")
        {
            Description = "Stretch strength for Siril-style asinh stretches (Siril's 'stretch' parameter). Range [1, 1000]. Larger = more aggressive lift. Honoured when any --*-stretch-mode=Asinh. Default 10 - a moderate lift; linear stars-only plates usually want 10-50, already-stretched starless 3-10.",
            DefaultValueFactory = _ => 10.0,
        };
        var asinhBlackPointOpt = new Option<double>("--asinh-black-point")
        {
            Description = "Black-point subtracted from each channel before the asinh-scaled output. 0 (default) is correct for stars-only plates (the bg has already been subtracted by RemoveStarsStep) or for any plate that's already been background-neutralised. Use the post-stretch bg peak when feeding data with a pedestal. Range [0, 1). Honoured when any --*-stretch-mode=Asinh.",
            DefaultValueFactory = _ => 0.0,
        };
        var asinhLumaOpt = new Option<LumaWeighting>("--asinh-luma")
        {
            Description = "Luma weighting profile for the colour asinh formula. Rec.709 (default) matches the rest of the stretch pipeline. Rec.601 / Rec.2020 cover NTSC / wide-gamut workflows. SensorMatched resolves to per-sensor QE x CFA weights via FilterCurveDatabase. Honoured when any --*-stretch-mode=Asinh on a multi-channel image.",
            DefaultValueFactory = _ => LumaWeighting.Rec709,
        };
        var noReduceBgOpt = new Option<bool>("--no-reduce-bg")
        {
            Description = "Skip the S-curve background reduction on the starless plate. By default --dual-stretch applies a Compression=0.36 reduce-background curve (matches finished Affinity workflow control point at ~0.112,0.04).",
        };
        var reduceBgCompressionOpt = new Option<double>("--reduce-bg-compression")
        {
            Description = "Background reduction strength: low control point Y = bg_peak * compression. 0.36 default matches Affinity finished-work measurements; lower = more aggressive shadow crush. Range (0, 1].",
            DefaultValueFactory = _ => 0.36,
        };
        var noCompressHighlightsOpt = new Option<bool>("--no-compress-highlights")
        {
            Description = "Skip the Reinhard-style soft highlight compression on the starless plate. By default --dual-stretch applies Knee=0.7, Amount=1.0 to tame the central-nebula core that would otherwise blow out after the dual stretch.",
        };
        var highlightKneeOpt = new Option<double>("--highlight-knee")
        {
            Description = "Threshold above which highlight compression starts (below = identity). Default 0.7. Range (0, 1).",
            DefaultValueFactory = _ => 0.7,
        };
        var highlightAmountOpt = new Option<double>("--highlight-amount")
        {
            Description = "Highlight compression strength; higher = stronger roll-off, more headroom recovered above the knee. Default 1.0 (v=1.0 maps to knee + (1-knee)/2). Range >= 0.",
            DefaultValueFactory = _ => 1.0,
        };

        // AI backend selection + per-role strength overrides (Phase 3a; flags renamed
        // backend-neutral when the n2n lane made them serve more than RC-Astro).
        var aiBackendOpt = new Option<string>("--ai-backend")
        {
            Description = "AI enhancer backend for the RC-servable roles (star removal / deblur / deconvolution / denoise): 'auto' (RC-Astro when present + licensed, else TianWen's own model where the role has one - default), 'rc' (force RC-Astro whenever the CLI is installed, skipping the license probe), or 'n2n' (the in-house TianWen Noise2Noise model for the denoise step - OSC-only, ships with the repo; other roles behave as auto). A role nothing serves is left out: with no star remover the program runs whole-frame. No effect on gradient correction (GraXpert, else the classical fit). 'sas' was removed on 2026-09-26.",
            DefaultValueFactory = _ => "auto",
        };
        var deblurSharpenOpt = new Option<double>("--deblur-sharpen")
        {
            Description = "Non-stellar deblur/deconvolution sharpen in [0, 1], applied to the full-image deblur and the starless deconvolution. RC-Astro maps it to BlurXTerminator's bxt --sn; < 0 (default) = the enhancer's own default (0.90). Other backends ignore it.",
            DefaultValueFactory = _ => -1.0,
        };
        var denoiseStrengthOpt = new Option<double>("--denoise-strength")
        {
            Description = "Denoise strength in [0, 1]. RC-Astro maps it to NoiseXTerminator's nxt --dn (< 0, the default, = noise-adaptive auto); the n2n backend maps it to its blend dial (out = in + s*(den - in), default 1.0). The SAS backend ignores it.",
            DefaultValueFactory = _ => -1.0,
        };
        var denoiseIterationsOpt = new Option<int>("--denoise-iterations")
        {
            Description = "Denoiser iterations. RC-Astro maps it to NoiseXTerminator's nxt --it; < 1 (default) = the enhancer's own default (2). Other backends ignore it.",
            DefaultValueFactory = _ => 0,
        };

        var cmd = new Command("sharpen", "The canonical AI enhance: with a star remover (RC-Astro StarXTerminator), deblur, gradient, remove stars, denoise (and deconvolve) the starless plate, optional SCNR on stars, recombine; without one, whole-frame gradient correction and denoise. Every step runs only where a backend serves it.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, modeOpt, stellarSharpenOpt, noGradientOpt, noDeconvOpt, noDenoiseOpt, noRecombineOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt, stellarBlendOpt, deconvBlendOpt, denoiseBlendOpt, denoiseVariantOpt, scnrOpt, scnrAmountOpt, dualStretchOpt, stretchStarsAmountOpt, stretchStarlessMedianOpt, starStretchModeOpt, starlessStretchModeOpt, stretchModeOpt, ghsConvergeOpt, ghsLnDOpt, ghsBOpt, ghsLpOpt, ghsHpOpt, ghsSpOpt, ghsPassesOpt, ghsStagesOpt, ghsAutoTargetValueOpt, ghsAutoTargetOpt, asinhBetaOpt, asinhBlackPointOpt, asinhLumaOpt, noReduceBgOpt, reduceBgCompressionOpt, noCompressHighlightsOpt, highlightKneeOpt, highlightAmountOpt, aiBackendOpt, deblurSharpenOpt, denoiseStrengthOpt, denoiseIterationsOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.Required(inputArg);
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

            var deblurSharpen = parseResult.GetValue(deblurSharpenOpt);
            var denoiseStrength = parseResult.GetValue(denoiseStrengthOpt);
            var denoiseIterations = parseResult.GetValue(denoiseIterationsOpt);
            // Backend + per-product tuning parse is shared with `stack --enhance` and the server
            // enhance endpoint via EnhanceOptions.TryParse (single source of truth). CLI sentinels
            // (-1 / 0 = "unset") map to a null override before the call.
            if (!EnhanceOptions.TryParse(
                    parseResult.GetValue(aiBackendOpt),
                    deblurSharpen >= 0 ? (float)deblurSharpen : null,
                    denoiseStrength >= 0 ? (float)denoiseStrength : null,
                    denoiseIterations >= 1 ? denoiseIterations : null,
                    out var enhanceOptions, out var enhanceError))
            {
                consoleHost.WriteError(enhanceError);
                return 1;
            }
            var backend = enhanceOptions.Backend;
            var tuning = enhanceOptions.Tuning;

            var stellarOptIn = parseResult.GetValue(stellarSharpenOpt);
            var noGradient = parseResult.GetValue(noGradientOpt);
            // Whether the caller ASKED for the starless deconvolution, as opposed to leaving it at its
            // default: with BlurX live the canonical program drops it, so only an explicit blend keeps it.
            var deconvExplicit = parseResult.GetResult(deconvBlendOpt)?.Tokens.Count > 0;
            var noDeconv = parseResult.GetValue(noDeconvOpt);
            var noDenoise = parseResult.GetValue(noDenoiseOpt);
            var noRecombine = parseResult.GetValue(noRecombineOpt);
            var outputPath = parseResult.GetValue(outputOpt);
            var stellarBlend = Math.Clamp(parseResult.GetValue(stellarBlendOpt), 0f, 1f);
            var deconvBlend = Math.Clamp(parseResult.GetValue(deconvBlendOpt), 0f, 1f);
            var denoiseBlend = Math.Clamp(parseResult.GetValue(denoiseBlendOpt), 0f, 1f);

            var scnrStr = (parseResult.GetValue(scnrOpt) ?? "none").ToLowerInvariant();
            ScnrMode scnrMode = scnrStr switch
            {
                "none" => ScnrMode.None,
                "average" => ScnrMode.Average,
                "maximum" or "max" => ScnrMode.Maximum,
                _ => (ScnrMode)(-1),
            };
            if ((int)scnrMode < 0)
            {
                consoleHost.WriteError($"--scnr must be 'none', 'average', or 'maximum', got '{scnrStr}'");
                return 1;
            }
            var scnrAmount = Math.Clamp(parseResult.GetValue(scnrAmountOpt), 0f, 1f);
            // Output format is read up-front because the step-list
            // construction below conditions on it: JXR opts the pipeline
            // into true-HDR semantics (skip the Reinhard highlight knee
            // so >1.0 cores are preserved instead of being compressed
            // back into [0, 1]).
            var format = parseResult.GetValue(formatOpt);
            var pngPqPeakNits = Math.Clamp(parseResult.GetValue(pngPqPeakNitsOpt), 1f, 10000f);
            var pngPqGamut = parseResult.GetValue(pngPqGamutOpt);
            var gamutToBt2020 = pngPqGamut == PngPqGamut.Bt2020;
            var denoiseVariantStr = (parseResult.GetValue(denoiseVariantOpt) ?? "default").ToLowerInvariant();
            DenoiseVariant denoiseVariant = denoiseVariantStr switch
            {
                "default" or "full" => DenoiseVariant.Default,
                "lite" => DenoiseVariant.Lite,
                "walking" or "walk" => DenoiseVariant.Walking,
                _ => (DenoiseVariant)(-1),
            };
            if ((int)denoiseVariant < 0)
            {
                consoleHost.WriteError($"--denoise-variant must be 'default', 'lite', or 'walking'; got '{denoiseVariantStr}'");
                return 1;
            }

            // Dual stretch: gated on --dual-stretch. Star plate uses Frank's
            // fixed-curve StarStretch (amount slider); starless plate uses
            // auto-target MTF (Frank's convention, target_median = 0.25).
            // Per-plate stretch flags (--star-stretch-mode / --starless-stretch-mode)
            // imply --dual-stretch; --stretch-mode is mutually exclusive with it.
            var dualStretchFlag = parseResult.GetValue(dualStretchOpt);
            var starsAmount = Math.Clamp(parseResult.GetValue(stretchStarsAmountOpt), 0.1, 10.0);
            var starlessMedian = Math.Clamp(parseResult.GetValue(stretchStarlessMedianOpt), 0.01, 0.99);
            var noReduceBg = parseResult.GetValue(noReduceBgOpt);
            var reduceBgCompression = Math.Clamp(parseResult.GetValue(reduceBgCompressionOpt), 0.01, 1.0);
            // Per-plate stretch modes. Detect "user explicitly provided" via
            // OptionResult.Tokens -- needed to distinguish "user accepted the
            // default" from "user didn't mention the flag at all", which in
            // turn drives the --dual-stretch implication.
            var starStretchMode = parseResult.GetValue(starStretchModeOpt);
            var starlessStretchMode = parseResult.GetValue(starlessStretchModeOpt);
            var combinedStretchMode = parseResult.GetValue(stretchModeOpt);
            var starModeExplicit = parseResult.GetResult(starStretchModeOpt)?.Tokens.Count > 0;
            var starlessModeExplicit = parseResult.GetResult(starlessStretchModeOpt)?.Tokens.Count > 0;
            var combinedModeExplicit = parseResult.GetResult(stretchModeOpt)?.Tokens.Count > 0;
            var ghsConverge = parseResult.GetValue(ghsConvergeOpt);

            // Resolve dual-stretch + GHS effective flags from the new mode triple.
            // Rule: per-plate (--star-stretch-mode / --starless-stretch-mode)
            // implies --dual-stretch; --stretch-mode is incompatible with split.
            var dualStretch = dualStretchFlag || starModeExplicit || starlessModeExplicit;
            if (combinedModeExplicit && dualStretch)
            {
                consoleHost.WriteError(
                    "--stretch-mode applies to the recombined plate and is mutually exclusive with --dual-stretch / --star-stretch-mode / --starless-stretch-mode.");
                return 1;
            }
            // --stretch-mode without --dual-stretch is now wired via
            // MtfStretchFinalStep / GhsStretchFinalStep -- the step list
            // construction below adds the post-recombine stretch when
            // combinedModeExplicit is set.
            // --star-stretch-mode StarStretch + Asinh are both wired; MTF/GHS
            // were removed from the enum since gh-astro doesn't propose a GHS
            // recipe for stars-only plates and MTF on stars doesn't beat
            // StarStretch + LumaBlend.
            // GHS effective flags: starless-plate mode == ghs turns on the
            // GHS codepath. Manual vs auto convergence is the separate
            // --ghs-converge axis.
            var ghsStarless = starlessStretchMode == StarlessStretchMode.Ghs;
            var ghsAuto = ghsConverge == GhsConvergeMode.Auto;
            var asinhBeta = Math.Clamp(parseResult.GetValue(asinhBetaOpt), 1.0, 1000.0);
            var asinhBlackPoint = Math.Clamp(parseResult.GetValue(asinhBlackPointOpt), 0.0, 0.999);
            var asinhLuma = parseResult.GetValue(asinhLumaOpt);
            var ghsLnD = Math.Max(0.0, parseResult.GetValue(ghsLnDOpt));
            // B is signed -- no clamp; the four-branch math handles any
            // finite double (B == -1, B < 0, B == 0, B > 0).
            var ghsB = parseResult.GetValue(ghsBOpt);
            var ghsLp = Math.Clamp(parseResult.GetValue(ghsLpOpt), 0.0, 1.0);
            var ghsHp = Math.Clamp(parseResult.GetValue(ghsHpOpt), 0.0, 1.0);
            var ghsSpRaw = parseResult.GetValue(ghsSpOpt);
            // Sentinel <= 0 (default) means auto-detect via EstimateRisingEdge at pipeline time.
            double? ghsSp = ghsSpRaw > 0.0
                ? Math.Clamp(ghsSpRaw, 0.01, 0.99)
                : null;
            // --ghs-target-value is the post-stretch target (interpretation
            // depends on --ghs-target: median or bg-peak mode). Only
            // honoured when --ghs-starless=auto.
            var ghsAutoTargetValue = Math.Clamp(parseResult.GetValue(ghsAutoTargetValueOpt), 0.01, 0.99);
            var ghsAutoTarget = parseResult.GetValue(ghsAutoTargetOpt);
            var ghsPasses = Math.Clamp(parseResult.GetValue(ghsPassesOpt), 1, 10);
            var ghsStages = Math.Clamp(parseResult.GetValue(ghsStagesOpt), 1, 3);
            var noCompressHighlights = parseResult.GetValue(noCompressHighlightsOpt);
            var highlightKnee = Math.Clamp(parseResult.GetValue(highlightKneeOpt), 0.01, 0.99);
            var highlightAmount = Math.Max(0.0, parseResult.GetValue(highlightAmountOpt));

            if (!Image.TryReadFitsFile(input, out var src, out var wcs))
            {
                consoleHost.WriteError($"Failed to read FITS file: {input}");
                return 1;
            }

            // AI enhancers require [0, 1]. ScaleFloatValuesToUnit is a no-op
            // when MaxValue <= 1 (= already normalised) and otherwise produces
            // a fresh copy at unit range. Original `src` is unchanged.
            var normalised = src.ScaleFloatValuesToUnit();


            // What can serve THIS image under THESE options sets the program's shape (the RC-Astro
            // licence probe runs here, at the first enhance, never at DI build): the split program with
            // a star remover, BlurX-first where a deblurrer serves, and the whole-frame program
            // (gradient, then denoise the frame) where no star remover does.
            var capabilities = sharpenPipeline.CapabilitiesFor(normalised, enhanceOptions);
            var deblurLive = capabilities.Deblur;
            if (!capabilities.StarRemoval && (dualStretch || noRecombine))
            {
                consoleHost.WriteError(
                    "--dual-stretch, --star-stretch-mode, --starless-stretch-mode and --no-recombine work on the split star and starless plates, " +
                    "and no star remover serves this image (star removal is RC-Astro StarXTerminator until TianWen's own ships).");
                return 1;
            }

            // Stellar-sharpen is opt-in (default OFF), and only where a stellar sharpener serves: none
            // ships today (the SETI Astro one went with the SAS tier on 2026-09-26, and it hardened
            // bright cores into square white blocks, ~89k clipped px measured on a dense field). Hard
            // override: when a BlurX deblurrer is live it is skipped even if opted in -- the BlurX-first
            // (PixInsight OSC) flow deblurs whole-frame before star extraction, so re-sharpening the
            // split stars double-dips.
            var doStellar = stellarOptIn && !deblurLive && capabilities.StellarSharpen && capabilities.StarRemoval;
            if (stellarOptIn && deblurLive)
            {
                consoleHost.WriteScrollable(
                    "[sharpen] --stellar-sharpen ignored: BlurX deblurrer live (deblur is whole-frame upstream; re-sharpening extracted stars over-sharpens).");
            }
            else if (stellarOptIn && !doStellar)
            {
                consoleHost.WriteScrollable(
                    "[sharpen] --stellar-sharpen ignored: no stellar sharpener is available (none ships today; RC-Astro BlurXTerminator's deblur tightens stars).");
            }

            // SCNR on the stars plate follows whichever canonical program applies, and only an
            // explicit --scnr overrides it. SharpenRequest.DeblurFirst carries ScnrStarsStep
            // (Average) and SharpenRequest.Canonical does not, which is the split honoured here:
            // BlurX tightens every star to near the sampling limit, and the faint ones then carry a
            // green fringe where the G channel outpaces R and B, so the BlurX-first flow neutralises
            // it and the split program without a deblurrer has nothing to neutralise. --dual-stretch keeps its own
            // reason on top: green stars are a stretched-space artefact, so a program that stretches
            // in-pipeline wants SCNR whichever deblurrer is live.
            var scnrExplicit = parseResult.GetResult(scnrOpt)?.Tokens.Count > 0;
            var effectiveScnrMode = scnrExplicit ? scnrMode
                : deblurLive || dualStretch ? ScnrMode.Average
                : ScnrMode.None;
            if (scnrExplicit && !capabilities.StarRemoval && scnrMode != ScnrMode.None)
            {
                consoleHost.WriteScrollable(
                    "[sharpen] --scnr ignored: SCNR works on the stars plate, and with no star remover the program is whole-frame.");
            }

            // THE STEP ORDER IS NOT WRITTEN HERE. LinearEnhanceProgram.For is the one place it
            // lives, and the viewer's Enhance button, the hosted endpoint and MasterPostProcessor
            // all read it from there too; this verb only says which steps its flags keep and at
            // what blend. It used to carry a hand-assembled list that began at RemoveStarsStep, so
            // `tianwen image sharpen` ran neither the whole-frame deblur nor the gradient
            // correction the other callers have always run: the same master enhanced in the viewer
            // and on the command line came out visibly different, an uncorrected background with
            // its colour cast intact, and nothing said so.
            //
            // Deviations from the canonical defaults, each with its own reason (and none of them can
            // switch on a step no role serves: every step flag is ANDed with its capability):
            //  - StellarSharpen is opt-in here, where a sharpener serves at all (see above).
            //  - DeconvolveStarless survives a live deblurrer only if asked for outright.
            //  - Scnr follows whichever canonical program applies unless --scnr overrides it, and
            //    --dual-stretch wants it either way (green stars are a stretched-space artefact).
            //  - Recombine is cleared by --no-recombine, which writes each plate separately.
            var program = LinearEnhanceProgram.For(capabilities) with
            {
                // Blend left at the step default, as the canonical program has it; --deblur-sharpen
                // tunes RC-Astro's own sharpening through EnhanceOptions and is a different dial.
                GradientCorrection = !noGradient && capabilities.GradientCorrection,
                SplitMode = mode,
                StellarSharpen = doStellar,
                StellarBlend = stellarBlend,
                DeconvolveStarless = !noDeconv && capabilities.StarRemoval && capabilities.Deconvolve && (!deblurLive || deconvExplicit),
                DeconvolveBlend = deconvBlend,
                Denoise = !noDenoise && capabilities.Denoise,
                DenoiseBlend = denoiseBlend,
                DenoiseVariant = denoiseVariant,
                Scnr = effectiveScnrMode,
                ScnrAmount = scnrAmount,
                Recombine = !noRecombine,
                // Dual-stretch produces plates already in [0, 1] stretched space. Additive sum
                // saturates at 1.0; screen is the natural bounded composite:
                // Final = 1 - (1-bg)(1-fg). Split stays in linear (mode controls that).
                RecombineMode = dualStretch ? RecombineMode.Screen : mode,
            };

            // The plate steps, then this verb's own per-plate stretch, then the composite. The
            // stretch's PLACEMENT is the CLI's call (SCNR after it, PixInsight convention); the
            // order of everything around it comes from the program above.
            var steps = new List<SharpenStep>(program.PlateSteps);
            // Per-plate stretch (linear -> stretched) AFTER all AI ops.
            if (dualStretch)
            {
                // Stars-plate selector: StarStretch (Frank's fixed curve)
                // or Asinh (Siril's chrominance-preserving asinh).
                if (starStretchMode == StarStretchMode.Asinh)
                {
                    steps.Add(new AsinhStretchStarsStep(
                        Beta: asinhBeta,
                        BlackPoint: asinhBlackPoint,
                        LumaWeights: asinhLuma));
                }
                else
                {
                    steps.Add(new StretchStarsStep(Amount: starsAmount));
                }
                // Pick MTF or GHS for the starless plate based on --ghs-starless.
                // GHS + --ghs-twopass: split bg-reduce between two GHS passes
                // (Cranfield's canonical recipe -- gh-astro sections 2.7-2.9):
                //   pass 1 (user params)  ->  bg-reduce  ->  pass 2 (B=2.5, HP=0.95).
                // Single-pass GHS or MTF: bg-reduce comes after the lone stretch.
                if (ghsStarless)
                {
                    // Stage 1 (always): caller-supplied params.
                    steps.Add(new GhsStretchStarlessStep(
                        LnD: ghsLnD,
                        B: ghsB,
                        SP: ghsSp,
                        LP: ghsLp,
                        HP: ghsHp,
                        Passes: ghsPasses,
                        AutoConverge: ghsAuto,
                        AutoTargetValue: ghsAutoTargetValue,
                        AutoTarget: ghsAutoTarget));
                    if (ghsStages >= 2)
                    {
                        // The "linear prestretch" / blackpoint clip lives between
                        // passes per the canonical doc. Forced on regardless of
                        // --no-reduce-bg; without it stage 2 would just re-flatten
                        // stage 1's lift. Auto bg-peak detect post-stage-1.
                        steps.Add(new BackgroundReduceStep(Compression: reduceBgCompression));
                        // Stage 2: lower B (less concentrated curve), higher HP
                        // (avoid double rolloff -- stage 1 already shaped highlights),
                        // SP auto-detect on the now-stretched-and-bg-reduced plate,
                        // LP=0, same auto-converge target as stage 1 so the bg peak
                        // lands back at the requested value after the clip pulled it
                        // down. Passes=1 -- chaining stages with multi-pass per-step
                        // is not a documented recipe.
                        steps.Add(new GhsStretchStarlessStep(
                            LnD: 0.5,
                            B: 2.5,
                            SP: null,
                            LP: 0.0,
                            HP: 0.95,
                            Passes: 1,
                            AutoConverge: ghsAuto,
                            AutoTargetValue: ghsAutoTargetValue,
                            AutoTarget: ghsAutoTarget));
                    }
                    else if (!noReduceBg)
                    {
                        // Single-stage GHS: bg-reduce after the stretch
                        // (statistical-stretch convention).
                        steps.Add(new BackgroundReduceStep(Compression: reduceBgCompression));
                    }
                    if (ghsStages >= 3)
                    {
                        // Stage 3: highlight refinement per case-2 (local contrast
                        // on already-stretched data). B = -1 picks the logarithmic
                        // branch, SP auto-detects to the new mid-tone position
                        // (where the histogram is densest post-stage-2), HP = 0.99
                        // keeps the highlight cap effectively off. Fixed small LnD
                        // (0.5) -- this stage isn't an auto-converged bg lift, it's
                        // a small shaped curve added on top, so we don't bisect.
                        // No bg-reduce between stages 2 and 3 -- the doc only
                        // mentions one linear prestretch between stages 1 and 2.
                        steps.Add(new GhsStretchStarlessStep(
                            LnD: 0.5,
                            B: -1.0,
                            SP: null,
                            LP: 0.0,
                            HP: 0.99,
                            Passes: 1,
                            AutoConverge: false,
                            AutoTargetValue: ghsAutoTargetValue,
                            AutoTarget: ghsAutoTarget));
                    }
                }
                else if (starlessStretchMode == StarlessStretchMode.Asinh)
                {
                    steps.Add(new AsinhStretchStarlessStep(
                        Beta: asinhBeta,
                        BlackPoint: asinhBlackPoint,
                        LumaWeights: asinhLuma));
                    if (!noReduceBg) steps.Add(new BackgroundReduceStep(Compression: reduceBgCompression));
                }
                else
                {
                    steps.Add(new StretchStarlessStep(TargetMedian: starlessMedian));
                    // MTF: bg-reduce after the stretch (statistical-stretch
                    // convention). Auto-detects bg peak via histogram mode.
                    if (!noReduceBg) steps.Add(new BackgroundReduceStep(Compression: reduceBgCompression));
                }
                // Reinhard-style soft highlight compression on the same
                // starless plate -- prevents the central-nebula core from
                // blowing out after the dual-stretch. Asymmetric companion
                // to the bg-reduce step; together they reproduce the SAS Pro
                // statistical-stretch shape.
                // Skip Reinhard for any HDR-preserving output format. Reinhard's
                // whole purpose is to bring >1.0 cores back into [0, 1] for SDR
                // display, which defeats HDR intent for both:
                //   * Jxr  -- float container, preserves overshoots verbatim
                //   * PngPq -- 16-bit PQ-coded PNG; the high-luminance PQ codes
                //              expand the [0, 1] PQ-input range across the
                //              perceptual brightness curve, so a bright core
                //              landing at the PQ peak displays at HDR-peak nits.
                //              Reinhard would compress that back into the same
                //              SDR-ish range as the plain Png path.
                // The gamut-preserving max-channel scale in the float-to-ushort
                // quantizers (WriteStretchedPngAsync / RenderStretchedRgba* )
                // catches the overshoots without per-channel hue-skew.
                var isHdrFormat = format == ImageOutputFormat.Jxr || format == ImageOutputFormat.PngPq;
                if (!noCompressHighlights && !isHdrFormat)
                    steps.Add(new CompressHighlightsStep(Knee: highlightKnee, Amount: highlightAmount));
            }
            // SCNR AFTER the stretch -- PixInsight convention. Green stars
            // are a stretched-space artefact (faint noise floor amplified
            // where the G channel slightly outpaces R/B), so neutralising
            // in stretched space has the highest visible benefit. That is why
            // the composite half of the program is appended HERE and not with
            // the plate steps above.
            steps.AddRange(program.CompositeSteps);
            var recombineMode = program.RecombineMode;
            if (!noRecombine)
            {
                // --stretch-mode operates on the recombined `final` plate.
                // Honoured only when no dual-stretch (per the validation above);
                // adds either MtfStretchFinalStep or GhsStretchFinalStep after
                // RecombineStep. The full --ghs-* knob set still drives the
                // GHS variant; --ghs-stages multi-stage is NOT applied to
                // final yet (single pass only -- multi-stage post-recombine
                // would need a "linear prestretch" step that operates on
                // `final` too, which we haven't wired).
                if (combinedModeExplicit && combinedStretchMode == CombinedStretchMode.Mtf)
                {
                    steps.Add(new MtfStretchFinalStep(TargetMedian: starlessMedian));
                }
                else if (combinedModeExplicit && combinedStretchMode == CombinedStretchMode.Ghs)
                {
                    steps.Add(new GhsStretchFinalStep(
                        LnD: ghsLnD,
                        B: ghsB,
                        SP: ghsSp,
                        LP: ghsLp,
                        HP: ghsHp,
                        Passes: ghsPasses,
                        AutoConverge: ghsAuto,
                        AutoTargetValue: ghsAutoTargetValue,
                        AutoTarget: ghsAutoTarget));
                }
                else if (combinedModeExplicit && combinedStretchMode == CombinedStretchMode.Asinh)
                {
                    steps.Add(new AsinhStretchFinalStep(
                        Beta: asinhBeta,
                        BlackPoint: asinhBlackPoint,
                        LumaWeights: asinhLuma));
                }
            }

            // Each CLI mode tells the pipeline exactly which plates it'll read
            // off SharpenResult. The pipeline releases everything else as soon
            // as the downstream chain has consumed it -- trims peak memory
            // from ~8 plates to ~3 (canonical recombine) or ~5 (dual-stretch)
            // for a 3k drizzle. See SharpenRequest.KeepIntermediates xmldoc.
            //   --no-recombine  -> writes each plate as a separate FITS, needs them all.
            //   --dual-stretch  -> reads per-plate stretched TIFFs from
            //                       sharpenedStars (or starsOnly fallback) +
            //                       denoisedStarless (or deconv/raw fallback) --
            //                       gradient-corrected is not needed.
            //   else            -> composite only; release every intermediate.
            var keepIntermediates =
                  noRecombine  ? SharpenIntermediates.All
                : dualStretch  ? SharpenIntermediates.StarsAndStarlessLineage
                :                SharpenIntermediates.None;
            var request = new SharpenRequest(normalised, ImmutableArray.CreateRange(steps), KeepIntermediates: keepIntermediates);

            var spDesc = ghsSp is { } spv ? spv.ToString("F3") : "auto";
            var targetLabel = ghsAutoTarget == Image.GhsConvergeTarget.Mode ? "mode" : "med";
            var lnDDesc = ghsAuto ? $"lnD~auto({targetLabel}={ghsAutoTargetValue:F2})" : $"lnD{ghsLnD:F2}";
            var stagesSuffix = ghsStages switch
            {
                2 => "+s2(b2.5/hp0.95)",
                3 => "+s2(b2.5/hp0.95)+s3(b-1/hp0.99)",
                _ => "",
            };
            var starlessStretchDesc = ghsStarless
                ? $"ghs({lnDDesc}/b{ghsB:F2}/sp{spDesc}/lp{ghsLp:F2}/hp{ghsHp:F2}/{ghsPasses}x{stagesSuffix})"
                : $"mtf-tm={starlessMedian:F2}";
            var dualStretchDesc = dualStretch
                ? $" dual-stretch(stars-amount={starsAmount:F2},starless={starlessStretchDesc}) reduce-bg={(!noReduceBg ? reduceBgCompression.ToString("F2") : "off")} compress-hi={(!noCompressHighlights ? $"k{highlightKnee:F2}/a{highlightAmount:F2}" : "off")}"
                : "";
            consoleHost.WriteScrollable(
                $"[sharpen] {input} {src.Width}x{src.Height}x{src.ChannelCount} mode={mode} " +
                $"stellar={doStellar}({stellarBlend:F2}) deconv={!noDeconv}({deconvBlend:F2}) " +
                $"denoise={!noDenoise}({denoiseBlend:F2},{denoiseVariant}) scnr={effectiveScnrMode}({scnrAmount:F2}){dualStretchDesc} recombine={!noRecombine} backend={backend}{(tuning is null ? "" : $" tuning(sn={tuning.DeblurSharpen?.ToString("F2") ?? "-"},dn={tuning.DenoiseStrength?.ToString("F2") ?? "-"},it={tuning.DenoiseIterations?.ToString() ?? "-"})")}");

            SharpenResult result;
            try
            {
                // Per-step progress to the console (step transitions + RC-Astro sub-step %).
                var enhanceProgress = EnhanceProgressConsole.Create(consoleHost, "[sharpen]");
                result = await sharpenPipeline.ProcessAsync(request, enhanceOptions, enhanceProgress, ct);
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
                await WritePlateAsync(result.Starless, basePath, "_starless", wcs, src.ImageMeta, format, pngPqPeakNits, gamutToBt2020, ct);
                await WritePlateAsync(result.StarsOnly, basePath, "_stars", wcs, src.ImageMeta, format, pngPqPeakNits, gamutToBt2020, ct);
                await WritePlateAsync(result.SharpenedStars, basePath, "_sharpened-stars", wcs, src.ImageMeta, format, pngPqPeakNits, gamutToBt2020, ct);
                await WritePlateAsync(result.DeconvolvedStarless, basePath, "_deconvolved-starless", wcs, src.ImageMeta, format, pngPqPeakNits, gamutToBt2020, ct);
                await WritePlateAsync(result.DenoisedStarless, basePath, "_denoised-starless", wcs, src.ImageMeta, format, pngPqPeakNits, gamutToBt2020, ct);
            }
            else if (result.Final is { } finalImage)
            {
                var dst = EnsureFitsExtension(outputPath ?? DefaultOut(input, "_sharpened"));
                finalImage.WriteToFitsFile(dst, wcs, SharpenPipeline.SwModifyHeader());
                consoleHost.WriteScrollable($"[sharpen] wrote {dst}");
                // For dual-stretch: composite is already in stretched [0, 1]
                // space (per-plate MTF + screen recombine), so MasterPreviewRenderer
                // would auto-MTF again (double-stretch). useStretchedPng = true
                // takes the byte-encode path instead. JXR ignores the flag and
                // always preserves floats verbatim.
                await WriteCompanionAsync(finalImage, dst, format, src.ImageMeta, wcs, "sharpen",
                    useStretchedPng: dualStretch, peakNits: pngPqPeakNits, gamutToBt2020: gamutToBt2020, ct: ct);
            }

            // Dual-stretch: also write per-plate stretched float TIFFs for
            // post-processing in Photoshop / Affinity (top layer + Screen
            // blend mode reproduces the in-pipeline screen recombine). sRGB
            // v4 ICC tagged so colour-managed viewers display correctly.
            if (dualStretch)
            {
                var tiffBase = outputPath is null
                    ? StripExtension(input)
                    : StripExtension(outputPath);
                var stretchedStars = result.SharpenedStars ?? result.StarsOnly;
                var stretchedStarless = result.DenoisedStarless ?? result.DeconvolvedStarless ?? result.Starless;
                if (stretchedStars is not null)
                    await WriteStretchedFloatTiffAsync(stretchedStars, tiffBase + "_stars.tif", ct);
                if (stretchedStarless is not null)
                    await WriteStretchedFloatTiffAsync(stretchedStarless, tiffBase + "_starless.tif", ct);
            }

            // Noise summary: per-channel σ pre/post + reduction %. Helps the
            // operator decide if denoise/deconv blends are sane without
            // grepping the pipeline log.
            if (!result.InputNoise.IsDefaultOrEmpty && !result.FinalNoise.IsDefaultOrEmpty
                && result.InputNoise.Length == result.FinalNoise.Length)
            {
                var preFmt = string.Join("/", result.InputNoise.Select(s => s.ToString("E2", System.Globalization.CultureInfo.InvariantCulture)));
                var postFmt = string.Join("/", result.FinalNoise.Select(s => s.ToString("E2", System.Globalization.CultureInfo.InvariantCulture)));
                var deltaFmt = string.Join("/", result.InputNoise.Zip(result.FinalNoise,
                    (pre, post) => pre > 0f ? $"{(post - pre) / pre * 100f:+0.0;-0.0;0.0}%" : "n/a"));
                consoleHost.WriteScrollable($"[sharpen] noise σ pre=[{preFmt}] post=[{postFmt}] Δ=[{deltaFmt}]");
            }

            // Release intermediates the orchestrator allocated.
            result.Starless?.Release();
            result.StarsOnly?.Release();
            result.SharpenedStars?.Release();
            result.DeconvolvedStarless?.Release();
            result.DenoisedStarless?.Release();
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

        var formatOpt = OutputFormatOption(
            "2D-viewer companion file alongside the FITS output. 'none' (default) = no companion. 'png' = 16-bit RGBA + cICP sRGB (SDR). 'png-pq' = 16-bit RGBA + cICP HDR10 PQ (HDR display). 'jxr' = JPEG XR with float-true HDR pixels.");
        var (pngPqPeakNitsOpt, pngPqGamutOpt) = HdrCompanionOptions();

        var cmd = new Command("remove-stars", "Star removal only (RC-Astro StarXTerminator; TianWen's own star remover is planned). Produces a starless export.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.Required(inputArg);
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

            var dst = EnsureFitsExtension(parseResult.GetValue(outputOpt) ?? DefaultOut(input, "_starless"));
            starless.WriteToFitsFile(dst, wcs, SharpenPipeline.SwModifyHeader());
            consoleHost.WriteScrollable($"[remove-stars] wrote {dst}");
            var format = parseResult.GetValue(formatOpt);
            var peakNits = Math.Clamp(parseResult.GetValue(pngPqPeakNitsOpt), 1f, 10000f);
            var gamutToBt2020 = parseResult.GetValue(pngPqGamutOpt) == PngPqGamut.Bt2020;
            await WriteCompanionAsync(starless, dst, format, src.ImageMeta, wcs, "remove-stars",
                useStretchedPng: false, peakNits: peakNits, gamutToBt2020: gamutToBt2020, ct: ct);
            starless.Release();
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image flatten ------------------------------------

    private Command BuildFlattenCommand()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "FITS file to flatten (remove smooth background gradient).",
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output FITS path. Default: <input>_flattened.fits.",
        };
        var formatOpt = OutputFormatOption(
            "2D-viewer companion file alongside the FITS output. 'none' (default) = no companion. 'png' = 16-bit cICP sRGB (SDR). 'png-pq' = 16-bit cICP HDR10 PQ. 'jxr' = float-true HDR. The --save-gradient surface PNG is unaffected - it stays PNG (min-max contrast visualisation, not banding-sensitive).");
        var (pngPqPeakNitsOpt, pngPqGamutOpt) = HdrCompanionOptions();
        var saveGradientOpt = new Option<bool>("--save-gradient")
        {
            Description = "Also write the estimated background surface as <output>_gradient.fits (+ .png if --png is set). Useful for sanity-checking the gradient model - you can see whether it picked up light pollution vs vignette vs sky-glow asymmetry. Skipped by default to avoid leaking a 120 MB plate per call on large drizzles.",
        };
        var backendOpt = new Option<FlattenBackend>("--backend")
        {
            Description = "'auto' (default) uses the registered IGradientCorrector: GraXpert BGE when its weights are installed, the classical fit otherwise. 'classical' always uses the classical polynomial fit and is what the tuning options below apply to. 'graxpert' demands the AI model and fails if it is absent rather than falling back silently.",
            DefaultValueFactory = _ => FlattenBackend.Auto,
        };
        var degreeOpt = new Option<int?>("--degree")
        {
            Description = "Classical only. Polynomial degree, 0 to 6, on coordinates normalised to [-1, 1]. Default 2, which is what the three reference implementations agree on: 1 cannot follow a dome, and above 3 the fit starts following the nebula.",
        };
        var surfaceOpt = new Option<bool>("--surface")
        {
            Description = "Classical only. Add the inpainted low-pass surface on the polynomial's residual, which follows a light-pollution dome the quadratic cannot. OFF by default and measured to be the switch that matters: over 118 real masters it moves the model 0.40 sigma RMS at p50 and drops the kept fraction from 0.795 to 0.581, because a flexible surface hollows a frame-filling nebula.",
        };
        var downsampleOpt = new Option<int?>("--downsample")
        {
            Description = "Classical only. Block-mean factor for the working grid, 1 to 32. Default 4; a CFA mosaic is split first and fitted at half this. Larger is faster and stiffer.",
        };
        var divideOpt = new Option<bool>("--divide")
        {
            Description = "Classical only. Divide by the background instead of subtracting: for a MULTIPLICATIVE residue (vignetting a flat did not remove). The right fix for that is a better flat; this is the escape hatch when there is none. An additive gradient (light pollution, sky glow) must stay subtractive.",
        };
        var excludeOpt = new Option<string[]>("--exclude")
        {
            Description = "Classical only, repeatable. A region the fit must not sample, in FULL-IMAGE pixels: either a rectangle 'x0,y0,x1,y1' or a polygon 'x,y;x,y;x,y' with at least three vertices. The model is still EVALUATED there (the polynomial is defined everywhere), so an excluded galaxy core keeps a background under it.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
        };

        var cmd = new Command("flatten", "Gradient correction: estimates the smooth background " +
            "(light pollution, vignette, sky-glow asymmetry) and takes it out while preserving the " +
            "mean sky level per plane. Runs at the head of the canonical Frank Sackenheim flow " +
            "(gradient -> stars -> detail -> stretch). Two backends: the GraXpert BGE ONNX model when " +
            "its weights are installed (read from GraXpert's own cache once it has run), and the classical robust " +
            "polynomial fit, which needs nothing and is what --degree/--surface/--downsample/" +
            "--divide/--exclude tune. Passing any of those selects --backend classical.")
        {
            Arguments = { inputArg },
            Options =
            {
                outputOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt, saveGradientOpt,
                backendOpt, degreeOpt, surfaceOpt, downsampleOpt, divideOpt, excludeOpt,
            },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.Required(inputArg);
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
            var format = parseResult.GetValue(formatOpt);
            var peakNits = Math.Clamp(parseResult.GetValue(pngPqPeakNitsOpt), 1f, 10000f);
            var gamutToBt2020 = parseResult.GetValue(pngPqGamutOpt) == PngPqGamut.Bt2020;
            var saveGradient = parseResult.GetValue(saveGradientOpt);

            var degree = parseResult.GetValue(degreeOpt);
            var downsample = parseResult.GetValue(downsampleOpt);
            var surface = parseResult.GetValue(surfaceOpt);
            var divide = parseResult.GetValue(divideOpt);
            var excludeSpecs = parseResult.GetValue(excludeOpt) ?? [];
            var tuned = degree.HasValue || downsample.HasValue || surface || divide || excludeSpecs.Length > 0;
            var backend = parseResult.GetValue(backendOpt);
            if (tuned && backend == FlattenBackend.Auto)
            {
                // A tuning option under 'auto' would be SILENTLY ignored whenever the GraXpert weights
                // happen to be installed, which is the machine-dependent half of a two-machine bug.
                // Selecting the backend the options belong to is the only reading that cannot surprise.
                backend = FlattenBackend.Classical;
            }
            else if (tuned && backend != FlattenBackend.Classical)
            {
                consoleHost.WriteError("--degree/--downsample/--surface/--divide/--exclude tune the classical fit and have no meaning for --backend graxpert.");
                src.Release();
                return 1;
            }

            var exclusions = ImmutableArray<ExclusionPolygon>.Empty;
            if (excludeSpecs.Length > 0)
            {
                var built = ImmutableArray.CreateBuilder<ExclusionPolygon>(excludeSpecs.Length);
                foreach (var spec in excludeSpecs)
                {
                    if (!TryParseExclusion(spec, out var polygon))
                    {
                        consoleHost.WriteError($"--exclude: cannot read '{spec}'. Use a rectangle 'x0,y0,x1,y1' or a polygon 'x,y;x,y;x,y'.");
                        src.Release();
                        return 1;
                    }
                    built.Add(polygon);
                }
                exclusions = built.MoveToImmutable();
            }

            if (backend == FlattenBackend.Graxpert && !gradientCorrector.Name.Contains("GraXpert", StringComparison.OrdinalIgnoreCase))
            {
                // Naming a backend has to mean it. 'auto' is the mode that silently substitutes; asking
                // for GraXpert on a machine without its weights and quietly getting the classical fit
                // would make a script's output depend on which box it ran on.
                consoleHost.WriteError(
                    "--backend graxpert: the GraXpert BGE weights are not installed, so the active corrector is "
                    + $"'{gradientCorrector.Name}'. Install GraXpert and run it once (its model cache is read in place), "
                    + "or use --backend classical / --backend auto.");
                return 1;
            }

            BackgroundExtractionOptions? classicalOptions = null;
            if (backend == FlattenBackend.Classical)
            {
                classicalOptions = BackgroundExtractionOptions.Default with
                {
                    PolynomialDegree = degree ?? BackgroundExtractionOptions.Default.PolynomialDegree,
                    Downsample = downsample ?? BackgroundExtractionOptions.Default.Downsample,
                    SurfaceRefinement = surface,
                    Correction = divide ? BackgroundCorrection.Divide : BackgroundCorrection.Subtract,
                    Exclusions = exclusions,
                };
                try
                {
                    classicalOptions.Validate();
                }
                catch (ArgumentException ex)
                {
                    consoleHost.WriteError($"flatten: {ex.Message}");
                    src.Release();
                    return 1;
                }
            }

            consoleHost.WriteScrollable(
                $"[flatten] {input} {src.Width}x{src.Height}x{src.ChannelCount} backend={backend.ToString().ToLowerInvariant()}"
                + (classicalOptions is { } o
                    ? $" degree={o.PolynomialDegree} downsample={o.Downsample} surface={o.SurfaceRefinement}"
                      + $" {o.Correction.ToString().ToLowerInvariant()}"
                      + (exclusions.Length > 0 ? $" exclusions={exclusions.Length}" : "")
                    : "")
                + (saveGradient ? " save-gradient=true" : ""));

            Image flattened;
            Image? background = null;
            try
            {
                if (classicalOptions is { } options)
                {
                    // The classical extractor hands back the model whether or not it was asked, so
                    // --save-gradient costs nothing here and the "no separate surface" note below is
                    // unreachable on this path.
                    var result = await backgroundExtractor.ExtractAsync(normalised, options, ct);
                    flattened = result.Cleaned;
                    background = saveGradient ? result.Background : null;
                    if (!saveGradient)
                    {
                        result.Background.Release();
                    }

                    // The kept fraction is the one number that says whether the fit sampled the sky or
                    // the object: it is the share of working pixels the final iteration was made on, and
                    // a frame-filling nebula drives it down. G1 measured 0.795 with the surface off
                    // against 0.581 with it on over 118 real masters, so a run well under that is the
                    // signal to look at --exclude or to turn --surface back off.
                    foreach (var plane in result.Planes)
                    {
                        consoleHost.WriteScrollable(
                            $"[flatten]   plane {plane.Plane}: kept {plane.KeptFraction:P1}"
                            // P2, and only above a hundredth of a percent: a TianWen master's canvas
                            // ring makes this nonzero on almost every real frame, and "excluded 0.0%"
                            // reads as a bug rather than as the edge blocks it is.
                            + (plane.ExcludedFraction >= 1e-4f ? $" excluded {plane.ExcludedFraction:P2}" : "")
                            + $" residual {plane.ResidualSigma:0.###e+0} sigma, level {plane.Level:0.####}"
                            + $", {plane.Iterations} iterations{(plane.Converged ? "" : " (NOT converged)")}");
                    }
                }
                else if (saveGradient)
                {
                    (flattened, background) = await gradientCorrector.EnhanceAndEstimateBackgroundAsync(normalised, ct);
                }
                else
                {
                    flattened = await gradientCorrector.EnhanceAsync(normalised, ct);
                }
            }
            catch (Exception ex)
            {
                consoleHost.WriteError($"flatten failed: {ex.Message}");
                logger?.LogError(ex, "Gradient correction failed for {Input}", input);
                return 2;
            }

            var dst = EnsureFitsExtension(parseResult.GetValue(outputOpt) ?? DefaultOut(input, "_flattened"));
            flattened.WriteToFitsFile(dst, wcs, SharpenPipeline.SwModifyHeader());
            consoleHost.WriteScrollable($"[flatten] wrote {dst}");
            await WriteCompanionAsync(flattened, dst, format, src.ImageMeta, wcs, "flatten",
                useStretchedPng: false, peakNits: peakNits, gamutToBt2020: gamutToBt2020, ct: ct);
            if (saveGradient)
            {
                if (background is null)
                {
                    // Default-interface-method path: the active corrector
                    // doesn't expose a background surface (only the AI BGE
                    // does today). Tell the operator so they don't think the
                    // gradient was empty.
                    consoleHost.WriteScrollable($"[flatten] --save-gradient: active IGradientCorrector ({gradientCorrector.GetType().Name}) does not expose a separate background surface; skipping.");
                }
                else
                {
                    var gradientDst = ReplaceExtension(dst, "_gradient.fits");
                    background.WriteToFitsFile(gradientDst, wcs, SharpenPipeline.SwModifyHeader());
                    consoleHost.WriteScrollable($"[flatten] wrote {gradientDst}");
                    if (format != ImageOutputFormat.None)
                    {
                        // Min-max contrast stretch -- the gradient is a smooth
                        // low-amplitude surface, MasterPreviewRenderer's
                        // SPCC + bg-neut + auto-MTF crushes the very signal
                        // we want to see. Logs per-channel amplitude so the
                        // operator can tell whether the model thinks there
                        // IS a gradient (informative output) or whether it
                        // settled on essentially uniform (suspicious). Always
                        // PNG -- contrast-stretch viz isn't banding-sensitive,
                        // a 30 MB JXR of a smooth gradient is pointless.
                        await WriteContrastStretchedPngAsync(background, ReplaceExtension(gradientDst, ".png"), "gradient", ct);
                    }
                    background.Release();
                }
            }
            flattened.Release();
            return 0;
        });
        return cmd;
    }

    /// <summary>
    /// Reads one <c>--exclude</c> value: a rectangle <c>x0,y0,x1,y1</c> or a polygon
    /// <c>x,y;x,y;x,y</c>. Both are FULL-IMAGE pixel coordinates. The two shapes are told apart by the
    /// separator rather than by counting, so a four-vertex polygon is still a polygon.
    /// </summary>
    internal static bool TryParseExclusion(string spec, [NotNullWhen(true)] out ExclusionPolygon? polygon)
    {
        polygon = null;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        if (spec.Contains(';', StringComparison.Ordinal))
        {
            var parts = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                return false;
            }
            var vertices = ImmutableArray.CreateBuilder<Vector2>(parts.Length);
            foreach (var part in parts)
            {
                var xy = part.Split(',', StringSplitOptions.TrimEntries);
                if (xy.Length != 2
                    || !float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                {
                    return false;
                }
                vertices.Add(new Vector2(x, y));
            }
            polygon = new ExclusionPolygon(vertices.MoveToImmutable());
            return true;
        }

        var n = spec.Split(',', StringSplitOptions.TrimEntries);
        if (n.Length != 4)
        {
            return false;
        }
        var v = new float[4];
        for (var i = 0; i < 4; i++)
        {
            if (!float.TryParse(n[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]))
            {
                return false;
            }
        }
        // Order the corners so '400,300,100,200' means the same rectangle as '100,200,400,300'.
        polygon = ExclusionPolygon.Rectangle(
            MathF.Min(v[0], v[2]), MathF.Min(v[1], v[3]), MathF.Max(v[0], v[2]), MathF.Max(v[1], v[3]));
        return true;
    }

    // -------- tianwen image render -------------------------------------

    private Command BuildRenderCommand()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "FITS file to render.",
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output path. Default: <input>.png (or <input>.jxr when --output-format=jxr).",
        };
        var formatOpt = OutputFormatOption(
            "Output container. 'png' (default) = 16-bit RGBA + cICP sRGB via MasterPreviewRenderer (SPCC + sky-bg WB + bg-neut + stretch). 'png-pq' = 16-bit RGBA + cICP HDR10 PQ (BT.2020 + SMPTE 2084); modern browsers / HDR displays render as actual HDR at --png-pq-peak-nits peak. 'jxr' = JPEG XR with float-true HDR pixels (BD32F mono / BD16F RGB); writes the input verbatim, NO SPCC / WB / stretch.", ImageOutputFormat.Png);
        var (pngPqPeakNitsOpt, pngPqGamutOpt) = HdrCompanionOptions();
        // Masked finishing boost (Image.MaskedBoost) -- mirrors `stack --saturation` /
        // `--contrast-boost` so the preview look can be iterated against an existing
        // master FITS without re-stacking.
        var saturationOpt = new Option<float>("--saturation")
        {
            Description = "Masked saturation boost baked into the stretched PNG output (background and star cores are protected by a luminance range mask). 1.0 = off (default); typical 1.3-2.0. PNG / PNG-PQ only; ignored for --output-format jxr (verbatim float output).",
            DefaultValueFactory = _ => 1.0f,
        };
        var contrastBoostOpt = new Option<float>("--contrast-boost")
        {
            Description = "Masked S-curve contrast boost baked into the stretched PNG output (same protective luminance mask as --saturation). 0 = off (default); typical 0.25-1.5. PNG / PNG-PQ only; ignored for --output-format jxr.",
            DefaultValueFactory = _ => 0f,
        };
        // INHERIT a colour calibration instead of re-fitting one. The renderer has always had the
        // parameter (MasterPostProcessor shares the master's one solve across the split-plate
        // TIFFs with it); this is the same thing reachable from the command line, so a caller
        // rendering a master and its enhanced twin can give the second the first's balance.
        var whiteBalanceOpt = new Option<string?>("--white-balance")
        {
            Description = "Use this exact white balance instead of solving one, as 'R,G,B' (e.g. 1.443,1,1.228) -- the triple the render line prints. Skips SPCC and the sky-background fallback. Use it to give an enhanced master the balance solved on the unenhanced one, rather than re-fitting against a background the enhance has already flattened. PNG / PNG-PQ only.",
        };

        var cmd = new Command("render", "Render a FITS file to a stretched PNG (default), HDR PQ PNG (--output-format png-pq), or float-true HDR JPEG XR (--output-format jxr).")
        {
            Arguments = { inputArg },
            Options = { outputOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt, saturationOpt, contrastBoostOpt, whiteBalanceOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.Required(inputArg);
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
            var format = parseResult.GetValue(formatOpt);
            if (format == ImageOutputFormat.None)
            {
                // `render`'s whole purpose is to produce a viewer file --
                // None is nonsensical here (it'd silently no-op). Sharpen /
                // remove-stars / flatten accept None because they have a
                // FITS primary output; render does not.
                consoleHost.WriteError("--output-format=none is invalid for `image render` (it would produce no output).");
                return 1;
            }
            // Masked preview boost: identity values collapse to null (no render change);
            // JXR writes the input floats verbatim, so the boost cannot apply there.
            var saturation = parseResult.GetValue(saturationOpt);
            var contrastBoost = parseResult.GetValue(contrastBoostOpt);
            if (saturation < 0f || !float.IsFinite(saturation) ||
                contrastBoost < 0f || !float.IsFinite(contrastBoost))
            {
                consoleHost.WriteError("--saturation and --contrast-boost must be finite values >= 0.");
                return 1;
            }
            var maskedBoost = saturation != 1.0f || contrastBoost != 0f
                ? new MaskedBoostOptions(saturation, contrastBoost)
                : null;
            if (maskedBoost is not null && format == ImageOutputFormat.Jxr)
            {
                consoleHost.WriteScrollable("[render] warning: --saturation/--contrast-boost only affect the stretched PNG paths; ignored with --output-format=jxr");
                maskedBoost = null;
            }
            (float R, float G, float B)? whiteBalance = null;
            if (parseResult.GetValue(whiteBalanceOpt) is { Length: > 0 } wbText)
            {
                if (!TryParseWhiteBalance(wbText, out var parsedWb))
                {
                    consoleHost.WriteError($"--white-balance must be three positive finite numbers 'R,G,B'; got '{wbText}'.");
                    return 1;
                }
                if (format == ImageOutputFormat.Jxr)
                {
                    consoleHost.WriteScrollable("[render] warning: --white-balance only affects the stretched PNG paths; ignored with --output-format=jxr (verbatim floats)");
                }
                else
                {
                    whiteBalance = parsedWb;
                }
            }
            // `render` writes the chosen format AS the primary output; passing
            // primaryPath = dst means ReplaceExtension is a no-op when the
            // extension already matches. ImageOutputFormat.None was rejected
            // above, so this always emits one file.
            var dst = parseResult.GetValue(outputOpt) ?? ReplaceExtension(input, ExtensionFor(format));
            consoleHost.WriteScrollable(
                $"[render] {input} {src.Width}x{src.Height}x{src.ChannelCount} -> {dst} ({format.ToString().ToLowerInvariant()})");
            // HONOUR THE DECLARED SCALE. Every other verb here unit-scales its input and this one did
            // not, so a master written on the subs' own ADU scale was stretched as if it were [0, 1]:
            // red crushed to black and green and blue saturated, which is how a Float16Staged session
            // master rendered as a flat blue field with a few stars in it. Measured on the Running
            // Chicken Nebula master (DATAMAX 97723.5, channel medians 738 / 2897 / 1749) against
            // Centaurus A from the SAME NIGHT and TRAIN (DATAMAX 1.24, medians 0.0069 / 0.0276 /
            // 0.0162): 30 of one store's 92 masters are on the ADU scale, and they are exactly the
            // staged ones, because only the drizzle path happens to normalise.
            //
            // ScaleFloatValuesToUnit is the right one: it asks whether the samples ARE ADU and leaves
            // a peak up to 2.0 alone, so a drizzle master at 1.24 is untouched and byte-identical to
            // before. JXR is excluded because it writes the input floats verbatim by contract -- no
            // SPCC, no white balance, no stretch -- and scaling them would break that promise.
            var toRender = format == ImageOutputFormat.Jxr ? src : src.ScaleFloatValuesToUnit();
            await WriteCompanionAsync(toRender, dst, format, src.ImageMeta, wcs, "render",
                useStretchedPng: false,
                peakNits: Math.Clamp(parseResult.GetValue(pngPqPeakNitsOpt), 1f, 10000f),
                gamutToBt2020: parseResult.GetValue(pngPqGamutOpt) == PngPqGamut.Bt2020,
                maskedBoost: maskedBoost,
                whiteBalanceOverride: whiteBalance,
                ct: ct);
            return 0;
        });
        return cmd;
    }

    // -------- tianwen image stats --------------------------------------

    private Command BuildStatsCommand()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "FITS file to measure stats against.",
        };
        var formatOpt = new Option<string>("--format")
        {
            Description = "Output format: 'text' (default, human-readable) or 'json' (machine-parseable single object).",
            DefaultValueFactory = _ => "text",
        };
        var snrMinOpt = new Option<float>("--snr-min")
        {
            Description = "Minimum star SNR for detection. Default 20 - matches FindStarsAsync default. Lower values pick up more (noisier) stars.",
            DefaultValueFactory = _ => 20f,
        };
        var maxStarsOpt = new Option<int>("--max-stars")
        {
            Description = "Cap on the number of detected stars. Default 500.",
            DefaultValueFactory = _ => 500,
        };

        var cmd = new Command("stats",
            "Measure per-image statistics: star count, median HFD/FWHM/Ellipticity/SNR (linear inputs only), " +
            "per-channel pedestal/median/MAD + noise σ (MAD x 1.4826, unit-scaled). " +
            "Inputs detected as already-stretched via Image.DetectPreStretched still produce numbers but emit a warning -- " +
            "HFD/FWHM/SNR aren't directly comparable across linear and stretched plates.")
        {
            Arguments = { inputArg },
            Options = { formatOpt, snrMinOpt, maxStarsOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.Required(inputArg);
            if (!File.Exists(input))
            {
                consoleHost.WriteError($"Input not found: {input}");
                return 1;
            }
            if (!Image.TryReadFitsFile(input, out var src, out _))
            {
                consoleHost.WriteError($"Failed to read FITS file: {input}");
                return 1;
            }

            var snrMin = parseResult.GetValue(snrMinOpt);
            var maxStars = parseResult.GetValue(maxStarsOpt);
            var formatStr = (parseResult.GetValue(formatOpt) ?? "text").ToLowerInvariant();
            var asJson = formatStr switch
            {
                "json" => true,
                "text" => false,
                _ => (bool?)null,
            };
            if (asJson is null)
            {
                consoleHost.WriteError($"--format must be 'text' or 'json', got '{formatStr}'");
                return 1;
            }

            ImageStats stats;
            try
            {
                stats = await ImageStats.ComputeAsync(src, snrMin: snrMin, maxStars: maxStars, logger: logger, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                consoleHost.WriteError($"stats failed: {ex.Message}");
                logger?.LogError(ex, "Stats computation failed for {Input}", input);
                return 2;
            }
            src.Release();

            // System.Text.Json reflection-based serializer trips IL2026/IL3050
            // under AOT. Schema is tiny; hand-roll JSON via StringBuilder
            // (same approach SolveSubCommand uses for its stars export).
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (asJson.Value)
            {
                var sb = new System.Text.StringBuilder(512);
                // JsonEncodedText handles control chars + quotes + backslashes per spec;
                // .ToString() returns the escaped content (no surrounding quotes), which
                // we provide ourselves.
                sb.Append("{\"input\":\"").Append(System.Text.Json.JsonEncodedText.Encode(input).ToString())
                    .Append("\",\"width\":").Append(stats.Width)
                    .Append(",\"height\":").Append(stats.Height)
                    .Append(",\"channels\":").Append(stats.ChannelCount)
                    .Append(",\"isLinear\":").Append(stats.IsLinear ? "true" : "false")
                    .Append(",\"starCount\":").Append(stats.StarCount)
                    .Append(",\"hfdMedian\":").Append(stats.HfdMedian.ToString("R", inv))
                    .Append(",\"fwhmMedian\":").Append(stats.FwhmMedian.ToString("R", inv))
                    .Append(",\"ellipticityMedian\":").Append(stats.EllipticityMedian.ToString("R", inv))
                    .Append(",\"snrMedian\":").Append(stats.SnrMedian.ToString("R", inv))
                    .Append(",\"perChannel\":[");
                for (var i = 0; i < stats.PerChannel.Length; i++)
                {
                    var c = stats.PerChannel[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"channel\":").Append(c.ChannelIndex)
                        .Append(",\"pedestal\":").Append(c.Pedestal.ToString("R", inv))
                        .Append(",\"median\":").Append(c.Median.ToString("R", inv))
                        .Append(",\"mad\":").Append(c.Mad.ToString("R", inv))
                        .Append(",\"noiseSigma\":").Append(c.NoiseSigma.ToString("R", inv))
                        .Append('}');
                }
                sb.Append("],\"warnings\":[");
                if (!stats.IsLinear)
                    sb.Append("\"image appears stretched; HFD/FWHM/SNR are not directly comparable to linear plates\"");
                sb.Append("]}");
                consoleHost.WriteScrollable(sb.ToString());
                return 0;
            }

            // Text format: stats line per group, two-decimal pixels, scientific
            // notation for noise σ (typically 1e-4 .. 1e-2 on linear plates).
            consoleHost.WriteScrollable(
                $"[stats] {input} {stats.Width}x{stats.Height}x{stats.ChannelCount} " +
                $"linear={(stats.IsLinear ? "yes" : "NO")} stars={stats.StarCount}");
            if (stats.StarCount > 0)
            {
                consoleHost.WriteScrollable(
                    $"[stats] stars: HFD={stats.HfdMedian.ToString("F2", inv)}px " +
                    $"FWHM={stats.FwhmMedian.ToString("F2", inv)}px " +
                    $"ecc={stats.EllipticityMedian.ToString("F3", inv)} " +
                    $"SNR={stats.SnrMedian.ToString("F1", inv)} (medians over {stats.StarCount} stars)");
            }
            for (var i = 0; i < stats.PerChannel.Length; i++)
            {
                var c = stats.PerChannel[i];
                consoleHost.WriteScrollable(
                    $"[stats] c{c.ChannelIndex}: pedestal={c.Pedestal.ToString("E2", inv)} " +
                    $"median={c.Median.ToString("E2", inv)} " +
                    $"MAD={c.Mad.ToString("E2", inv)} " +
                    $"σ={c.NoiseSigma.ToString("E2", inv)}");
            }
            if (!stats.IsLinear)
            {
                consoleHost.WriteError("[stats] WARN: image appears stretched; HFD/FWHM/SNR are not directly comparable to linear plates.");
            }
            return 0;
        });
        return cmd;
    }

    // -------- helpers ---------------------------------------------------

    /// <summary>
    /// Write a plate to <c>basePath + suffix + ".fits"</c>, optionally followed
    /// by a same-stem 2D-viewer companion picked by <paramref name="format"/>.
    /// Used for both the recombined output and the per-plate exports from
    /// <c>--no-recombine</c>. Companion dispatch is delegated to
    /// <see cref="WriteCompanionAsync"/>.
    /// </summary>
    private async Task WritePlateAsync(Image? plate, string basePath, string suffix, WCS? wcs, ImageMeta sensorMeta, ImageOutputFormat format, float peakNits, bool gamutToBt2020, CancellationToken ct)
    {
        if (plate is null) return;
        var path = basePath.EndsWith(".fits", StringComparison.OrdinalIgnoreCase)
            ? StripExtension(basePath) + suffix + ".fits"
            : basePath + suffix + ".fits";
        plate.WriteToFitsFile(path, wcs, SharpenPipeline.SwModifyHeader());
        consoleHost.WriteScrollable($"[sharpen] wrote {path}");
        await WriteCompanionAsync(plate, path, format, sensorMeta, wcs, "sharpen",
            useStretchedPng: false, peakNits: peakNits, gamutToBt2020: gamutToBt2020, ct: ct);
    }

    /// <summary>File extension (with leading dot) for the chosen companion format.</summary>
    private static string ExtensionFor(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.Jxr => ".jxr",
        ImageOutputFormat.Exr => ".exr",
        // UltraHdr is a JPEG container (baseline SDR base + attached gain map).
        ImageOutputFormat.UltraHdr => ".jpg",
        // PngPq stays .png -- it's a standard PNG file with cICP HDR10 signaling,
        // not a different container. Tools that don't honour cICP fall back to
        // SDR display via the PNG bit-depth alone.
        _ => ".png",
    };

    /// <summary>
    /// Single dispatch point for companion files. Callers pass the FITS
    /// primary path (extension is swapped here) plus the chosen
    /// <paramref name="format"/>; the routing between PNG renderer, JXR
    /// float writer, dual-stretch byte-PNG, and "no companion" lives in
    /// one place so subcommands don't open-code the same switch four times.
    /// </summary>
    /// <param name="useStretchedPng">When <c>true</c> and
    /// <paramref name="format"/> is <see cref="ImageOutputFormat.Png"/> or
    /// <see cref="ImageOutputFormat.PngPq"/>, the byte-encode path is used
    /// (assumes the image is already in stretched <c>[0, 1]</c> space).
    /// Used by the sharpen + dual-stretch flow to avoid double-stretching
    /// via <see cref="MasterPreviewRenderer"/>. Ignored for JXR.</param>
    /// <param name="peakNits">Peak display luminance for the HDR10 PQ
    /// encoding when format is <see cref="ImageOutputFormat.PngPq"/>.
    /// Ignored for other formats.</param>
    /// <param name="gamutToBt2020">When PNG-PQ is selected, controls
    /// whether the sRGB-to-BT.2020 gamut matrix is applied (true,
    /// canonical HDR10) or skipped (false, narrow-gamut sRGB+PQ).
    /// Ignored for other formats.</param>
    /// <param name="maskedBoost">Opt-in masked finishing boost baked into the
    /// renderer-stretched PNG paths (see <see cref="Image.MaskedBoost"/>).
    /// Ignored for JXR (verbatim floats) and the pre-stretched byte-encode
    /// paths (<paramref name="useStretchedPng"/>). Only the render verb
    /// passes a non-null value today.</param>
    private async Task WriteCompanionAsync(
        Image image, string primaryPath, ImageOutputFormat format,
        ImageMeta sensorMeta, WCS? wcs, string tag,
        bool useStretchedPng,
        float peakNits,
        bool gamutToBt2020,
        MaskedBoostOptions? maskedBoost = null,
        (float R, float G, float B)? whiteBalanceOverride = null,
        CancellationToken ct = default)
    {
        if (format == ImageOutputFormat.None) return;
        var path = ReplaceExtension(primaryPath, ExtensionFor(format));
        switch (format)
        {
            case ImageOutputFormat.Jxr:
                try
                {
                    await image.WriteJxrAsync(path, DebayerAlgorithm.VNG, ct);
                    consoleHost.WriteScrollable($"[{tag}] wrote {path} (JXR HDR)");
                }
                catch (Exception ex)
                {
                    consoleHost.WriteError($"JXR write failed for {path}: {ex.Message}");
                    logger?.LogError(ex, "JXR write failed for {Path}", path);
                }
                break;
            case ImageOutputFormat.Png when useStretchedPng:
                await WriteStretchedPngAsync(image, path, hdr10Pq: false, peakNits, gamutToBt2020, ct);
                break;
            case ImageOutputFormat.PngPq when useStretchedPng:
                await WriteStretchedPngAsync(image, path, hdr10Pq: true, peakNits, gamutToBt2020, ct);
                break;
            case ImageOutputFormat.Png:
                await RenderPngAsync(image, sensorMeta, wcs, path, hdr10Pq: false, peakNits, gamutToBt2020, maskedBoost, whiteBalanceOverride, ct);
                break;
            case ImageOutputFormat.PngPq:
                await RenderPngAsync(image, sensorMeta, wcs, path, hdr10Pq: true, peakNits, gamutToBt2020, maskedBoost, whiteBalanceOverride, ct);
                break;
            case ImageOutputFormat.UltraHdr:
                await RenderUltraHdrAsync(image, sensorMeta, wcs, path, peakNits, maskedBoost, ct);
                break;
            case ImageOutputFormat.Exr:
                // EXR is the unstretched linear master emitted by the 'stack' command;
                // the 'image' command produces stretched/processed output (jxr / png).
                consoleHost.WriteError($"EXR is the unstretched stacking-master format (use the 'stack' command); the 'image' command emits stretched output. Skipping {path}.");
                break;
        }
    }

    /// <summary>
    /// Run the shared <see cref="MasterPreviewRenderer"/> -- same path the
    /// stack subcommand uses for its <c>master_*.png</c> -- against
    /// <paramref name="img"/>. SPCC is computed at render time and only
    /// baked into the PNG; the source FITS stays untouched.
    /// </summary>
    /// <summary>
    /// Parse a <c>--white-balance</c> triple. Accepts exactly what the render line prints,
    /// <c>R,G,B</c>, and refuses anything that is not three positive finite numbers -- a zero or
    /// negative gain would black out or invert a channel, and a silently-dropped override would
    /// look like the inheritance had worked.
    /// </summary>
    internal static bool TryParseWhiteBalance(string text, out (float R, float G, float B) wb)
    {
        wb = default;
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) return false;
        Span<float> gains = stackalloc float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
                || !float.IsFinite(g) || g <= 0f)
            {
                return false;
            }
            gains[i] = g;
        }
        wb = (gains[0], gains[1], gains[2]);
        return true;
    }

    private async Task RenderPngAsync(Image img, ImageMeta sensorMeta, WCS? wcs, string pngPath, bool hdr10Pq, float peakNits, bool gamutToBt2020, MaskedBoostOptions? maskedBoost, (float R, float G, float B)? whiteBalanceOverride, CancellationToken ct)
    {
        try
        {
            var render = await previewRenderer.RenderAsync(img, sensorMeta, wcs, statsSource: null, pngPath,
                hdr10Pq: hdr10Pq, peakNits: peakNits, gamutToBt2020: gamutToBt2020,
                maskedBoost: maskedBoost, whiteBalanceOverride: whiteBalanceOverride, ct: ct);
            var suffix = hdr10Pq
                ? $" (HDR PQ, {peakNits:F0} nits, {(gamutToBt2020 ? "BT.2020" : "sRGB")} primaries)"
                : "";
            consoleHost.WriteScrollable($"[render] wrote {pngPath}{suffix}");
            // SAY WHICH WHITE BALANCE WAS USED, in the form --white-balance takes back. The
            // renderer has always returned it and this verb has always discarded it, so a caller
            // rendering a master and then its enhanced twin had no way to give the second the
            // first's colour calibration and each re-fitted its own -- which is not a re-fit of
            // the same question, because the enhance has already flattened the background the
            // second solve reads. Printing it is what lets one render inherit another's, the way
            // MasterPostProcessor already shares one solve across the split-plate TIFFs.
            if (render.WhiteBalance is { } wb)
            {
                var source = whiteBalanceOverride is not null ? "inherited"
                    : render.Spcc is not null ? "SPCC"
                    : "sky background";
                consoleHost.WriteScrollable(
                    $"[render] white-balance {wb.R:F6},{wb.G:F6},{wb.B:F6} ({source})");
            }
        }
        catch (Exception ex)
        {
            consoleHost.WriteError($"PNG render failed for {pngPath}: {ex.Message}");
            logger?.LogError(ex, "PNG render failed for {Path}", pngPath);
        }
    }

    /// <summary>
    /// Emit an Ultra HDR (gain-map) JPEG via the shared <see cref="MasterPreviewRenderer"/>:
    /// the same SPCC + stretch solve as <see cref="RenderPngAsync"/>, but the renderer writes
    /// the gain-map JPEG (SDR base + attached highlight-recovery gain map) instead of a PNG.
    /// The source FITS is untouched; <paramref name="peakNits"/> sets the linear display headroom.
    /// </summary>
    private async Task RenderUltraHdrAsync(Image img, ImageMeta sensorMeta, WCS? wcs, string jpgPath, float peakNits, MaskedBoostOptions? maskedBoost, CancellationToken ct)
    {
        try
        {
            // pngPath empty -> the renderer skips the PNG and only writes the Ultra HDR JPEG.
            await previewRenderer.RenderAsync(img, sensorMeta, wcs, statsSource: null, outputPath: string.Empty,
                peakNits: peakNits, maskedBoost: maskedBoost, ultraHdrPath: jpgPath, ct: ct);
            consoleHost.WriteScrollable($"[render] wrote {jpgPath} (Ultra HDR gain-map JPEG, {peakNits:F0} nits headroom)");
        }
        catch (Exception ex)
        {
            consoleHost.WriteError($"Ultra HDR render failed for {jpgPath}: {ex.Message}");
            logger?.LogError(ex, "Ultra HDR render failed for {Path}", jpgPath);
        }
    }

    /// <summary>
    /// Per-plate stretched-TIFF export for <c>--dual-stretch</c>. Delegates to the
    /// shared <see cref="Image.WriteStretchedTiffAsync"/> (TianWen.Lib) -- 32-bit
    /// IEEE float, written verbatim, tagged with the bundled sRGB v4 ICC so
    /// colour-managed viewers (Photoshop / Affinity) display the stretched values
    /// 1:1. Sharing the writer keeps this path and <c>stack --split-plates</c>
    /// byte-identical. (EXR is deliberately NOT used here: it carries no transfer
    /// tag and is assumed scene-linear, so stretched values would be re-gamma'd
    /// and over-brightened -- EXR is reserved for the linear master.)
    /// </summary>
    private async Task WriteStretchedFloatTiffAsync(Image image, string path, CancellationToken ct)
    {
        var (channels, _, _) = image.Shape;
        if (channels is not (1 or 3))
        {
            consoleHost.WriteError($"TIFF export requires 1 or 3 channels, got {channels}; skipping {path}");
            return;
        }
        await image.WriteStretchedTiffAsync(path, ct);
        consoleHost.WriteScrollable($"[sharpen] wrote {path}");
    }

    /// <summary>
    /// Byte-encode a pre-stretched [0, 1] <see cref="Image"/> directly as a
    /// PNG with sRGB v4 ICC tag. NO additional stretch / SCNR / WB is
    /// applied -- the input is treated as final display-ready data. Use
    /// this for the dual-stretch PNG path where the pipeline has already
    /// done per-plate MTF + screen recombine and a second MTF via
    /// <see cref="MasterPreviewRenderer"/> would over-lift and saturate.
    /// </summary>
    /// <summary>
    /// Min-max contrast-stretched PNG. For each channel, computes min/max
    /// and maps that range to [0, 255]. Designed for visualising
    /// low-amplitude smooth surfaces (gradient correctors' background output)
    /// where the master preview renderer's SPCC + bg-neut + auto-MTF stretch
    /// crushes the very signal we want to inspect. Also logs per-channel
    /// min / max / amplitude so the operator can see whether the model
    /// actually thinks there's a gradient at all.
    /// </summary>
    private async Task WriteContrastStretchedPngAsync(Image image, string pngPath, string tag, CancellationToken ct)
    {
        var (channels, w, h) = image.Shape;
        if (channels is not (1 or 3))
        {
            consoleHost.WriteError($"PNG export requires 1 or 3 channels, got {channels}; skipping {pngPath}");
            return;
        }

        var pixelCount = w * h;
        // Heap allocation because stackalloc Span can't cross the LINQ lambda
        // below. 3-element float[] is trivial; not in a hot path.
        var mins = new float[3];
        var maxs = new float[3];
        for (var c = 0; c < channels; c++)
        {
            var span = image.GetChannelSpan(c);
            var min = float.PositiveInfinity;
            var max = float.NegativeInfinity;
            for (var i = 0; i < span.Length; i++)
            {
                var v = span[i];
                if (!float.IsFinite(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            mins[c] = min;
            maxs[c] = max;
        }

        var amplitudeLog = string.Join(" ",
            Enumerable.Range(0, channels)
                .Select(c => $"c{c}:[{mins[c]:E2}..{maxs[c]:E2}] amp={maxs[c] - mins[c]:E2}"));
        consoleHost.WriteScrollable($"[{tag}] {amplitudeLog}");

        var rgba = new byte[pixelCount * 4];
        var r = image.GetChannelSpan(0);
        var g = channels == 3 ? image.GetChannelSpan(1) : r;
        var b = channels == 3 ? image.GetChannelSpan(2) : r;
        var rMin = mins[0]; var rRange = MathF.Max(maxs[0] - rMin, 1e-9f);
        var gMin = channels == 3 ? mins[1] : rMin; var gRange = channels == 3 ? MathF.Max(maxs[1] - gMin, 1e-9f) : rRange;
        var bMin = channels == 3 ? mins[2] : rMin; var bRange = channels == 3 ? MathF.Max(maxs[2] - bMin, 1e-9f) : rRange;
        for (var i = 0; i < pixelCount; i++)
        {
            rgba[i * 4 + 0] = ToByte((r[i] - rMin) / rRange);
            rgba[i * 4 + 1] = ToByte((g[i] - gMin) / gRange);
            rgba[i * 4 + 2] = ToByte((b[i] - bMin) / bRange);
            rgba[i * 4 + 3] = 255;
        }

        var png = PngWriter.Encode(rgba, w, h, new PngWriteOptions { Cicp = CicpChunk.Srgb });
        await File.WriteAllBytesAsync(pngPath, png, ct);
        consoleHost.WriteScrollable($"[{tag}] wrote {pngPath} (min-max contrast)");

        static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);
    }

    private async Task WriteStretchedPngAsync(Image image, string pngPath, bool hdr10Pq, float peakNits, bool gamutToBt2020, CancellationToken ct)
    {
        var (channels, w, h) = image.Shape;
        if (channels is not (1 or 3))
        {
            consoleHost.WriteError($"PNG export requires 1 or 3 channels, got {channels}; skipping {pngPath}");
            return;
        }

        // 16-bit RGBA interleaved (alpha=65535). Mono replicates the single
        // channel into R/G/B since EncodeRgba16 is the natural HDR-precision
        // entry point and we don't want to fork a separate Gray16 path here.
        // 65,536 levels eliminate the banding the old 8-bit path produced.
        //
        // Gamut-preserving max-channel scale: under HDR formats (PngPq, Jxr)
        // we skip CompressHighlightsStep, so the plate can contain >1.0
        // overshoots. A per-channel clamp here would let one channel saturate
        // while the others stay, skewing the hue toward yellow / white. We
        // scale all three by 1/max instead, so the brightest channel lands
        // at 1.0 and the colour ratio is preserved (the overshoot desaturates
        // toward white). Mono path is unaffected (max == one channel always).
        var pixelCount = w * h;
        var rgba = new ushort[pixelCount * 4];
        var r = image.GetChannelSpan(0);
        var g = channels == 3 ? image.GetChannelSpan(1) : r;
        var b = channels == 3 ? image.GetChannelSpan(2) : r;
        for (var i = 0; i < pixelCount; i++)
        {
            var r0 = r[i];
            var g0 = g[i];
            var b0 = b[i];
            var maxV = MathF.Max(r0, MathF.Max(g0, b0));
            if (maxV > 1f)
            {
                var s = 1f / maxV;
                r0 *= s; g0 *= s; b0 *= s;
            }
            rgba[i * 4 + 0] = ToUShort(r0);
            rgba[i * 4 + 1] = ToUShort(g0);
            rgba[i * 4 + 2] = ToUShort(b0);
            rgba[i * 4 + 3] = 65535;
        }

        // cICP: sRGB by default; PQ for HDR10 with --png-pq-gamut choosing
        // canonical BT.2020-primaries (cICP {9, 16, 0, 1}) or narrow-gamut
        // sRGB-primaries (cICP {1, 16, 0, 1}). The encoding step rewrites
        // the rgba buffer in-place using the matching gamut math.
        CicpChunk cicp;
        if (hdr10Pq)
        {
            Bt2020Pq.EncodeInPlace(rgba, peakNits, gamutToBt2020);
            cicp = gamutToBt2020 ? CicpChunk.Hdr10Pq : CicpChunk.SrgbPq;
        }
        else
        {
            cicp = CicpChunk.Srgb;
        }

        var png = PngWriter.EncodeRgba16(rgba, w, h, new PngWriteOptions { Cicp = cicp });
        await File.WriteAllBytesAsync(pngPath, png, ct);
        var suffix = hdr10Pq
            ? $" (16-bit dual-stretch PNG, HDR PQ @ {peakNits:F0} nits, {(gamutToBt2020 ? "BT.2020" : "sRGB")} primaries)"
            : " (16-bit dual-stretch PNG, no re-stretch)";
        consoleHost.WriteScrollable($"[sharpen] wrote {pngPath}{suffix}");

        static ushort ToUShort(float v) => (ushort)Math.Clamp(v * 65535f + 0.5f, 0f, 65535f);
    }

    /// <summary>
    /// <c>--output-format</c> for one verb. The DESCRIPTION is the verb's own, because what the
    /// companion file is FOR differs between them (<c>render</c> emits the picture itself and
    /// defaults to PNG; the others emit it beside a FITS and default to none). Everything else --
    /// the name, the parser, the shape -- is the same everywhere and is written once here.
    /// </summary>
    private static Option<ImageOutputFormat> OutputFormatOption(
        string description, ImageOutputFormat defaultValue = ImageOutputFormat.None)
        => new("--output-format")
        {
            Description = description,
            DefaultValueFactory = _ => defaultValue,
            CustomParser = ParseOutputFormat,
        };

    /// <summary>
    /// The two HDR PQ companion options, which mean exactly the same thing for every verb that can
    /// emit a picture -- so unlike the format option above they carry no per-verb wording.
    /// <para>
    /// They were declared inline at four call sites, and the four had drifted into four different
    /// descriptions of one flag: <c>--png-pq-gamut</c> was documented as "skips the BT.2020 gamut
    /// matrix ... consumer viewers render this muted" on one verb and as "'srgb' (default) = cICP
    /// {1, 16, 0, 1}" on another. Nothing kept them in step and nothing ever would have, which is
    /// the whole argument for declaring a flag once.
    /// </para>
    /// </summary>
    private static (Option<float> PeakNits, Option<PngPqGamut> Gamut) HdrCompanionOptions()
        => (new Option<float>("--png-pq-peak-nits")
            {
                Description = "Peak display luminance assigned to stretched value 1.0 in HDR10 PQ output (--output-format png-pq). Cinema HDR10 typically grades at 1000; ITU-R BT.2408 reference white is 203; premium HDR targets 4000. Range (0, 10000]. Default 1000.",
                DefaultValueFactory = _ => 1000f,
            },
            new Option<PngPqGamut>("--png-pq-gamut")
            {
                Description = "Colour primaries for PNG-PQ output. 'srgb' (default) skips the BT.2020 gamut matrix; cICP {1, 16, 0, 1} tells viewers 'sRGB primaries, PQ transfer' so colours stay at sRGB saturation regardless of whether the viewer correctly inverts BT.2020-to-display. 'bt2020' performs the canonical sRGB-to-BT.2020 matrix conversion and tags cICP {9, 16, 0, 1} (HDR10 canonical) - correct per spec but consumer viewers that skip the inverse gamut tonemap render this muted.",
                DefaultValueFactory = _ => PngPqGamut.Srgb,
            });

    /// <summary>Parser for <c>--output-format</c>. Accepts the hyphenated CLI
    /// form (e.g. <c>png-pq</c>) in addition to the bare enum-identifier form
    /// (<c>PngPq</c>) that System.CommandLine's default enum parser would
    /// require. Case-insensitive; aliases (<c>hdr10</c>, <c>hdr10-pq</c>) map
    /// to <see cref="ImageOutputFormat.PngPq"/> because that's the standard
    /// industry name for the underlying signaling.</summary>
    private static ImageOutputFormat ParseOutputFormat(ArgumentResult arg)
    {
        var token = arg.Tokens.Count > 0 ? arg.Tokens[0].Value.ToLowerInvariant() : "none";
        return token switch
        {
            "none" => ImageOutputFormat.None,
            "png" => ImageOutputFormat.Png,
            "png-pq" or "pngpq" or "hdr10" or "hdr10-pq" => ImageOutputFormat.PngPq,
            "jxr" => ImageOutputFormat.Jxr,
            "exr" => ImageOutputFormat.Exr,
            "uhdr" or "ultrahdr" or "ultra-hdr" or "gainmap" or "gain-map" => ImageOutputFormat.UltraHdr,
            _ => throw new ArgumentException(
                $"--output-format: unknown value '{token}'; expected one of: none, png, png-pq, jxr, exr, uhdr"),
        };
    }

    /// <summary>
    /// <c>tianwen image sources</c>: the background map and the source segmentation on one channel of a
    /// frame, the segment summary printed, and on request the label map, the masks, the background and
    /// noise maps as FITS sidecars and the full segment table as CSV, every one named by a suffix on the
    /// frame's own name (<see cref="SourceDetectionWriter"/>). The detection is the library's; this verb
    /// only chooses the channel, hands over the options and puts the results where they were asked for.
    /// </summary>
    private Command BuildSourcesCommand()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "FITS frame to detect sources in.",
        };
        var channelOpt = new Option<int>("--channel")
        {
            Description = "Channel to detect on, 0-based. Default: the frame's reference star channel (green on a colour frame).",
            DefaultValueFactory = _ => -1,
        };
        var sigmaOpt = new Option<float>("--sigma")
        {
            Description = "Detection threshold in sigmas of the unsmoothed noise, applied to the smoothed sky-subtracted plane. Default 3.",
            DefaultValueFactory = _ => SourceDetectionOptions.Default.ThresholdSigma,
        };
        var minPixelsOpt = new Option<int>("--min-pixels")
        {
            Description = "Smallest segment kept, in pixels. Default 5.",
            DefaultValueFactory = _ => SourceDetectionOptions.Default.MinPixels,
        };
        var noDeblendOpt = new Option<bool>("--no-deblend")
        {
            Description = "Keep touching sources as one segment instead of splitting them at their saddles.",
        };
        var blockSizeOpt = new Option<int>("--block-size")
        {
            Description = "Background mesh cell, in pixels. Default 64; larger follows less structure.",
            DefaultValueFactory = _ => BackgroundMapOptions.Default.BlockSize,
        };
        var marginOpt = new Option<int>("--margin")
        {
            Description = "Dilation of the star and sky masks, in pixels. Default 3.",
            DefaultValueFactory = _ => 3,
        };
        var outOpt = new Option<string?>("--out", "-o")
        {
            Description = "Directory for the sidecars. Default: beside the input.",
        };
        var mapsOpt = new Option<bool>("--maps")
        {
            Description = "Write the label map, the star / structure / sky masks, the background and the noise as FITS sidecars " +
                          "(<stem>.labels.fits, .starmask.fits, .structmask.fits, .skymask.fits, .background.fits, .rms.fits), each with a MAPKIND card.",
        };
        var csvOpt = new Option<bool>("--csv")
        {
            Description = "Write the full segment table as <stem>.sources.csv.",
        };
        var topOpt = new Option<int>("--top")
        {
            Description = "Rows printed per class (the largest extended segments, then the largest compact ones). Default 10.",
            DefaultValueFactory = _ => 10,
        };

        var cmd = new Command("sources",
            "Detect sources: a mesh background and noise map, then segmentation of everything over the threshold, " +
            "deblended at its saddles, each segment classed compact (a star) or extended (structure). " +
            "Prints the summary; --maps and --csv put the label map, masks and table on disk beside the frame.")
        {
            Arguments = { inputArg },
            Options = { channelOpt, sigmaOpt, minPixelsOpt, noDeblendOpt, blockSizeOpt, marginOpt, outOpt, mapsOpt, csvOpt, topOpt },
        };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.Required(inputArg);
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

            var channel = parseResult.GetValue(channelOpt);
            if (channel < 0)
            {
                channel = src.ReferenceStarChannel;
            }
            else if (channel >= src.ChannelCount)
            {
                consoleHost.WriteError($"--channel {channel} is out of range for a {src.ChannelCount}-channel frame");
                return 1;
            }

            var margin = parseResult.GetValue(marginOpt);
            var top = parseResult.GetValue(topOpt);
            if (margin < 0 || top < 0)
            {
                consoleHost.WriteError("--margin and --top must not be negative");
                return 1;
            }

            var mapOptions = new BackgroundMapOptions(BlockSize: parseResult.GetValue(blockSizeOpt));
            var options = new SourceDetectionOptions(
                ThresholdSigma: parseResult.GetValue(sigmaOpt),
                MinPixels: parseResult.GetValue(minPixelsOpt),
                Deblend: !parseResult.GetValue(noDeblendOpt));
            try
            {
                mapOptions.Validate();
                options.Validate();
            }
            catch (ArgumentException ex)
            {
                consoleHost.WriteError(ex.Message);
                return 1;
            }

            var outDir = parseResult.GetValue(outOpt);
            var writeMaps = parseResult.GetValue(mapsOpt);
            var writeCsv = parseResult.GetValue(csvOpt);
            if (outDir is not null && (writeMaps || writeCsv))
            {
                Directory.CreateDirectory(outDir);
            }

            consoleHost.WriteScrollable($"[sources] {input} {src.Width}x{src.Height}x{src.ChannelCount}, channel {channel}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var background = BackgroundMap.Estimate(src, channel, mapOptions);
            var tMap = sw.Elapsed;
            sw.Restart();
            var segments = SourceSegmentation.Detect(src, channel, background, options);
            var tDetect = sw.Elapsed;

            var inv = CultureInfo.InvariantCulture;
            var compact = 0;
            foreach (var s in segments.Segments)
            {
                if (s.IsCompact)
                {
                    compact++;
                }
            }

            consoleHost.WriteScrollable(
                $"[sources] background {background.CellsX}x{background.CellsY} cells of {background.BlockSize} px in {tMap.TotalMilliseconds:F0} ms: " +
                $"sky {background.GlobalBackground.ToString("G5", inv)}, noise {background.GlobalRms.ToString("G5", inv)}");
            consoleHost.WriteScrollable(
                $"[sources] {segments.Segments.Length} segments in {tDetect.TotalMilliseconds:F0} ms: {compact} compact, {segments.Segments.Length - compact} extended " +
                $"(threshold {segments.ThresholdSigma.ToString("G3", inv)} sigma" +
                (segments.ThresholdSigma > options.ThresholdSigma ? $", raised from {options.ThresholdSigma.ToString("G3", inv)} by the crowded-field rule" : string.Empty) +
                $", min {options.MinPixels} px, deblend {(options.Deblend ? "on" : "off")})");

            if (top > 0 && segments.Segments.Length > 0)
            {
                var byArea = segments.Segments.Sort(static (a, b) => b.Area.CompareTo(a.Area));
                consoleHost.WriteScrollable(SegmentRowHeader);
                var printed = 0;
                foreach (var s in byArea)
                {
                    if (!s.IsCompact && printed++ < top)
                    {
                        consoleHost.WriteScrollable(FormatSegmentRow(s, background));
                    }
                }

                printed = 0;
                foreach (var s in byArea)
                {
                    if (s.IsCompact && printed++ < top)
                    {
                        consoleHost.WriteScrollable(FormatSegmentRow(s, background));
                    }
                }
            }

            if (writeMaps)
            {
                foreach (var path in SourceDetectionWriter.WriteMaps(input, segments, background, wcs, margin, outDir))
                {
                    consoleHost.WriteScrollable($"[sources] wrote {path}");
                }
            }

            if (writeCsv)
            {
                var tablePath = SourceDetectionWriter.SidecarPath(input, SourceDetectionWriter.TableSuffix, outDir);
                await SourceDetectionWriter.WriteTableAsync(tablePath, segments, background, wcs, ct);
                consoleHost.WriteScrollable($"[sources] wrote {tablePath} ({segments.Segments.Length} rows)");
            }

            return 0;
        });
        return cmd;
    }

    /// <summary>The column header <see cref="FormatSegmentRow"/>'s rows line up under.</summary>
    internal const string SegmentRowHeader = "  label     area        x        y  peak/rms  elong  core  pk/mean peaks class";

    /// <summary>One printed segment row: label, area, centroid, peak over the local noise, shape figures and class.</summary>
    internal static string FormatSegmentRow(Segment s, BackgroundMap background)
    {
        var inv = CultureInfo.InvariantCulture;
        var peakSnr = s.Peak / background.RmsAt(s.PeakX, s.PeakY);
        return string.Create(inv,
            $"{s.Label,7} {s.Area,8} {s.XCentroid,8:F1} {s.YCentroid,8:F1} {peakSnr,9:F1} {s.Elongation,6:F2} {s.CoreFraction,5:F2} {s.PeakToMean,8:F1} {s.PeakCount,5} {(s.IsCompact ? "compact" : "extended")}");
    }

    private static string DefaultOut(string input, string suffix)
        => StripExtension(input) + suffix + ".fits";

    /// <summary>
    /// Ensure <paramref name="path"/> ends with <c>.fits</c>. Used to harden
    /// the primary-output sites where the user-supplied <c>-o</c> path might
    /// arrive without an extension (otherwise we'd write a FITS file with no
    /// extension and the companion -- via <see cref="ReplaceExtension"/> --
    /// would end up at <c>&lt;path&gt;.png/.jxr</c> while the FITS sits
    /// extensionless, an obviously broken pair).
    /// </summary>
    private static string EnsureFitsExtension(string path)
        => path.EndsWith(".fits", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".fit", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".fts", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ".fits";

    private static string StripExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? path : path[..^ext.Length];
    }

    private static string ReplaceExtension(string path, string newExt)
        => StripExtension(path) + newExt;
}
