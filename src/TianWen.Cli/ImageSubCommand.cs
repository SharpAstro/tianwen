using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Png;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
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
/// </list></summary>
public enum ImageOutputFormat { None, Png, PngPq, Jxr, Exr }

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
/// AI enhancers go through the AI4 NAFNet pipeline wired by
/// <c>services.AddTianWenAi()</c>. Input is normalised to <c>[0, 1]</c>
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
    MasterPreviewRenderer previewRenderer,
    ILogger<ImageSubCommand>? logger = null)
{
    public Command Build()
    {
        var image = new Command("image", "Single-image enhancement + render verbs (sharpen, remove-stars, flatten, render, stats).")
        {
            Subcommands =
            {
                BuildSharpenCommand(),
                BuildRemoveStarsCommand(),
                BuildFlattenCommand(),
                BuildRenderCommand(),
                BuildStatsCommand(),
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
            Description = "Output FITS path. Default: <input>_sharpened.fits (or per-plate <input>_{starless,stars,sharpened-stars,deconvolved-starless,denoised-starless}.fits when --no-recombine is set).",
        };
        var modeOpt = new Option<string>("--mode")
        {
            Description = "Split/recombine math: 'additive' (default, linear-light correct) or 'screen' (matches NAFNet's stretched-space training identity).",
            DefaultValueFactory = _ => "additive",
        };
        var stellarSharpenOpt = new Option<bool>("--stellar-sharpen")
        {
            Description = "Opt in to the SAS stellar-sharpening pass on the extracted stars (default OFF). Stars from a registered/drizzled stack are already round, and the SAS NAFNet over-sharpens bright cores - it pushes them past 1.0 (hard clamp) and hardens the edges into square white blocks. Left off, stars pass through to StarStretch/recombine unmodified. Hard override: when a BlurX deblurrer is live (RC-Astro present) this pass is skipped even if requested, since the BlurX-first flow deblurs whole-frame before star extraction.",
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
        var formatOpt = new Option<ImageOutputFormat>("--output-format")
        {
            Description = "2D-viewer companion file alongside each output FITS. 'none' (default) = FITS only. 'png' = 16-bit RGBA + cICP sRGB (SDR display-referred). 'png-pq' = 16-bit RGBA + cICP HDR10 (BT.2020 + PQ); Affinity Photo honours the cICP HDR signal and shows it correctly, but Windows 11 Photos ignores cICP and displays the PQ samples as sRGB (looks muted). 'jxr' = JPEG XR with float-true HDR pixels (BD32F mono / BD16F RGB); writes the post-pipeline plate verbatim, skips Reinhard highlight knee so >1.0 overshoots survive. Per-plate dual-stretch float TIFFs are unaffected.",
            DefaultValueFactory = _ => ImageOutputFormat.None,
            CustomParser = ParseOutputFormat,
        };
        var pngPqPeakNitsOpt = new Option<float>("--png-pq-peak-nits")
        {
            Description = "Peak display luminance assigned to stretched value 1.0 in HDR10 PQ output (--output-format png-pq). Cinema HDR10 typically grades at 1000; ITU-R BT.2408 reference white is 203; premium HDR targets 4000. Range (0, 10000]. Default 1000.",
            DefaultValueFactory = _ => 1000f,
        };
        var pngPqGamutOpt = new Option<PngPqGamut>("--png-pq-gamut")
        {
            Description = "Colour primaries for PNG-PQ output. 'srgb' (default) skips the BT.2020 gamut matrix; cICP {1, 16, 0, 1} tells viewers 'sRGB primaries, PQ transfer' so colours stay at sRGB saturation regardless of whether the viewer correctly inverts BT.2020-to-display. 'bt2020' performs the canonical sRGB-to-BT.2020 matrix conversion and tags cICP {9, 16, 0, 1} (HDR10 canonical) - correct per spec but consumer viewers that skip the inverse gamut tonemap render this muted.",
            DefaultValueFactory = _ => PngPqGamut.Srgb,
        };
        var stellarBlendOpt = new Option<float>("--stellar-blend")
        {
            Description = "AI strength for the stellar sharpening pass in [0, 1], applied only when --stellar-sharpen is set. 0 = stars untouched; 1 = full AI output; ~0.5 is a typical good value for tight star fields where AI4 over-sharpens.",
            DefaultValueFactory = _ => 1.0f,
        };
        var deconvBlendOpt = new Option<float>("--deconv-blend")
        {
            Description = "AI strength for the non-stellar deconvolution pass in [0, 1]. 0 = nebula untouched; 1 = full AI output. Nebula usually tolerates higher values than stellar sharpening.",
            DefaultValueFactory = _ => 1.0f,
        };
        var denoiseBlendOpt = new Option<float>("--denoise-blend")
        {
            Description = "AI strength for the denoise pass on the starless plate in [0, 1]. 0 = noise untouched; 1 = full AI output. AI4 NoiseX is conservative on faint nebula detail so full strength is usually safe.",
            DefaultValueFactory = _ => 1.0f,
        };
        var denoiseVariantOpt = new Option<string>("--denoise-variant")
        {
            Description = "AI4 denoise weight bundle: 'default' (full NAFNet, slowest+best), 'lite' (half-width, ~2x faster), or 'walking' (trained on dither-correlated pattern noise).",
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

        // RC-Astro vs SAS backend control + per-product strength overrides (Phase 3a).
        var aiBackendOpt = new Option<string>("--ai-backend")
        {
            Description = "AI enhancer backend for the RC-servable roles (star removal / deblur / deconvolution / denoise): 'auto' (RC-Astro when present + licensed, else SAS ONNX - default), 'rc' (force RC-Astro whenever the CLI is installed, skipping the license probe), or 'sas' (force SAS ONNX even when RC-Astro is licensed). No effect on stellar-sharpen / gradient-correction (SAS-only).",
            DefaultValueFactory = _ => "auto",
        };
        var bxtSharpenOpt = new Option<double>("--bxt-sharpen")
        {
            Description = "RC-Astro BlurXTerminator non-stellar sharpen (bxt --sn) in [0, 1], applied to the full-image deblur and the starless deconvolution. < 0 (default) = the enhancer's own default (0.90). Only affects the RC-Astro backend.",
            DefaultValueFactory = _ => -1.0,
        };
        var nxtDenoiseOpt = new Option<double>("--nxt-denoise")
        {
            Description = "RC-Astro NoiseXTerminator strength (nxt --dn) in [0, 1]. < 0 (default) = noise-adaptive auto. Only affects the RC-Astro backend.",
            DefaultValueFactory = _ => -1.0,
        };
        var nxtIterationsOpt = new Option<int>("--nxt-iterations")
        {
            Description = "RC-Astro NoiseXTerminator iterations (nxt --it). < 1 (default) = the enhancer's own default (2). Only affects the RC-Astro backend.",
            DefaultValueFactory = _ => 0,
        };

        var cmd = new Command("sharpen", "Full AI4 NAFNet sharpen pipeline: remove stars, sharpen the stars-only plate, deconvolve + denoise the starless plate, optional SCNR on stars, recombine.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, modeOpt, stellarSharpenOpt, noDeconvOpt, noDenoiseOpt, noRecombineOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt, stellarBlendOpt, deconvBlendOpt, denoiseBlendOpt, denoiseVariantOpt, scnrOpt, scnrAmountOpt, dualStretchOpt, stretchStarsAmountOpt, stretchStarlessMedianOpt, starStretchModeOpt, starlessStretchModeOpt, stretchModeOpt, ghsConvergeOpt, ghsLnDOpt, ghsBOpt, ghsLpOpt, ghsHpOpt, ghsSpOpt, ghsPassesOpt, ghsStagesOpt, ghsAutoTargetValueOpt, ghsAutoTargetOpt, asinhBetaOpt, asinhBlackPointOpt, asinhLumaOpt, noReduceBgOpt, reduceBgCompressionOpt, noCompressHighlightsOpt, highlightKneeOpt, highlightAmountOpt, aiBackendOpt, bxtSharpenOpt, nxtDenoiseOpt, nxtIterationsOpt },
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

            var bxtSharpen = parseResult.GetValue(bxtSharpenOpt);
            var nxtDenoise = parseResult.GetValue(nxtDenoiseOpt);
            var nxtIterations = parseResult.GetValue(nxtIterationsOpt);
            // Backend + per-product tuning parse is shared with `stack --enhance` and the server
            // enhance endpoint via EnhanceOptions.TryParse (single source of truth). CLI sentinels
            // (-1 / 0 = "unset") map to a null override before the call.
            if (!EnhanceOptions.TryParse(
                    parseResult.GetValue(aiBackendOpt),
                    bxtSharpen >= 0 ? (float)bxtSharpen : null,
                    nxtDenoise >= 0 ? (float)nxtDenoise : null,
                    nxtIterations >= 1 ? nxtIterations : null,
                    out var enhanceOptions, out var enhanceError))
            {
                consoleHost.WriteError(enhanceError!);
                return 1;
            }
            var backend = enhanceOptions.Backend;
            var tuning = enhanceOptions.Tuning;

            var stellarOptIn = parseResult.GetValue(stellarSharpenOpt);
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

            // Auto-enable SCNR on the stars plate when --dual-stretch is set
            // and the user didn't explicitly choose a mode. Green stars are
            // the dominant artefact of stretching faint-star noise where the
            // G channel slightly outpaces R/B; SCNR neutralises it. Stretched
            // space is where SCNR has the strongest visible effect, so it
            // belongs AFTER StretchStarsStep in the order below.
            var effectiveScnrMode = dualStretch && scnrMode == ScnrMode.None ? ScnrMode.Average : scnrMode;

            // Stellar-sharpen is opt-in (default OFF). Stars from a registered/
            // drizzled stack are already round, and the SAS NAFNet over-sharpens
            // bright cores -- it pushes them past 1.0 (hard clamp) and hardens the
            // edges into square white blocks (measured ~89k clipped px on a dense
            // field; 0 with it off). So it stays off unless --stellar-sharpen is
            // passed. Hard override: when a BlurX deblurrer is live (RC-Astro
            // present) it is skipped even if opted in -- the BlurX-first (PixInsight
            // OSC) flow deblurs whole-frame before star extraction, so re-sharpening
            // the split stars double-dips.
            var deblurLive = sharpenPipeline.SupportsDeblur;
            var doStellar = stellarOptIn && !deblurLive;
            if (stellarOptIn && deblurLive)
            {
                consoleHost.WriteScrollable(
                    "[sharpen] --stellar-sharpen ignored: BlurX deblurrer live (deblur is whole-frame upstream; re-sharpening extracted stars over-sharpens).");
            }

            // Build the SharpenStep list in canonical order. CLI flags toggle
            // step presence; the pipeline interprets the array in declared
            // order so this is also the execution order.
            var steps = new List<SharpenStep> { new RemoveStarsStep(SplitMode: mode) };
            if (doStellar) steps.Add(new SharpenStarsStep(Blend: stellarBlend));
            if (!noDeconv) steps.Add(new DeconvolveStarlessStep(Blend: deconvBlend));
            if (!noDenoise) steps.Add(new DenoiseStarlessStep(Blend: denoiseBlend, Variant: denoiseVariant));
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
            // in stretched space has the highest visible benefit.
            if (effectiveScnrMode != ScnrMode.None) steps.Add(new ScnrStarsStep(Mode: effectiveScnrMode, Amount: scnrAmount));
            // Dual-stretch produces plates already in [0, 1] stretched space.
            // Additive sum saturates at 1.0; screen is the natural bounded
            // composite: Final = 1 - (1-bg)(1-fg). Split stays in linear
            // (mode controls that).
            var recombineMode = dualStretch ? RecombineMode.Screen : mode;
            if (!noRecombine)
            {
                steps.Add(new RecombineStep(Mode: recombineMode));
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
                finalImage.WriteToFitsFile(dst, wcs);
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

        var formatOpt = new Option<ImageOutputFormat>("--output-format")
        {
            Description = "2D-viewer companion file alongside the FITS output. 'none' (default) = no companion. 'png' = 16-bit RGBA + cICP sRGB (SDR). 'png-pq' = 16-bit RGBA + cICP HDR10 PQ (HDR display). 'jxr' = JPEG XR with float-true HDR pixels.",
            DefaultValueFactory = _ => ImageOutputFormat.None,
            CustomParser = ParseOutputFormat,
        };
        var pngPqPeakNitsOpt = new Option<float>("--png-pq-peak-nits")
        {
            Description = "Peak luminance for HDR PQ output (--output-format png-pq). Range (0, 10000]. Default 1000.",
            DefaultValueFactory = _ => 1000f,
        };
        var pngPqGamutOpt = new Option<PngPqGamut>("--png-pq-gamut")
        {
            Description = "Colour primaries for HDR PQ output (--output-format png-pq). 'srgb' (default) keeps sRGB primaries, cICP {1, 16, 0, 1}. 'bt2020' applies sRGB-to-BT.2020 matrix, cICP {9, 16, 0, 1} = canonical HDR10.",
            DefaultValueFactory = _ => PngPqGamut.Srgb,
        };

        var cmd = new Command("remove-stars", "AI4 NAFNet star removal only. Produces a starless export.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt },
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

            var dst = EnsureFitsExtension(parseResult.GetValue(outputOpt) ?? DefaultOut(input, "_starless"));
            starless.WriteToFitsFile(dst, wcs);
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
        var formatOpt = new Option<ImageOutputFormat>("--output-format")
        {
            Description = "2D-viewer companion file alongside the FITS output. 'none' (default) = no companion. 'png' = 16-bit cICP sRGB (SDR). 'png-pq' = 16-bit cICP HDR10 PQ. 'jxr' = float-true HDR. The --save-gradient surface PNG is unaffected - it stays PNG (min-max contrast visualisation, not banding-sensitive).",
            DefaultValueFactory = _ => ImageOutputFormat.None,
            CustomParser = ParseOutputFormat,
        };
        var pngPqPeakNitsOpt = new Option<float>("--png-pq-peak-nits")
        {
            Description = "Peak luminance for HDR PQ output (--output-format png-pq). Range (0, 10000]. Default 1000.",
            DefaultValueFactory = _ => 1000f,
        };
        var pngPqGamutOpt = new Option<PngPqGamut>("--png-pq-gamut")
        {
            Description = "Colour primaries for HDR PQ output. 'srgb' (default) keeps sRGB primaries; 'bt2020' applies sRGB-to-BT.2020 matrix = canonical HDR10.",
            DefaultValueFactory = _ => PngPqGamut.Srgb,
        };
        var saveGradientOpt = new Option<bool>("--save-gradient")
        {
            Description = "Also write the estimated background surface as <output>_gradient.fits (+ .png if --png is set). Useful for sanity-checking the gradient model - you can see whether it picked up light pollution vs vignette vs sky-glow asymmetry. Skipped by default to avoid leaking a 120 MB plate per call on large drizzles.",
        };

        var cmd = new Command("flatten", "AI gradient correction via GraXpert BGE ONNX (subtractive). " +
            "Estimates the smooth background (light pollution, vignette, sky-glow asymmetry) and " +
            "subtracts it while preserving the mean sky level. Runs at the head of the canonical " +
            "Frank Sackenheim flow (gradient -> stars -> detail -> stretch). Requires the GraXpert " +
            "BGE model materialised via tools/tianwen-ai-models-fetch.ps1.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt, saveGradientOpt },
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
            var format = parseResult.GetValue(formatOpt);
            var peakNits = Math.Clamp(parseResult.GetValue(pngPqPeakNitsOpt), 1f, 10000f);
            var gamutToBt2020 = parseResult.GetValue(pngPqGamutOpt) == PngPqGamut.Bt2020;
            var saveGradient = parseResult.GetValue(saveGradientOpt);

            consoleHost.WriteScrollable(
                $"[flatten] {input} {src.Width}x{src.Height}x{src.ChannelCount}{(saveGradient ? " save-gradient=true" : "")}");

            Image flattened;
            Image? background = null;
            try
            {
                if (saveGradient)
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
            flattened.WriteToFitsFile(dst, wcs);
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
                    background.WriteToFitsFile(gradientDst, wcs);
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
        var formatOpt = new Option<ImageOutputFormat>("--output-format")
        {
            Description = "Output container. 'png' (default) = 16-bit RGBA + cICP sRGB via MasterPreviewRenderer (SPCC + sky-bg WB + bg-neut + stretch). 'png-pq' = 16-bit RGBA + cICP HDR10 PQ (BT.2020 + SMPTE 2084); modern browsers / HDR displays render as actual HDR at --png-pq-peak-nits peak. 'jxr' = JPEG XR with float-true HDR pixels (BD32F mono / BD16F RGB); writes the input verbatim, NO SPCC / WB / stretch.",
            DefaultValueFactory = _ => ImageOutputFormat.Png,
            CustomParser = ParseOutputFormat,
        };
        var pngPqPeakNitsOpt = new Option<float>("--png-pq-peak-nits")
        {
            Description = "Peak luminance for HDR PQ output (--output-format png-pq). Range (0, 10000]. Default 1000.",
            DefaultValueFactory = _ => 1000f,
        };
        var pngPqGamutOpt = new Option<PngPqGamut>("--png-pq-gamut")
        {
            Description = "Colour primaries for HDR PQ output. 'srgb' (default) = cICP {1, 16, 0, 1}; 'bt2020' = canonical HDR10 cICP {9, 16, 0, 1}.",
            DefaultValueFactory = _ => PngPqGamut.Srgb,
        };

        var cmd = new Command("render", "Render a FITS file to a stretched PNG (default), HDR PQ PNG (--output-format png-pq), or float-true HDR JPEG XR (--output-format jxr).")
        {
            Arguments = { inputArg },
            Options = { outputOpt, formatOpt, pngPqPeakNitsOpt, pngPqGamutOpt },
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
            // `render` writes the chosen format AS the primary output; passing
            // primaryPath = dst means ReplaceExtension is a no-op when the
            // extension already matches. ImageOutputFormat.None was rejected
            // above, so this always emits one file.
            var dst = parseResult.GetValue(outputOpt) ?? ReplaceExtension(input, ExtensionFor(format));
            consoleHost.WriteScrollable(
                $"[render] {input} {src.Width}x{src.Height}x{src.ChannelCount} -> {dst} ({format.ToString().ToLowerInvariant()})");
            await WriteCompanionAsync(src, dst, format, src.ImageMeta, wcs, "render",
                useStretchedPng: false,
                peakNits: Math.Clamp(parseResult.GetValue(pngPqPeakNitsOpt), 1f, 10000f),
                gamutToBt2020: parseResult.GetValue(pngPqGamutOpt) == PngPqGamut.Bt2020,
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
            var input = parseResult.GetValue(inputArg)!;
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
        plate.WriteToFitsFile(path, wcs);
        consoleHost.WriteScrollable($"[sharpen] wrote {path}");
        await WriteCompanionAsync(plate, path, format, sensorMeta, wcs, "sharpen",
            useStretchedPng: false, peakNits: peakNits, gamutToBt2020: gamutToBt2020, ct: ct);
    }

    /// <summary>File extension (with leading dot) for the chosen companion format.</summary>
    private static string ExtensionFor(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.Jxr => ".jxr",
        ImageOutputFormat.Exr => ".exr",
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
    private async Task WriteCompanionAsync(
        Image image, string primaryPath, ImageOutputFormat format,
        ImageMeta sensorMeta, WCS? wcs, string tag,
        bool useStretchedPng,
        float peakNits,
        bool gamutToBt2020,
        CancellationToken ct)
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
                await RenderPngAsync(image, sensorMeta, wcs, path, hdr10Pq: false, peakNits, gamutToBt2020, ct);
                break;
            case ImageOutputFormat.PngPq:
                await RenderPngAsync(image, sensorMeta, wcs, path, hdr10Pq: true, peakNits, gamutToBt2020, ct);
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
    private async Task RenderPngAsync(Image img, ImageMeta sensorMeta, WCS? wcs, string pngPath, bool hdr10Pq, float peakNits, bool gamutToBt2020, CancellationToken ct)
    {
        try
        {
            await previewRenderer.RenderAsync(img, sensorMeta, wcs, statsSource: null, pngPath,
                hdr10Pq: hdr10Pq, peakNits: peakNits, gamutToBt2020: gamutToBt2020, ct: ct);
            var suffix = hdr10Pq
                ? $" (HDR PQ, {peakNits:F0} nits, {(gamutToBt2020 ? "BT.2020" : "sRGB")} primaries)"
                : "";
            consoleHost.WriteScrollable($"[render] wrote {pngPath}{suffix}");
        }
        catch (Exception ex)
        {
            consoleHost.WriteError($"PNG render failed for {pngPath}: {ex.Message}");
            logger?.LogError(ex, "PNG render failed for {Path}", pngPath);
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
            _ => throw new ArgumentException(
                $"--output-format: unknown value '{token}'; expected one of: none, png, png-pq, jxr"),
        };
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
