using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.CommandLine;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpAstro.Color.Icc;
using SharpAstro.Png;
using SharpAstro.Tiff;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.UI.Abstractions;

namespace TianWen.Cli;

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
        var noStellarOpt = new Option<bool>("--no-stellar-sharpen")
        {
            Description = "Skip the stellar-sharpening pass. Stars pass through unmodified into the recombine step (or output as-is when --no-recombine).",
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
        var pngOpt = new Option<bool>("--png")
        {
            Description = "Also write a stretched PNG preview alongside each output FITS (same render as 'tianwen stack' produces, so the sharpened result is visually comparable).",
        };
        var stellarBlendOpt = new Option<float>("--stellar-blend")
        {
            Description = "AI strength for the stellar sharpening pass in [0, 1]. 0 = stars untouched (= --no-stellar-sharpen); 1 = full AI output; ~0.5 is a typical good value for tight star fields where AI4 over-sharpens.",
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
        var ghsStarlessOpt = new Option<bool>("--ghs-starless")
        {
            Description = "Use Mike Cranfield's Generalised Hyperbolic Stretch on the starless plate (https://github.com/mikec1485/GHS) instead of MTF. Defaults match Paul (Polymath Astro)'s video walkthrough for case-1 (linear -> display): LnD=1.30, B=8.0 (hyperbolic branch), LP=0, HP=0.8, SP=auto (Image.EstimateRisingEdge), passes=1. See PLAN-ghs.md for the curve math.",
        };
        var ghsLnDOpt = new Option<double>("--ghs-lnd")
        {
            Description = "GHS stretch factor in the PixInsight slider convention -- the value the script displays is ln(D + 1); internally D = exp(LnD) - 1. 0 = identity. Default 1.30 = D~2.67. Implies --ghs-starless.",
            DefaultValueFactory = _ => 1.30,
        };
        var ghsBOpt = new Option<double>("--ghs-b")
        {
            Description = "GHS local stretch intensity (signed). B = 8 picks the hyperbolic / harmonic branch (Paul's case-1 default, lifts dim bg); B = -1 picks the logarithmic branch (good for case-2 local contrast on already-stretched input); B = 0 exponential; B < 0, != -1 power-with-negative-B. Larger |B| = more focused stretch around SP. Default 8.0. Implies --ghs-starless.",
            DefaultValueFactory = _ => 8.0,
        };
        var ghsLpOpt = new Option<double>("--ghs-lp")
        {
            Description = "GHS shadow protection point in [0, SP]. Below LP the curve is linear at the gradient evaluated at LP -- preserves shadow texture. Default 0. Implies --ghs-starless.",
            DefaultValueFactory = _ => 0.0,
        };
        var ghsHpOpt = new Option<double>("--ghs-hp")
        {
            Description = "GHS highlight protection point in [SP, 1]. Above HP the curve is linear at the gradient evaluated at HP -- prevents the upper tail from being compressed. Default 0.8 (Paul's recommendation). Implies --ghs-starless.",
            DefaultValueFactory = _ => 0.8,
        };
        var ghsSpOpt = new Option<double>("--ghs-sp")
        {
            Description = "GHS symmetry point -- the input pixel value where the curve has maximum gradient (its inflection point). Pass a value in (0, 1) to override; default <=0 means auto-detect via Image.EstimateRisingEdge (histogram lift-off). Implies --ghs-starless.",
            DefaultValueFactory = _ => -1.0,
        };
        var ghsPassesOpt = new Option<int>("--ghs-passes")
        {
            Description = "How many times to apply the GHS curve. Default 1. Range [1, 10].",
            DefaultValueFactory = _ => 1,
        };
        var ghsAutoOpt = new Option<bool>("--ghs-starless-auto")
        {
            Description = "Auto-tune --ghs-lnd via Image.ConvergeGhsStretchFactor: bisect LnD against the input plate's histogram until the post-stretch median lands at --ghs-target-median. B, SP, LP, HP stay caller-supplied; only LnD is solved. Implies --ghs-starless. The timing log line includes the converged LnD, achieved median, and the log-slope R^2 quality marker.",
        };
        var ghsAutoTargetMedianOpt = new Option<double>("--ghs-target-median")
        {
            Description = "Target post-stretch median for --ghs-starless-auto. Default 0.25 (SAS Pro / PixInsight statistical-stretch convention). Implies --ghs-starless-auto.",
            DefaultValueFactory = _ => 0.25,
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

        var cmd = new Command("sharpen", "Full AI4 NAFNet sharpen pipeline: remove stars, sharpen the stars-only plate, deconvolve + denoise the starless plate, optional SCNR on stars, recombine.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, modeOpt, noStellarOpt, noDeconvOpt, noDenoiseOpt, noRecombineOpt, pngOpt, stellarBlendOpt, deconvBlendOpt, denoiseBlendOpt, denoiseVariantOpt, scnrOpt, scnrAmountOpt, dualStretchOpt, stretchStarsAmountOpt, stretchStarlessMedianOpt, ghsStarlessOpt, ghsLnDOpt, ghsBOpt, ghsLpOpt, ghsHpOpt, ghsSpOpt, ghsPassesOpt, ghsAutoOpt, ghsAutoTargetMedianOpt, noReduceBgOpt, reduceBgCompressionOpt, noCompressHighlightsOpt, highlightKneeOpt, highlightAmountOpt },
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
            var dualStretch = parseResult.GetValue(dualStretchOpt);
            var starsAmount = Math.Clamp(parseResult.GetValue(stretchStarsAmountOpt), 0.1, 10.0);
            var starlessMedian = Math.Clamp(parseResult.GetValue(stretchStarlessMedianOpt), 0.01, 0.99);
            var noReduceBg = parseResult.GetValue(noReduceBgOpt);
            var reduceBgCompression = Math.Clamp(parseResult.GetValue(reduceBgCompressionOpt), 0.01, 1.0);
            var ghsStarless = parseResult.GetValue(ghsStarlessOpt);
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
            // --ghs-target-median below 1.0 implies --ghs-starless-auto
            // (default 0.25 leaves auto off; explicit target turns it on).
            var ghsAutoTargetMedian = Math.Clamp(parseResult.GetValue(ghsAutoTargetMedianOpt), 0.01, 0.99);
            var ghsAutoFlag = parseResult.GetValue(ghsAutoOpt);
            var ghsAuto = ghsAutoFlag;
            var ghsPasses = Math.Clamp(parseResult.GetValue(ghsPassesOpt), 1, 10);
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

            // Build the SharpenStep list in canonical order. CLI flags toggle
            // step presence; the pipeline interprets the array in declared
            // order so this is also the execution order.
            var steps = new List<SharpenStep> { new RemoveStarsStep(SplitMode: mode) };
            if (!noStellar) steps.Add(new SharpenStarsStep(Blend: stellarBlend));
            if (!noDeconv) steps.Add(new DeconvolveStarlessStep(Blend: deconvBlend));
            if (!noDenoise) steps.Add(new DenoiseStarlessStep(Blend: denoiseBlend, Variant: denoiseVariant));
            // Per-plate stretch (linear -> stretched) AFTER all AI ops.
            if (dualStretch)
            {
                steps.Add(new StretchStarsStep(Amount: starsAmount));
                // Pick MTF or GHS for the starless plate based on --ghs-starless.
                if (ghsStarless)
                    steps.Add(new GhsStretchStarlessStep(
                        LnD: ghsLnD,
                        B: ghsB,
                        SP: ghsSp,
                        LP: ghsLp,
                        HP: ghsHp,
                        Passes: ghsPasses,
                        AutoConverge: ghsAuto,
                        AutoTargetMedian: ghsAutoTargetMedian));
                else
                    steps.Add(new StretchStarlessStep(TargetMedian: starlessMedian));
                // S-curve background reduction on starless after stretch
                // (PixInsight statistical-stretch convention). Auto-detects
                // bg peak via histogram mode. Skippable with --no-reduce-bg.
                if (!noReduceBg) steps.Add(new BackgroundReduceStep(Compression: reduceBgCompression));
                // Reinhard-style soft highlight compression on the same
                // starless plate -- prevents the central-nebula core from
                // blowing out after the dual-stretch. Asymmetric companion
                // to the bg-reduce step; together they reproduce the SAS Pro
                // statistical-stretch shape.
                if (!noCompressHighlights) steps.Add(new CompressHighlightsStep(Knee: highlightKnee, Amount: highlightAmount));
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
            if (!noRecombine) steps.Add(new RecombineStep(Mode: recombineMode));

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
            var lnDDesc = ghsAuto ? $"lnD~auto(t={ghsAutoTargetMedian:F2})" : $"lnD{ghsLnD:F2}";
            var starlessStretchDesc = ghsStarless
                ? $"ghs({lnDDesc}/b{ghsB:F2}/sp{spDesc}/lp{ghsLp:F2}/hp{ghsHp:F2}/{ghsPasses}x)"
                : $"mtf-tm={starlessMedian:F2}";
            var dualStretchDesc = dualStretch
                ? $" dual-stretch(stars-amount={starsAmount:F2},starless={starlessStretchDesc}) reduce-bg={(!noReduceBg ? reduceBgCompression.ToString("F2") : "off")} compress-hi={(!noCompressHighlights ? $"k{highlightKnee:F2}/a{highlightAmount:F2}" : "off")}"
                : "";
            consoleHost.WriteScrollable(
                $"[sharpen] {input} {src.Width}x{src.Height}x{src.ChannelCount} mode={mode} " +
                $"stellar={!noStellar}({stellarBlend:F2}) deconv={!noDeconv}({deconvBlend:F2}) " +
                $"denoise={!noDenoise}({denoiseBlend:F2},{denoiseVariant}) scnr={effectiveScnrMode}({scnrAmount:F2}){dualStretchDesc} recombine={!noRecombine}");

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

            var withPng = parseResult.GetValue(pngOpt);

            if (noRecombine)
            {
                // Per-plate output: derive each path from the explicit output (if
                // any) or from the input. Skip plates that weren't produced.
                var basePath = outputPath ?? StripExtension(input);
                await WritePlateAsync(result.Starless, basePath, "_starless", wcs, src.ImageMeta, withPng, ct);
                await WritePlateAsync(result.StarsOnly, basePath, "_stars", wcs, src.ImageMeta, withPng, ct);
                await WritePlateAsync(result.SharpenedStars, basePath, "_sharpened-stars", wcs, src.ImageMeta, withPng, ct);
                await WritePlateAsync(result.DeconvolvedStarless, basePath, "_deconvolved-starless", wcs, src.ImageMeta, withPng, ct);
                await WritePlateAsync(result.DenoisedStarless, basePath, "_denoised-starless", wcs, src.ImageMeta, withPng, ct);
            }
            else if (result.Final is { } finalImage)
            {
                var dst = outputPath ?? DefaultOut(input, "_sharpened");
                finalImage.WriteToFitsFile(dst, wcs);
                consoleHost.WriteScrollable($"[sharpen] wrote {dst}");
                if (withPng)
                {
                    // For dual-stretch: composite is already in stretched
                    // [0, 1] space (per-plate MTF + screen recombine), so
                    // running MasterPreviewRenderer would auto-MTF again
                    // (double-stretch). Just byte-encode + sRGB tag. For
                    // the non-dual-stretch path, fall through to the
                    // existing renderer which does the single auto-stretch.
                    var pngPath = ReplaceExtension(dst, ".png");
                    if (dualStretch)
                        await WriteStretchedPngAsync(finalImage, pngPath, ct);
                    else
                        await RenderPngAsync(finalImage, src.ImageMeta, wcs, pngPath, ct);
                }
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

        var pngOpt = new Option<bool>("--png")
        {
            Description = "Also write a stretched PNG preview alongside the FITS output.",
        };

        var cmd = new Command("remove-stars", "AI4 NAFNet star removal only. Produces a starless export.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, pngOpt },
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
            if (parseResult.GetValue(pngOpt))
            {
                await RenderPngAsync(starless, src.ImageMeta, wcs, ReplaceExtension(dst, ".png"), ct);
            }
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
        var pngOpt = new Option<bool>("--png")
        {
            Description = "Also write a stretched PNG preview alongside the FITS output.",
        };
        var saveGradientOpt = new Option<bool>("--save-gradient")
        {
            Description = "Also write the estimated background surface as <output>_gradient.fits (+ .png if --png is set). Useful for sanity-checking the gradient model -- you can see whether it picked up light pollution vs vignette vs sky-glow asymmetry. Skipped by default to avoid leaking a 120 MB plate per call on large drizzles.",
        };

        var cmd = new Command("flatten", "AI gradient correction via GraXpert BGE ONNX (subtractive). " +
            "Estimates the smooth background (light pollution, vignette, sky-glow asymmetry) and " +
            "subtracts it while preserving the mean sky level. Runs at the head of the canonical " +
            "Frank Sackenheim flow (gradient -> stars -> detail -> stretch). Requires the GraXpert " +
            "BGE model materialised via tools/tianwen-ai-models-fetch.ps1.")
        {
            Arguments = { inputArg },
            Options = { outputOpt, pngOpt, saveGradientOpt },
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
            var withPng = parseResult.GetValue(pngOpt);
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

            var dst = parseResult.GetValue(outputOpt) ?? DefaultOut(input, "_flattened");
            flattened.WriteToFitsFile(dst, wcs);
            consoleHost.WriteScrollable($"[flatten] wrote {dst}");
            if (withPng)
            {
                await RenderPngAsync(flattened, src.ImageMeta, wcs, ReplaceExtension(dst, ".png"), ct);
            }
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
                    if (withPng)
                    {
                        // Min-max contrast stretch -- the gradient is a smooth
                        // low-amplitude surface, MasterPreviewRenderer's
                        // SPCC + bg-neut + auto-MTF crushes the very signal
                        // we want to see. Logs per-channel amplitude so the
                        // operator can tell whether the model thinks there
                        // IS a gradient (informative output) or whether it
                        // settled on essentially uniform (suspicious).
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
            Description = "FITS file to render to PNG.",
        };
        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output PNG path. Default: <input>.png.",
        };

        var cmd = new Command("render", "Render a FITS file to a stretched PNG using the same renderer as 'tianwen stack' (SPCC + sky-bg WB + bg-neut + stretch + sRGB ICC).")
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
            var dst = parseResult.GetValue(outputOpt) ?? ReplaceExtension(input, ".png");
            consoleHost.WriteScrollable(
                $"[render] {input} {src.Width}x{src.Height}x{src.ChannelCount} -> {dst}");
            await RenderPngAsync(src, src.ImageMeta, wcs, dst, ct);
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
            Description = "Minimum star SNR for detection. Default 20 -- matches FindStarsAsync default. Lower values pick up more (noisier) stars.",
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
    /// by a same-stem PNG via <see cref="RenderPngAsync"/>. Used for both the
    /// recombined output and the per-plate exports from <c>--no-recombine</c>.
    /// </summary>
    private async Task WritePlateAsync(Image? plate, string basePath, string suffix, WCS? wcs, ImageMeta sensorMeta, bool withPng, CancellationToken ct)
    {
        if (plate is null) return;
        var path = basePath.EndsWith(".fits", StringComparison.OrdinalIgnoreCase)
            ? StripExtension(basePath) + suffix + ".fits"
            : basePath + suffix + ".fits";
        plate.WriteToFitsFile(path, wcs);
        consoleHost.WriteScrollable($"[sharpen] wrote {path}");
        if (withPng) await RenderPngAsync(plate, sensorMeta, wcs, ReplaceExtension(path, ".png"), ct);
    }

    /// <summary>
    /// Run the shared <see cref="MasterPreviewRenderer"/> -- same path the
    /// stack subcommand uses for its <c>master_*.png</c> -- against
    /// <paramref name="img"/>. SPCC is computed at render time and only
    /// baked into the PNG; the source FITS stays untouched.
    /// </summary>
    private async Task RenderPngAsync(Image img, ImageMeta sensorMeta, WCS? wcs, string pngPath, CancellationToken ct)
    {
        try
        {
            await previewRenderer.RenderAsync(img, sensorMeta, wcs, statsSource: null, pngPath, ct: ct);
            consoleHost.WriteScrollable($"[render] wrote {pngPath}");
        }
        catch (Exception ex)
        {
            consoleHost.WriteError($"PNG render failed for {pngPath}: {ex.Message}");
            logger?.LogError(ex, "PNG render failed for {Path}", pngPath);
        }
    }

    /// <summary>
    /// Write an <see cref="Image"/> with [0, 1] float values as a 32-bit
    /// IEEE float TIFF tagged with sRGB v4 ICC. The MTF tone curve in the
    /// stretched plates isn't exactly sRGB gamma, but it's close enough
    /// that colour-managed viewers (PixInsight, Photoshop, Affinity, browsers)
    /// will display the data sensibly. Used for the per-plate dual-stretch
    /// export so users can layer stars + starless in PS/Affinity with the
    /// "Screen" blend mode to reproduce the in-pipeline recombine.
    /// </summary>
    private async Task WriteStretchedFloatTiffAsync(Image image, string path, CancellationToken ct)
    {
        var (channels, w, h) = image.Shape;
        if (channels is not (1 or 3))
        {
            consoleHost.WriteError($"TIFF export requires 1 or 3 channels, got {channels}; skipping {path}");
            return;
        }

        // Allocate the byte buffer directly and write floats into it via
        // MemoryMarshal -- avoids the float[] then byte[] double allocation.
        // Layout is contig (PlanarConfig=1): RGBRGBRGB for color, just G for mono.
        var pixelCount = w * h;
        var totalFloats = pixelCount * channels;
        var byteBuffer = new byte[totalFloats * sizeof(float)];
        var floatView = MemoryMarshal.Cast<byte, float>(byteBuffer.AsSpan());

        if (channels == 1)
        {
            image.GetChannelSpan(0).CopyTo(floatView);
        }
        else
        {
            var r = image.GetChannelSpan(0);
            var g = image.GetChannelSpan(1);
            var b = image.GetChannelSpan(2);
            for (var i = 0; i < pixelCount; i++)
            {
                floatView[i * 3 + 0] = r[i];
                floatView[i * 3 + 1] = g[i];
                floatView[i * 3 + 2] = b[i];
            }
        }

        var options = new TiffPageOptions
        {
            SampleFormat = TiffSampleFormat.IeeeFloat,
            BitsPerSample = 32,
            SamplesPerPixel = channels,
            Photometric = channels == 1 ? TiffPhotometric.MinIsBlack : TiffPhotometric.Rgb,
            IccProfile = IccProfiles.SRgbV4,
            SMinSampleValue = 0f,
            SMaxSampleValue = 1f,
            Software = "TianWen.Cli (dual-stretch)",
        };

        await using var writer = TiffWriter.Create(path);
        await writer.AddPageAsync(byteBuffer, w, h, options, ct);
        await writer.FlushAsync(ct);
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

        var png = PngWriter.Encode(rgba, w, h, IccProfiles.SRgbV4.Span);
        await File.WriteAllBytesAsync(pngPath, png, ct);
        consoleHost.WriteScrollable($"[{tag}] wrote {pngPath} (min-max contrast)");

        static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);
    }

    private async Task WriteStretchedPngAsync(Image image, string pngPath, CancellationToken ct)
    {
        var (channels, w, h) = image.Shape;
        if (channels is not (1 or 3))
        {
            consoleHost.WriteError($"PNG export requires 1 or 3 channels, got {channels}; skipping {pngPath}");
            return;
        }

        // RGBA interleaved 8-bit (alpha=255). Mono replicates the single
        // channel into R/G/B so the PNG file is RGB-encoded (PngWriter
        // doesn't have a Gray entry point and replicating is cheap).
        var pixelCount = w * h;
        var rgba = new byte[pixelCount * 4];
        var r = image.GetChannelSpan(0);
        var g = channels == 3 ? image.GetChannelSpan(1) : r;
        var b = channels == 3 ? image.GetChannelSpan(2) : r;
        for (var i = 0; i < pixelCount; i++)
        {
            rgba[i * 4 + 0] = ToByte(r[i]);
            rgba[i * 4 + 1] = ToByte(g[i]);
            rgba[i * 4 + 2] = ToByte(b[i]);
            rgba[i * 4 + 3] = 255;
        }

        var png = PngWriter.Encode(rgba, w, h, IccProfiles.SRgbV4.Span);
        await File.WriteAllBytesAsync(pngPath, png, ct);
        consoleHost.WriteScrollable($"[sharpen] wrote {pngPath} (dual-stretch PNG, no re-stretch)");

        static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);
    }

    private static string DefaultOut(string input, string suffix)
        => StripExtension(input) + suffix + ".fits";

    private static string StripExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? path : path[..^ext.Length];
    }

    private static string ReplaceExtension(string path, string newExt)
        => StripExtension(path) + newExt;
}
