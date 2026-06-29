using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Composes the AI4 enhancers into a sharpen flow described by a sequence
/// of strongly-typed <see cref="SharpenStep"/> records: each step bundles
/// "what to do" with its own parameters (blend amount, mode, etc.).
/// Pipeline execution honours <see cref="SharpenRequest.Steps"/> order
/// strictly -- the request is the program, the pipeline is the interpreter.
/// </summary>
/// <remarks>
/// <para>Lives in <c>TianWen.Lib</c> with zero ONNX dependency -- the
/// orchestrator only talks to the role-typed enhancer interfaces
/// (<see cref="IStarRemover"/>, <see cref="IStellarSharpener"/>,
/// <see cref="INonStellarDeconvolver"/>, <see cref="IDenoiseEnhancer"/>),
/// so consumers can substitute classical fallbacks or alternative model
/// backends without changing the pipeline. All enhancer dependencies are
/// nullable so the pipeline is constructible even when no concrete impls
/// are registered; it throws only when <see cref="ProcessAsync"/> sees a
/// step whose enhancer wasn't supplied.</para>
///
/// <para>Step semantics are documented on the individual records;
/// <see cref="SharpenRequest.Canonical"/> wires up the recommended order
/// for a "sharpen everything" workflow. Validation is topological -- each
/// step's prerequisites must already be satisfied by earlier steps in the
/// array (e.g. <see cref="SharpenStarsStep"/> requires a preceding
/// <see cref="RemoveStarsStep"/> to have produced the stars-only plate).
/// Duplicate step types are rejected in v1 (callers compose differently
/// instead of stacking the same step twice).</para>
/// </remarks>
public sealed class SharpenPipeline(
    IStarRemover? starRemover = null,
    IStellarSharpener? stellarSharpener = null,
    INonStellarDeconvolver? nonStellarDeconvolver = null,
    IDenoiseEnhancer? denoiser = null,
    IGradientCorrector? gradientCorrector = null,
    ILogger<SharpenPipeline>? logger = null)
{
    public async Task<SharpenResult> ProcessAsync(SharpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ValidateRequest(request);

        // BayerDrizzle (and any partial-coverage) masters carry non-finite
        // coverage holes. The enhancers -- SAS ONNX and RC-Astro alike --
        // compute non-NaN-aware global normalisation, so a single NaN poisons
        // the whole output to NaN. Sanitise once at the boundary so every
        // downstream enhancer (and the EstimateNoiseProfile baseline below)
        // sees finite input. No-op + same instance when already clean.
        var source = request.Source.ReplaceNonFiniteWithChannelMean();
        if (!ReferenceEquals(source, request.Source))
        {
            logger?.LogWarning("SharpenPipeline: source had non-finite samples (e.g. drizzle coverage holes); filled with per-channel mean before enhancement.");
        }

        var totalSw = Stopwatch.StartNew();
        var (channels, srcW, srcH) = source.Shape;
        // Capture entry noise before any step runs so the result has a clean
        // baseline for "how much grain did the AI pipeline remove" deltas.
        // MAD-based estimator naturally rejects bright outliers (stars, nebula
        // cores) so this is background σ even without segmentation. See
        // Image.EstimateNoiseProfile xmldoc.
        var inputNoise = source.EstimateNoiseProfile();
        logger?.LogDebug("SharpenPipeline.ProcessAsync: input {W}x{H}x{C} σ=[{Sigma}] steps=[{Steps}]",
            srcW, srcH, channels, FormatNoise(inputNoise),
            string.Join(", ", request.Steps.Select(s => s.GetType().Name)));

        // Gradient-corrected source plate. Populated by GradientCorrectionStep
        // (head of Frank Sackenheim's canonical order). Downstream steps that
        // read from the source -- only RemoveStarsStep, since everything else
        // works off starless/stars lineages -- pick gradientCorrected if
        // present, else fall back to request.Source. This keeps the request
        // immutable.
        Image? gradientCorrected = null;
        Image? starless = null;
        Image? starsOnly = null;
        Image? sharpenedStars = null;
        Image? deconvolvedStarless = null;
        Image? denoisedStarless = null;
        Image? final = null;

        var timings = new List<(string Name, long Ms, ImmutableArray<float> NoiseAfter)>(request.Steps.Length);
        // Tracks the noise σ of the most-processed *linear* starless plate.
        // Bumped only by linear-domain steps (RemoveStars / Deconv / Denoise);
        // stretch / bg-reduce / compress / GHS steps that mutate the
        // denoisedStarless slot in stretched space do NOT update this -- they
        // would conflate AI noise reduction with histogram redistribution.
        // Anchors the headline "AI removed X% noise" delta in SharpenResult.
        var linearStarlessNoise = inputNoise;
        var phaseSw = Stopwatch.StartNew();

        try
        {
            foreach (var step in request.Steps)
            {
                phaseSw.Restart();
                switch (step)
                {
                    case GradientCorrectionStep:
                    {
                        // Removes the smooth background gradient at the head
                        // of the pipeline. All downstream steps that consume
                        // the source pick `gradientCorrected ?? source`
                        // so they see the cleaned plate.
                        gradientCorrected = await Require(gradientCorrector).EnhanceAsync(source, cancellationToken);
                        timings.Add(("gradient-correction", phaseSw.ElapsedMilliseconds, gradientCorrected.EstimateNoiseProfile()));
                        break;
                    }

                    case RemoveStarsStep removeStars:
                    {
                        // Read from the corrected plate when GradientCorrectionStep
                        // ran upstream; falls back to the original source otherwise.
                        var starsInput = gradientCorrected ?? source;
                        starless = await Require(starRemover).EnhanceAsync(starsInput, cancellationToken);
                        // Pixel split:
                        //   Additive (default): StarsOnly = max(Source - Starless, 0).
                        //     Physically correct in linear-light photon space.
                        //   Screen: StarsOnly = unscreen(Source, Starless).
                        //     Matches the stretched-space identity NAFNet was
                        //     trained against -- prefer when callers pass
                        //     pre-stretched data or want to round-trip through
                        //     the screen identity.
                        starsOnly = removeStars.SplitMode == RecombineMode.Screen
                            ? starsInput.Unscreen(starless)
                            : starsInput.Subtract(starless);
                        linearStarlessNoise = starless.EstimateNoiseProfile();
                        timings.Add(("remove-stars+split", phaseSw.ElapsedMilliseconds, linearStarlessNoise));
                        // gradientCorrected's only consumer was the star
                        // remover + the split math above. Caller opts out of
                        // keeping it via the GradientCorrected flag -- by
                        // default (All) it's preserved for inspection.
                        if (!request.KeepIntermediates.HasFlag(SharpenIntermediates.GradientCorrected) && gradientCorrected is not null)
                        {
                            gradientCorrected.Release();
                            gradientCorrected = null;
                        }
                        break;
                    }

                    case SharpenStarsStep sharpStep:
                    {
                        // Input is whatever the stars plate currently is -- raw
                        // starsOnly normally, or an SCNR'd version if ScnrStarsStep
                        // ran earlier in the same request.
                        var inputPlate = Require(starsOnly);
                        var raw = await Require(stellarSharpener).EnhanceAsync(inputPlate, cancellationToken);
                        sharpenedStars = sharpStep.Blend < 1f
                            ? inputPlate.Lerp(raw, sharpStep.Blend)
                            : raw;
                        if (!ReferenceEquals(sharpenedStars, raw)) raw.Release();
                        timings.Add(("sharpen-stars", phaseSw.ElapsedMilliseconds, sharpenedStars.EstimateNoiseProfile()));
                        break;
                    }

                    case DeconvolveStarlessStep deconvStep:
                    {
                        var inputPlate = Require(starless);
                        var raw = await Require(nonStellarDeconvolver).EnhanceAsync(inputPlate, cancellationToken);
                        deconvolvedStarless = deconvStep.Blend < 1f
                            ? inputPlate.Lerp(raw, deconvStep.Blend)
                            : raw;
                        if (!ReferenceEquals(deconvolvedStarless, raw)) raw.Release();
                        linearStarlessNoise = deconvolvedStarless.EstimateNoiseProfile();
                        timings.Add(("deconv-starless", phaseSw.ElapsedMilliseconds, linearStarlessNoise));
                        // starless is dead once deconv lands -- downstream
                        // (denoise / recombine fallback) prefers deconv ->
                        // denoised in the rolling-most-processed chain.
                        if (!request.KeepIntermediates.HasFlag(SharpenIntermediates.Starless) && starless is not null)
                        {
                            starless.Release();
                            starless = null;
                        }
                        break;
                    }

                    case DenoiseStarlessStep denoiseStep:
                    {
                        // Denoise sees the most-processed starless: deconv
                        // output if a DeconvolveStarlessStep ran earlier,
                        // otherwise the raw starless plate.
                        var inputPlate = deconvolvedStarless ?? Require(starless);
                        var raw = await Require(denoiser).EnhanceAsync(inputPlate, denoiseStep.Variant, cancellationToken);
                        denoisedStarless = denoiseStep.Blend < 1f
                            ? inputPlate.Lerp(raw, denoiseStep.Blend)
                            : raw;
                        if (!ReferenceEquals(denoisedStarless, raw)) raw.Release();
                        linearStarlessNoise = denoisedStarless.EstimateNoiseProfile();
                        timings.Add(("denoise-starless", phaseSw.ElapsedMilliseconds, linearStarlessNoise));
                        // deconvolvedStarless is dead once denoise lands
                        // (same rolling-most-processed argument); release if
                        // the caller didn't flag it for retention.
                        if (!request.KeepIntermediates.HasFlag(SharpenIntermediates.DeconvolvedStarless) && deconvolvedStarless is not null)
                        {
                            deconvolvedStarless.Release();
                            deconvolvedStarless = null;
                        }
                        // Likewise starless when denoise reads directly from
                        // it (no deconv ran in between -- starless is now dead).
                        if (!request.KeepIntermediates.HasFlag(SharpenIntermediates.Starless) && starless is not null)
                        {
                            starless.Release();
                            starless = null;
                        }
                        break;
                    }

                    case BackgroundReduceStep bgStep:
                    {
                        // Pulls background luminosity down on the starless
                        // plate via an S-curve. Auto-derives the histogram
                        // peak when BackgroundPeak is null.
                        var inputPlate = denoisedStarless ?? deconvolvedStarless ?? Require(starless);
                        var bgPeak = bgStep.BackgroundPeak ?? inputPlate.EstimateBackgroundPeak();
                        var reduced = inputPlate.ReduceBackground(bgPeak, bgStep.Compression);
                        if (denoisedStarless is not null) denoisedStarless.Release();
                        denoisedStarless = reduced;
                        timings.Add(("background-reduce", phaseSw.ElapsedMilliseconds, denoisedStarless.EstimateNoiseProfile()));
                        break;
                    }

                    case CompressHighlightsStep hiStep:
                    {
                        // Soft highlight roll-off on the starless plate. Knee
                        // threshold below which curve is identity; above,
                        // Reinhard-style compression. Auto-slotted into the
                        // denoisedStarless lineage like the other starless
                        // transforms.
                        var inputPlate = denoisedStarless ?? deconvolvedStarless ?? Require(starless);
                        var compressed = inputPlate.CompressHighlights(hiStep.Knee, hiStep.Amount);
                        if (denoisedStarless is not null) denoisedStarless.Release();
                        denoisedStarless = compressed;
                        timings.Add(("compress-highlights", phaseSw.ElapsedMilliseconds, denoisedStarless.EstimateNoiseProfile()));
                        break;
                    }

                    case ScnrStarsStep scnrStep:
                    {
                        // SCNR (Subtractive Chromatic Noise Reduction) on the
                        // stellar plate only -- the starless plate keeps
                        // legitimate green nebula signal (OIII / H-beta).
                        // Mutates the most-processed stars plate in-place
                        // (sharpened if present, else raw starsOnly).
                        var inputPlate = sharpenedStars ?? Require(starsOnly);
                        var afterScnr = inputPlate.SubtractiveChromaticNoise(scnrStep.Mode, scnrStep.Amount);
                        if (sharpenedStars is not null)
                        {
                            sharpenedStars.Release();
                            sharpenedStars = afterScnr;
                        }
                        else
                        {
                            Require(starsOnly).Release();
                            starsOnly = afterScnr;
                        }
                        // SCNR mutates the stars plate; report its noise to track G-channel drift.
                        var scnrTracked = sharpenedStars ?? Require(starsOnly);
                        timings.Add(("scnr-stars", phaseSw.ElapsedMilliseconds, scnrTracked.EstimateNoiseProfile()));
                        break;
                    }

                    case StretchStarsStep stretchStarsStep:
                    {
                        // Frank-style fixed-curve StarStretch on the most-
                        // processed stars plate. The fixed curve is critical
                        // here -- auto-targeted MtfStretch would push the
                        // stars plate's near-zero median to the target and
                        // saturate all bright peaks. In-place mutation
                        // lineage matches SCNR.
                        var inputPlate = sharpenedStars ?? Require(starsOnly);
                        var stretched = inputPlate.StarStretch(stretchStarsStep.Amount);
                        if (sharpenedStars is not null)
                        {
                            sharpenedStars.Release();
                            sharpenedStars = stretched;
                        }
                        else
                        {
                            Require(starsOnly).Release();
                            starsOnly = stretched;
                        }
                        var stretchTracked = sharpenedStars ?? Require(starsOnly);
                        timings.Add(("stretch-stars", phaseSw.ElapsedMilliseconds, stretchTracked.EstimateNoiseProfile()));
                        break;
                    }

                    case AsinhStretchStarsStep asinhStarsStep:
                    {
                        // Asinh stretch on the most-processed stars plate.
                        // Chrominance-preserving by construction (all channels
                        // share one luma-derived scale); the right curve when
                        // star colour matters under heavy lift. Same in-place
                        // mutation lineage as StretchStarsStep.
                        var inputPlate = sharpenedStars ?? Require(starsOnly);
                        var stretched = inputPlate.AsinhStretch(
                            asinhStarsStep.Beta, asinhStarsStep.BlackPoint, asinhStarsStep.LumaWeights);
                        if (sharpenedStars is not null)
                        {
                            sharpenedStars.Release();
                            sharpenedStars = stretched;
                        }
                        else
                        {
                            Require(starsOnly).Release();
                            starsOnly = stretched;
                        }
                        var stretchTracked = sharpenedStars ?? Require(starsOnly);
                        timings.Add(($"asinh-stretch-stars(β={asinhStarsStep.Beta:F1},bp={asinhStarsStep.BlackPoint:F3})", phaseSw.ElapsedMilliseconds, stretchTracked.EstimateNoiseProfile()));
                        break;
                    }

                    case GhsStretchStarlessStep ghsStep:
                    {
                        // Generalised Hyperbolic Stretch on the most-processed
                        // starless plate. Cranfield's reference implementation
                        // (see docs/plans/ghs.md). All curve mechanics live in
                        // ApplyGhsChain, shared with GhsStretchFinalStep.
                        var inputPlate = denoisedStarless ?? deconvolvedStarless ?? Require(starless);
                        var (stretched, spLabel, convergenceLabel) = ApplyGhsChain(
                            inputPlate,
                            lnD: ghsStep.LnD, b: ghsStep.B, userSp: ghsStep.SP,
                            lp: ghsStep.LP, hp: ghsStep.HP, passes: ghsStep.Passes,
                            autoConverge: ghsStep.AutoConverge,
                            autoTargetValue: ghsStep.AutoTargetValue, autoTarget: ghsStep.AutoTarget);
                        if (denoisedStarless is not null) denoisedStarless.Release();
                        denoisedStarless = stretched;
                        timings.Add(($"ghs-stretch-starless(b{ghsStep.B:F1},{spLabel},hp{ghsStep.HP:F2},{ghsStep.Passes}x{convergenceLabel})", phaseSw.ElapsedMilliseconds, denoisedStarless.EstimateNoiseProfile()));
                        break;
                    }

                    case StretchStarlessStep stretchStarlessStep:
                    {
                        // MTF stretch on the most-processed starless plate
                        // (denoised > deconvolved > raw). Slotted as a NEW
                        // denoisedStarless entry so the rolling
                        // "most-processed" chain remains valid downstream.
                        var inputPlate = denoisedStarless ?? deconvolvedStarless ?? Require(starless);
                        var stretched = inputPlate.MtfStretch(stretchStarlessStep.TargetMedian, out _, out _);
                        // Release whichever slot we're shadowing -- the
                        // recombine fallback chain picks denoisedStarless
                        // first regardless of how it got there.
                        if (denoisedStarless is not null)
                        {
                            denoisedStarless.Release();
                        }
                        denoisedStarless = stretched;
                        timings.Add(("stretch-starless", phaseSw.ElapsedMilliseconds, denoisedStarless.EstimateNoiseProfile()));
                        break;
                    }

                    case AsinhStretchStarlessStep asinhStarlessStep:
                    {
                        // Asinh stretch on the most-processed starless plate.
                        // Same "slotted as denoisedStarless" pattern as the MTF
                        // and GHS starless cases.
                        var inputPlate = denoisedStarless ?? deconvolvedStarless ?? Require(starless);
                        var stretched = inputPlate.AsinhStretch(
                            asinhStarlessStep.Beta, asinhStarlessStep.BlackPoint, asinhStarlessStep.LumaWeights);
                        if (denoisedStarless is not null) denoisedStarless.Release();
                        denoisedStarless = stretched;
                        timings.Add(($"asinh-stretch-starless(β={asinhStarlessStep.Beta:F1},bp={asinhStarlessStep.BlackPoint:F3})", phaseSw.ElapsedMilliseconds, denoisedStarless.EstimateNoiseProfile()));
                        break;
                    }

                    case RecombineStep recombineStep:
                    {
                        // Most-processed plate wins on each side:
                        //   bg = denoised > deconvolved > raw starless
                        //   fg = sharpened > raw starsOnly
                        var bg = denoisedStarless ?? deconvolvedStarless ?? Require(starless);
                        var fg = sharpenedStars ?? Require(starsOnly);
                        final = recombineStep.Mode == RecombineMode.Screen ? bg.Screen(fg) : bg.Add(fg);
                        timings.Add(("recombine", phaseSw.ElapsedMilliseconds, final.EstimateNoiseProfile()));
                        // Recombine may be followed by a post-recombine stretch
                        // step (MtfStretchFinalStep / GhsStretchFinalStep); the
                        // contributing per-plate slots stay around so an explicit
                        // keep-intermediates flag honours them, but they get
                        // released here on the no-keep path because the final
                        // stretch only reads `final`.
                        var keep = request.KeepIntermediates;
                        if (!keep.HasFlag(SharpenIntermediates.GradientCorrected)   && gradientCorrected   is not null) { gradientCorrected.Release();   gradientCorrected   = null; }
                        if (!keep.HasFlag(SharpenIntermediates.Starless)            && starless            is not null) { starless.Release();            starless            = null; }
                        if (!keep.HasFlag(SharpenIntermediates.StarsOnly)           && starsOnly           is not null) { starsOnly.Release();           starsOnly           = null; }
                        if (!keep.HasFlag(SharpenIntermediates.SharpenedStars)      && sharpenedStars      is not null) { sharpenedStars.Release();      sharpenedStars      = null; }
                        if (!keep.HasFlag(SharpenIntermediates.DeconvolvedStarless) && deconvolvedStarless is not null) { deconvolvedStarless.Release(); deconvolvedStarless = null; }
                        if (!keep.HasFlag(SharpenIntermediates.DenoisedStarless)    && denoisedStarless    is not null) { denoisedStarless.Release();    denoisedStarless    = null; }
                        break;
                    }

                    case MtfStretchFinalStep mtfFinalStep:
                    {
                        // MTF stretch on the recombined `final` plate.
                        // The non-split workflow's stretch step: validation
                        // ensures this only fires after RecombineStep, so
                        // `final` is populated.
                        var input = Require(final);
                        var stretched = input.MtfStretch(mtfFinalStep.TargetMedian, out _, out _);
                        input.Release();
                        final = stretched;
                        timings.Add(($"mtf-stretch-final(tm={mtfFinalStep.TargetMedian:F2})", phaseSw.ElapsedMilliseconds, final.EstimateNoiseProfile()));
                        break;
                    }

                    case GhsStretchFinalStep ghsFinalStep:
                    {
                        // GHS chain on the recombined `final` plate. Reuses
                        // ApplyGhsChain (single source of truth for per-channel
                        // auto-converge + multi-pass + telemetry); validation
                        // ensures `final` is populated.
                        var input = Require(final);
                        var (stretched, spLabel, convergenceLabel) = ApplyGhsChain(
                            input,
                            lnD: ghsFinalStep.LnD, b: ghsFinalStep.B, userSp: ghsFinalStep.SP,
                            lp: ghsFinalStep.LP, hp: ghsFinalStep.HP, passes: ghsFinalStep.Passes,
                            autoConverge: ghsFinalStep.AutoConverge,
                            autoTargetValue: ghsFinalStep.AutoTargetValue, autoTarget: ghsFinalStep.AutoTarget);
                        input.Release();
                        final = stretched;
                        timings.Add(($"ghs-stretch-final(b{ghsFinalStep.B:F1},{spLabel},hp{ghsFinalStep.HP:F2},{ghsFinalStep.Passes}x{convergenceLabel})", phaseSw.ElapsedMilliseconds, final.EstimateNoiseProfile()));
                        break;
                    }

                    case AsinhStretchFinalStep asinhFinalStep:
                    {
                        // Asinh stretch on the recombined `final` plate. The
                        // non-split-workflow asinh option. Validation ensures
                        // `final` is populated.
                        var input = Require(final);
                        var stretched = input.AsinhStretch(
                            asinhFinalStep.Beta, asinhFinalStep.BlackPoint, asinhFinalStep.LumaWeights);
                        input.Release();
                        final = stretched;
                        timings.Add(($"asinh-stretch-final(β={asinhFinalStep.Beta:F1},bp={asinhFinalStep.BlackPoint:F3})", phaseSw.ElapsedMilliseconds, final.EstimateNoiseProfile()));
                        break;
                    }

                    default:
                        // Defensive: validation catches unknown step types,
                        // but if a new SharpenStep is added without a switch
                        // arm we want a loud failure rather than a silent skip.
                        throw new NotSupportedException(
                            $"SharpenPipeline: unhandled step type {step.GetType().Name}.");
                }
            }
        }
        catch
        {
            // On failure release everything we've allocated so the camera
            // buffer pool doesn't grow unboundedly across failed runs.
            gradientCorrected?.Release();
            starless?.Release();
            starsOnly?.Release();
            sharpenedStars?.Release();
            deconvolvedStarless?.Release();
            denoisedStarless?.Release();
            final?.Release();
            throw;
        }

        // FinalNoise = the σ of the most-processed *linear* starless plate,
        // tracked separately via linearStarlessNoise so stretch / bg-reduce /
        // compress / GHS steps (which mutate the denoisedStarless SLOT in
        // stretched space) don't poison the headline Δ%. See the variable's
        // declaration for the full rationale.
        var finalNoise = linearStarlessNoise;

        logger?.LogInformation(
            "SharpenPipeline.ProcessAsync: {W}x{H}x{C} σ_in=[{NoiseIn}] σ_out=[{NoiseOut}] timings={Timings} total={Total}ms",
            srcW, srcH, channels,
            FormatNoise(inputNoise), FormatNoise(finalNoise),
            string.Join(" ", timings.Select(t => $"{t.Name}={t.Ms}ms{(t.NoiseAfter.IsDefaultOrEmpty ? "" : $" σ=[{FormatNoise(t.NoiseAfter)}]")}")),
            totalSw.ElapsedMilliseconds);

        return new SharpenResult(
            Final: final,
            Starless: starless,
            StarsOnly: starsOnly,
            SharpenedStars: sharpenedStars,
            DeconvolvedStarless: deconvolvedStarless,
            DenoisedStarless: denoisedStarless,
            GradientCorrected: gradientCorrected,
            InputNoise: inputNoise,
            FinalNoise: finalNoise);
    }

    /// <summary>
    /// Renders a per-channel σ vector as a slash-separated scientific-notation
    /// string for log lines (e.g. <c>4.21E-003/5.18E-003/4.99E-003</c>).
    /// Used by both the entry log and the per-step timing summary.
    /// </summary>
    private static string FormatNoise(ImmutableArray<float> sigmas)
    {
        if (sigmas.IsDefaultOrEmpty) return "n/a";
        return string.Join("/", sigmas.Select(s => s.ToString("E2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Asserts a plate / enhancer the pipeline expects (per
    /// <see cref="ValidateRequest"/>) is actually present. Removes the need
    /// for the null-forgiving <c>!</c> operator at call sites while keeping
    /// the cross-method invariant explicit. The <c>InvalidOperationException</c>
    /// here is dead code in practice -- <see cref="ValidateRequest"/> catches
    /// the missing-precondition case before execution -- but it makes the
    /// assumption legible and turns any future validation bug into a clear
    /// diagnostic rather than a NullReferenceException.
    /// </summary>
    private static T Require<T>(T? value, [CallerArgumentExpression(nameof(value))] string? expression = null) where T : class
        => value ?? throw new InvalidOperationException(
            $"SharpenPipeline invariant violated: '{expression}' is null at use. ValidateRequest should have rejected this request.");

    /// <summary>
    /// Run a (possibly multi-pass, possibly per-channel auto-converged) GHS
    /// chain on <paramref name="input"/>. Single source of truth for the
    /// dispatcher cases that apply GHS to a plate, regardless of which
    /// plate slot (starless / final). Returns the stretched <see cref="Image"/>
    /// and two telemetry labels (sp and convergence) that the caller stitches
    /// into the timing-log line.
    /// </summary>
    /// <remarks>
    /// Per-channel auto-converge fires when <paramref name="autoConverge"/>
    /// is true AND the plate has &gt; 1 channel -- each channel gets its
    /// own SP detection + LnD bisection. Linked mode otherwise. SP
    /// auto-detects ONCE before the multi-pass loop: re-estimating after
    /// each pass would chase the already-stretched histogram and bias the
    /// hinge toward the new mode, defeating the "pivot at the linear
    /// lift-off" intent.
    /// </remarks>
    private static (Image Stretched, string SpLabel, string ConvergenceLabel) ApplyGhsChain(
        Image input,
        double lnD, double b, double? userSp, double lp, double hp,
        int passes, bool autoConverge,
        double autoTargetValue, Image.GhsConvergeTarget autoTarget)
    {
        var channelCount = input.ChannelCount;
        var current = input;
        string spLabel;
        var convergenceLabel = "";

        if (autoConverge && channelCount > 1)
        {
            // Per-channel: detect SP + bisect LnD independently per channel;
            // build per-channel LUTs in a single GeneralizedHyperbolicStretchPerChannel
            // call. Matches PixInsight's "unlinked" stretch mode and avoids
            // the colour cast that linked AutoConverge produces on OSC drizzle
            // plates with uneven channel bg.
            var perChannelLnD = new double[channelCount];
            var perChannelSp = new double[channelCount];
            var perChannelMedian = new double[channelCount];
            var perChannelMode = new double[channelCount];
            var perChannelR2 = new double[channelCount];
            var perChannelB = new double[channelCount];
            var perChannelLp = new double[channelCount];
            var perChannelHp = new double[channelCount];
            var allConverged = true;
            for (var c = 0; c < channelCount; c++)
            {
                var hist = input.Histogram(channel: c);
                var chSp = userSp ?? Math.Clamp(input.EstimateRisingEdge(channel: c), 1e-4, 0.999);
                var convergence = Image.ConvergeGhsStretchFactor(
                    hist, b: b, sp: chSp,
                    lp: lp, hp: hp,
                    targetValue: autoTargetValue,
                    target: autoTarget);
                perChannelLnD[c] = convergence.LnD;
                perChannelSp[c] = chSp;
                perChannelMedian[c] = convergence.PostStretchMedian;
                perChannelMode[c] = convergence.PostStretchMode;
                perChannelR2[c] = convergence.LogSlopeRSquared;
                perChannelB[c] = b;
                perChannelLp[c] = lp;
                perChannelHp[c] = hp;
                allConverged &= convergence.Converged;
            }
            for (var pass = 0; pass < passes; pass++)
            {
                var stretched = current.GeneralizedHyperbolicStretchPerChannel(
                    perChannelLnD, perChannelB, perChannelSp,
                    perChannelLp, perChannelHp);
                if (!ReferenceEquals(current, input)) current.Release();
                current = stretched;
            }
            // Telemetry: show the metric the bisection targeted (med vs mode);
            // both numbers are computed regardless of target so the operator can
            // sanity-check that the non-targeted metric hasn't drifted too far.
            var perChTargeted = autoTarget == Image.GhsConvergeTarget.Mode ? perChannelMode : perChannelMedian;
            var perChLabel = autoTarget == Image.GhsConvergeTarget.Mode ? "mode" : "med";
            spLabel = $"sp~[{string.Join("/", perChannelSp.Select(v => v.ToString("F4")))}]";
            convergenceLabel = $",auto-perch(lnD=[{string.Join("/", perChannelLnD.Select(v => v.ToString("F2")))}],{perChLabel}=[{string.Join("/", perChTargeted.Select(v => v.ToString("F3")))}]" +
                (allConverged ? "" : ",BEST-EFFORT") + ")";
        }
        else
        {
            // Single-channel mono OR multi-channel non-auto: linked.
            // Either there's only one channel or the caller is supplying
            // explicit LnD (knows their input is balanced).
            var sp = userSp ?? Math.Clamp(input.EstimateRisingEdge(), 1e-4, 0.999);
            var effectiveLnD = lnD;
            if (autoConverge)
            {
                var histogram = input.Histogram(channel: 0);
                var convergence = Image.ConvergeGhsStretchFactor(
                    histogram, b: b, sp: sp,
                    lp: lp, hp: hp,
                    targetValue: autoTargetValue,
                    target: autoTarget);
                effectiveLnD = convergence.LnD;
                convergenceLabel = convergence.Converged
                    ? $",auto(med={convergence.PostStretchMedian:F3},mode={convergence.PostStretchMode:F3},R^2={convergence.LogSlopeRSquared:F2})"
                    : $",auto(BEST-EFFORT med={convergence.PostStretchMedian:F3},mode={convergence.PostStretchMode:F3})";
            }
            for (var pass = 0; pass < passes; pass++)
            {
                var stretched = current.GeneralizedHyperbolicStretch(
                    lnD: effectiveLnD, b: b, sp: sp,
                    lp: lp, hp: hp);
                if (!ReferenceEquals(current, input)) current.Release();
                current = stretched;
            }
            spLabel = userSp is null ? $"sp~{sp:F4}auto" : $"sp={sp:F3}";
        }

        return (current, spLabel, convergenceLabel);
    }

    private static bool HasPrecedingRecombine(SharpenRequest request, int index)
    {
        for (var k = 0; k < index; k++)
        {
            if (request.Steps[k] is RecombineStep) return true;
        }
        return false;
    }

    private void ValidateRequest(SharpenRequest request)
    {
        if (request.Steps.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "SharpenRequest.Steps must contain at least one step. Use SharpenRequest.Canonical(source) for the default sharpen workflow.",
                nameof(request));
        }

        // Topological walk: for each step, every plate it reads must be
        // produced by an earlier step. Most step types are rejected on
        // duplicate (v1 simplification) except those that have a
        // documented multi-application recipe -- e.g. GhsStretchStarlessStep
        // for Mike Cranfield's two-pass workflow (gh-astro sections 2.7-2.9:
        // pass 1 lifts the linear histogram, BackgroundReduceStep does the
        // "linear prestretch", pass 2 redistributes contrast).
        var hasStarless = false;
        var hasStarsOnly = false;
        var seenTypes = new HashSet<Type>();

        for (var i = 0; i < request.Steps.Length; i++)
        {
            var step = request.Steps[i];
            var t = step.GetType();
            var alreadySeen = !seenTypes.Add(t);
            if (alreadySeen && step is not GhsStretchStarlessStep)
            {
                throw new ArgumentException(
                    $"SharpenRequest.Steps[{i}]: duplicate step of type {t.Name}. Each step type may appear at most once.",
                    nameof(request));
            }
            switch (step)
            {
                case GradientCorrectionStep:
                    if (hasStarless) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GradientCorrectionStep must run BEFORE RemoveStarsStep -- Frank Sackenheim's canonical order is gradient -> stars -> detail -> stretch. Move the gradient step to the head of the request.",
                        nameof(request));
                    break;
                case RemoveStarsStep:
                    hasStarless = true;
                    hasStarsOnly = true;
                    break;
                case SharpenStarsStep:
                    if (!hasStarsOnly) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: SharpenStarsStep requires a preceding RemoveStarsStep (no stars-only plate to sharpen).",
                        nameof(request));
                    break;
                case DeconvolveStarlessStep:
                    if (!hasStarless) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: DeconvolveStarlessStep requires a preceding RemoveStarsStep (no starless plate to deconvolve).",
                        nameof(request));
                    break;
                case DenoiseStarlessStep:
                    if (!hasStarless) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: DenoiseStarlessStep requires a preceding RemoveStarsStep (no starless plate to denoise).",
                        nameof(request));
                    break;
                case ScnrStarsStep:
                    if (!hasStarsOnly) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: ScnrStarsStep requires a preceding RemoveStarsStep (no stars-only plate for SCNR).",
                        nameof(request));
                    break;
                case BackgroundReduceStep bgReduce:
                    if (!hasStarless) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: BackgroundReduceStep requires a preceding RemoveStarsStep (no starless plate to reduce).",
                        nameof(request));
                    if (bgReduce.Compression is <= 0.0 or > 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: BackgroundReduceStep.Compression must be in (0, 1]; got {bgReduce.Compression}.",
                        nameof(request));
                    if (bgReduce.BackgroundPeak is { } bp && (bp <= 0.0 || bp >= 0.5)) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: BackgroundReduceStep.BackgroundPeak must be in (0, 0.5); got {bp}.",
                        nameof(request));
                    break;
                case CompressHighlightsStep hi:
                    if (!hasStarless) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: CompressHighlightsStep requires a preceding RemoveStarsStep (no starless plate to compress).",
                        nameof(request));
                    if (hi.Knee is <= 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: CompressHighlightsStep.Knee must be in (0, 1); got {hi.Knee}.",
                        nameof(request));
                    if (hi.Amount < 0.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: CompressHighlightsStep.Amount must be >= 0; got {hi.Amount}.",
                        nameof(request));
                    break;
                case StretchStarsStep stretchStars:
                    if (!hasStarsOnly) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: StretchStarsStep requires a preceding RemoveStarsStep (no stars plate to stretch).",
                        nameof(request));
                    if (stretchStars.Amount is <= 0.0 or > 10.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: StretchStarsStep.Amount must be in (0, 10]; got {stretchStars.Amount}. " +
                        "SAS Pro slider range is 0-8; typical linear-input values are 1.5-3.0.",
                        nameof(request));
                    break;
                case StretchStarlessStep stretchStarless:
                    if (!hasStarless) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: StretchStarlessStep requires a preceding RemoveStarsStep (no starless plate to stretch).",
                        nameof(request));
                    if (stretchStarless.TargetMedian is <= 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: StretchStarlessStep.TargetMedian must be in (0, 1); got {stretchStarless.TargetMedian}.",
                        nameof(request));
                    break;
                case GhsStretchStarlessStep ghs:
                    if (!hasStarless) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchStarlessStep requires a preceding RemoveStarsStep (no starless plate to stretch).",
                        nameof(request));
                    if (ghs.LnD < 0.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchStarlessStep.LnD must be >= 0 (0 = identity); got {ghs.LnD}.",
                        nameof(request));
                    if (ghs.LP < 0.0 || ghs.LP > 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchStarlessStep.LP must be in [0, 1]; got {ghs.LP}.",
                        nameof(request));
                    if (ghs.HP < 0.0 || ghs.HP > 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchStarlessStep.HP must be in [0, 1]; got {ghs.HP}.",
                        nameof(request));
                    if (ghs.HP < ghs.LP) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchStarlessStep requires LP <= HP; got LP={ghs.LP}, HP={ghs.HP}.",
                        nameof(request));
                    if (ghs.SP is { } spVal && (spVal < ghs.LP || spVal > ghs.HP)) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchStarlessStep.SP must be null (auto-detect) or in [LP, HP] = [{ghs.LP}, {ghs.HP}]; got {spVal}.",
                        nameof(request));
                    if (ghs.Passes is < 1 or > 10) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchStarlessStep.Passes must be in [1, 10]; got {ghs.Passes}.",
                        nameof(request));
                    if (ghs.AutoTargetValue is <= 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchStarlessStep.AutoTargetValue must be in (0, 1); got {ghs.AutoTargetValue}.",
                        nameof(request));
                    break;
                case RecombineStep:
                    if (!hasStarless || !hasStarsOnly) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: RecombineStep requires a preceding RemoveStarsStep (no plates to recombine).",
                        nameof(request));
                    // Recombine no longer has to be last -- post-recombine
                    // stretch steps (MtfStretchFinalStep / GhsStretchFinalStep
                    // / AsinhStretchFinalStep) are allowed afterwards.
                    // Forbid anything else though.
                    for (var j = i + 1; j < request.Steps.Length; j++)
                    {
                        if (request.Steps[j] is not (MtfStretchFinalStep or GhsStretchFinalStep or AsinhStretchFinalStep))
                        {
                            throw new ArgumentException(
                                $"SharpenRequest.Steps[{i}]: only MtfStretchFinalStep, GhsStretchFinalStep or AsinhStretchFinalStep may follow RecombineStep; got {request.Steps[j].GetType().Name} at Steps[{j}].",
                                nameof(request));
                        }
                    }
                    break;
                case MtfStretchFinalStep mtfFinal:
                    if (!HasPrecedingRecombine(request, i)) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: MtfStretchFinalStep requires a preceding RecombineStep.",
                        nameof(request));
                    if (mtfFinal.TargetMedian is <= 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: MtfStretchFinalStep.TargetMedian must be in (0, 1); got {mtfFinal.TargetMedian}.",
                        nameof(request));
                    break;
                case GhsStretchFinalStep ghsFinal:
                    if (!HasPrecedingRecombine(request, i)) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchFinalStep requires a preceding RecombineStep.",
                        nameof(request));
                    if (ghsFinal.LnD < 0.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchFinalStep.LnD must be >= 0; got {ghsFinal.LnD}.",
                        nameof(request));
                    if (ghsFinal.LP is < 0.0 or > 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchFinalStep.LP must be in [0, 1]; got {ghsFinal.LP}.",
                        nameof(request));
                    if (ghsFinal.HP is < 0.0 or > 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchFinalStep.HP must be in [0, 1]; got {ghsFinal.HP}.",
                        nameof(request));
                    if (ghsFinal.SP is { } sp && sp is <= 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchFinalStep.SP must be in (0, 1); got {sp}.",
                        nameof(request));
                    if (ghsFinal.Passes is < 1 or > 10) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchFinalStep.Passes must be in [1, 10]; got {ghsFinal.Passes}.",
                        nameof(request));
                    if (ghsFinal.AutoTargetValue is <= 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: GhsStretchFinalStep.AutoTargetValue must be in (0, 1); got {ghsFinal.AutoTargetValue}.",
                        nameof(request));
                    break;
                case AsinhStretchStarsStep asinhStars:
                    if (!hasStarsOnly) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchStarsStep requires a preceding RemoveStarsStep (no stars plate to stretch).",
                        nameof(request));
                    if (asinhStars.Beta < 1.0 || asinhStars.Beta > 1000.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchStarsStep.Beta must be in [1, 1000]; got {asinhStars.Beta}. Siril's typical range is 1-1000.",
                        nameof(request));
                    if (asinhStars.BlackPoint is < 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchStarsStep.BlackPoint must be in [0, 1); got {asinhStars.BlackPoint}.",
                        nameof(request));
                    break;
                case AsinhStretchStarlessStep asinhStarless:
                    if (!hasStarless) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchStarlessStep requires a preceding RemoveStarsStep (no starless plate to stretch).",
                        nameof(request));
                    if (asinhStarless.Beta < 1.0 || asinhStarless.Beta > 1000.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchStarlessStep.Beta must be in [1, 1000]; got {asinhStarless.Beta}.",
                        nameof(request));
                    if (asinhStarless.BlackPoint is < 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchStarlessStep.BlackPoint must be in [0, 1); got {asinhStarless.BlackPoint}.",
                        nameof(request));
                    break;
                case AsinhStretchFinalStep asinhFinal:
                    if (!HasPrecedingRecombine(request, i)) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchFinalStep requires a preceding RecombineStep.",
                        nameof(request));
                    if (asinhFinal.Beta < 1.0 || asinhFinal.Beta > 1000.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchFinalStep.Beta must be in [1, 1000]; got {asinhFinal.Beta}.",
                        nameof(request));
                    if (asinhFinal.BlackPoint is < 0.0 or >= 1.0) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: AsinhStretchFinalStep.BlackPoint must be in [0, 1); got {asinhFinal.BlackPoint}.",
                        nameof(request));
                    break;
                default:
                    throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: unknown step type {t.Name}.",
                        nameof(request));
            }
        }

        // DI availability: each step's enhancer must be registered.
        foreach (var step in request.Steps)
        {
            switch (step)
            {
                case GradientCorrectionStep when gradientCorrector is null:
                    throw new InvalidOperationException(
                        "SharpenPipeline: GradientCorrectionStep requested but no IGradientCorrector registered. " +
                        "Call AddTianWenAi() (or register a custom IGradientCorrector) in your composition root.");
                case RemoveStarsStep when starRemover is null:
                    throw new InvalidOperationException(
                        "SharpenPipeline: RemoveStarsStep requested but no IStarRemover registered. " +
                        "Call AddTianWenAi() (or register a custom IStarRemover) in your composition root.");
                case SharpenStarsStep when stellarSharpener is null:
                    throw new InvalidOperationException(
                        "SharpenPipeline: SharpenStarsStep requested but no IStellarSharpener registered.");
                case DeconvolveStarlessStep when nonStellarDeconvolver is null:
                    throw new InvalidOperationException(
                        "SharpenPipeline: DeconvolveStarlessStep requested but no INonStellarDeconvolver registered.");
                case DenoiseStarlessStep when denoiser is null:
                    throw new InvalidOperationException(
                        "SharpenPipeline: DenoiseStarlessStep requested but no IDenoiseEnhancer registered.");
            }
        }
    }
}

/// <summary>Base type for the discriminated union of steps a
/// <see cref="SharpenPipeline"/> can execute. Concrete steps bundle "what
/// to do" with the parameters that govern it.</summary>
public abstract record SharpenStep;

/// <summary>Gradient / background correction. Slots at the head of the
/// canonical Frank Sackenheim flow (gradient -> stars -> detail -> stretch)
/// so every downstream stage sees a flat-background plate. Validation
/// enforces this ordering -- a gradient step that lands after
/// <see cref="RemoveStarsStep"/> is rejected because the starless plate is
/// itself a smoothed estimate of background + structure and a gradient
/// solver wants the raw photon counts.</summary>
/// <remarks>
/// Backed by <see cref="IGradientCorrector"/>. Output replaces the
/// source for downstream steps that read it (currently only
/// <see cref="RemoveStarsStep"/>); the original <c>request.Source</c> is
/// left untouched and remains the <c>InputNoise</c> baseline.
/// </remarks>
public sealed record GradientCorrectionStep : SharpenStep;

/// <summary>Star removal: produces a starless plate and (via the chosen
/// split mode) a stars-only plate.</summary>
/// <param name="SplitMode">How <c>StarsOnly</c> is derived from <c>Source</c>
/// and <c>Starless</c>. Default <see cref="RecombineMode.Additive"/>
/// (linear-light correct). Use <see cref="RecombineMode.Screen"/> to match
/// NAFNet's stretched-space training identity -- and match it on the
/// downstream <see cref="RecombineStep.Mode"/> for round-trip consistency.</param>
public sealed record RemoveStarsStep(RecombineMode SplitMode = RecombineMode.Additive) : SharpenStep;

/// <summary>Stellar sharpening on the stars-only plate.</summary>
/// <param name="Blend">AI strength in [0, 1]. 0 = stars untouched; 1 = full
/// AI output; ~0.5 is a typical good value for tight star fields where
/// AI4 over-sharpens.</param>
public sealed record SharpenStarsStep(float Blend = 1.0f) : SharpenStep;

/// <summary>Non-stellar deconvolution on the starless plate (PSF-conditional).</summary>
/// <param name="Blend">AI strength in [0, 1]. 0 = nebula untouched; 1 = full
/// AI output. Nebula usually tolerates higher values than stellar sharpening.</param>
public sealed record DeconvolveStarlessStep(float Blend = 1.0f) : SharpenStep;

/// <summary>Noise reduction on the starless plate. Sees the
/// <see cref="DeconvolveStarlessStep"/> output if that step ran first,
/// otherwise the raw starless plate.</summary>
/// <param name="Blend">AI strength in [0, 1]. 0 = noise untouched; 1 = full
/// AI output. AI4 NoiseX is conservative on faint detail so full strength
/// is usually safe.</param>
/// <param name="Variant">Model weight bundle. <see cref="DenoiseVariant.Default"/>
/// is the full AI4 NAFNet (slowest, highest quality); <see cref="DenoiseVariant.Lite"/>
/// is the half-width fast variant; <see cref="DenoiseVariant.Walking"/> is
/// trained on dither-correlated pattern noise.</param>
public sealed record DenoiseStarlessStep(float Blend = 1.0f, DenoiseVariant Variant = DenoiseVariant.Default) : SharpenStep;

/// <summary>S-curve background reduction on the starless / nebula plate.
/// Pulls the histogram peak down toward black via a symmetric cubic-Hermite
/// curve through <c>(0,0), (bg, bg·c), (0.5, 0.5), (1-bg, 1-bg·c), (1, 1)</c>.
/// Mirrors the "Reduce Background Luminosity" step in SAS Pro's statistical
/// stretch script (and the equivalent Affinity / Photoshop curves workflow).
/// </summary>
/// <remarks>
/// Applied AFTER <see cref="StretchStarlessStep"/> only -- the stars plate
/// already has black background between star peaks and shouldn't have it
/// pushed further down. Identity at midpoint, symmetric around it, so
/// highlights are preserved while the histogram peak (background) gets
/// crushed toward black.
/// </remarks>
/// <param name="Compression">Multiplier applied to <paramref name="BackgroundPeak"/>
/// for the low control point. Default <c>0.36</c> matches the empirical
/// Affinity curve point <c>(0.112, 0.04)</c> from finished real-world
/// processing -- the histogram peak gets compressed by ~3x. Subsequent
/// layers (Masked Contrast Boost etc.) push the visible picker reading
/// further down to ~3/255; this step is just the curves layer.
/// Range (0, 1]; 1.0 = no reduction (identity curve).</param>
/// <param name="BackgroundPeak">X anchor of the low control point. Null
/// (default) auto-derives via <see cref="Image.EstimateBackgroundPeak"/>
/// at execution time -- the histogram peak post-stretch, matching where
/// the eye picks the "background level" on an inspected histogram.
/// Range (0, 0.5).</param>
public sealed record BackgroundReduceStep(double Compression = 0.36, double? BackgroundPeak = null) : SharpenStep;

/// <summary>Reinhard-style soft highlight compression on the starless /
/// nebula plate. Pairs with <see cref="BackgroundReduceStep"/>: that step
/// pulls the low end down, this step compresses the high end -- together
/// they reproduce the SAS Pro statistical-stretch shape (asymmetric curve
/// passing through identity at the midpoint). Applied AFTER
/// <see cref="BackgroundReduceStep"/> in the canonical order.
/// </summary>
/// <param name="Knee">Threshold above which compression starts (below =
/// identity). Default <c>0.7</c>.</param>
/// <param name="Amount">Compression strength; higher = stronger roll-off.
/// Default <c>1.0</c>. Range &gt;= 0.</param>
public sealed record CompressHighlightsStep(double Knee = 0.7, double Amount = 1.0) : SharpenStep;

/// <summary>Subtractive Chromatic Noise Reduction on the stars-only plate
/// only -- preserves legitimate green nebula signal (OIII / H-beta) on the
/// starless side. Mutates the most-processed stars plate (sharpened if a
/// <see cref="SharpenStarsStep"/> ran first, else the raw stars plate).</summary>
/// <param name="Mode">Reference used to neutralise green:
/// <see cref="ScnrMode.Average"/> = pull G down to (R+B)/2;
/// <see cref="ScnrMode.Maximum"/> = pull G down to max(R, B).</param>
/// <param name="Amount">SCNR strength in [0, 1]. 1 = full neutralise.</param>
public sealed record ScnrStarsStep(ScnrMode Mode, float Amount = 1.0f) : SharpenStep;

/// <summary>Frank Sackenheim's fixed-curve StarStretch on the stars-only
/// plate. Applies <see cref="Image.StarStretch"/> (PixInsight MTF with a
/// fixed midtones balance, NOT auto-targeted to the channel median) to
/// whatever the current stars plate is (sharpened or raw, scnr'd or not).
/// </summary>
/// <remarks>
/// <para>The fixed-curve approach is critical for stars-only plates: the
/// plate's channel median is near zero (mostly background with sparse
/// bright peaks), so a median-targeting <see cref="MtfStretch"/> would
/// shove that near-zero median to the target and over-saturate the bright
/// peaks. Frank's approach uses a single fixed curve based on
/// <paramref name="Amount"/> -- same lift for every pixel regardless of
/// channel statistics.</para>
///
/// <para>Output is in stretched [0, 1] space; the downstream
/// <see cref="RecombineStep"/> should use <see cref="RecombineMode.Screen"/>
/// when both plates are pre-stretched (screen is the natural bounded
/// composite for stretched data).</para>
/// </remarks>
/// <param name="Amount">SAS Pro slider amount. UI default in SAS Pro is
/// 5.0 (factor = 243, midtones ≈ 0.004) for already-stretched input. On
/// linear stars-only data values in [1.5, 3.0] usually balance the lift
/// without over-brightening; we default to <c>2.0</c>.</param>
public sealed record StretchStarsStep(double Amount = 2.0) : SharpenStep;

/// <summary>Mike Cranfield's Generalized Hyperbolic Stretch on the
/// starless / nebula plate -- an alternative to <see cref="StretchStarlessStep"/>
/// (MTF) with built-in shadow / highlight protection controls. Use when MTF's
/// single-hyperbolic shape over-lifts highlights (the "core blowout" symptom)
/// and you don't want to stack a Reinhard <see cref="CompressHighlightsStep"/>
/// band-aid on top.
/// </summary>
/// <remarks>
/// <para>Pick this OR <see cref="StretchStarlessStep"/>, not both -- validation
/// rejects duplicate-purpose stretches on the same plate.</para>
///
/// <para>Defaults mirror Paul (Polymath Astro)'s PixInsight workflow as a
/// single-pass stretch-from-linear stage: <c>Asymmetry=8.0</c> (his "local
/// stretch intensity" of 8-10), <c>HighlightProtection=0.8</c> to preserve
/// bright structure without core blowout, <c>SymmetryPoint=null</c> (auto-
/// detect via <see cref="Image.EstimateRisingEdge"/> -- finds the histogram
/// lift-off point), <c>Passes=1</c> with these correct parameters.</para>
/// </remarks>
/// <param name="Intensity">Curve exponent (a). Smaller = stronger lift. Default <c>1.5</c>.</param>
/// <param name="Asymmetry">Direction (b in SAS Pro; "local stretch intensity"
/// in PixInsight). 1.0 = symmetric; higher = stronger hyperbolic curvature.
/// Default <c>8.0</c> -- Paul (Polymath Astro) recommends 8-10 for an initial
/// stretch-from-linear pass; lower values were the underlying cause of the
/// "GHS landed flat" symptom in the 3-pass workaround.</param>
/// <param name="ShadowProtection">LP in [0, 1] -- identity blend below SP. Default <c>0.0</c>.</param>
/// <param name="HighlightProtection">HP in [0, 1] -- identity blend above SP.
/// Default <c>0.8</c> per Paul's PixInsight workflow: high HP combined with
/// the high <paramref name="Asymmetry"/> gives a gentle highlight roll-off
/// without an external Reinhard band-aid.</param>
/// <param name="SymmetryPoint">SP in (0, 1) -- the curve hinges at this input
/// value (output at <c>x = 0.5</c> equals SP). <c>null</c> (default) auto-
/// detects via <see cref="Image.EstimateRisingEdge"/> on the input plate --
/// the histogram lift-off point on the left side of the background mode.
/// Mirrors Paul's "where the histogram starts lifting up towards the peak"
/// rule of thumb.</param>
/// <param name="Passes">How many times to apply the GHS curve. Default
/// <c>1</c> -- with correct defaults (b=8, hp=0.8, sp=auto) a single pass
/// gives the same shape as Paul's PixInsight workflow. Higher values are a
/// workaround for sub-optimal defaults; prefer raising
/// <paramref name="Asymmetry"/> over stacking passes. Range [1, 10].</param>
/// <summary>
/// Generalised Hyperbolic Stretch on the starless plate, faithfully porting
/// Mike Cranfield's PixInsight script
/// (<a href="https://github.com/mikec1485/GHS">mikec1485/GHS</a>). Defaults
/// match Paul (Polymath Astro)'s video walkthrough for the case-1
/// (linear -> display) stretch: <c>B = 8</c> (hyperbolic branch),
/// SP auto-detect via histogram lift-off, <c>HP = 0.8</c>.
/// See docs/plans/ghs.md for the math.
/// </summary>
/// <param name="LnD">User-facing stretch factor in the
/// <c>ln(D + 1)</c> convention -- internally <c>D = exp(LnD) - 1</c>.</param>
/// <param name="B">Signed local stretch intensity. <c>B = 8</c>
/// is Paul's case-1 recommendation (hyperbolic branch). <c>B = -1</c>
/// switches to the logarithmic branch for case-2
/// (local contrast on already-stretched input).</param>
/// <param name="SP">Symmetry point (input value where curve hinges).
/// Null = auto-detect via <see cref="Image.EstimateRisingEdge"/> on the
/// input histogram.</param>
/// <param name="LP">Shadow protection point in <c>[0, SP]</c>.</param>
/// <param name="HP">Highlight protection point in <c>[SP, 1]</c>.</param>
/// <param name="Passes">Number of times to apply the curve. Default 1.</param>
/// <param name="AutoConverge">When true, ignore <paramref name="LnD"/>
/// and bisect via <see cref="Image.ConvergeGhsStretchFactor"/> against
/// the input plate's histogram until the metric chosen by
/// <paramref name="AutoTarget"/> lands near
/// <paramref name="AutoTargetValue"/>. B / SP / LP / HP stay
/// caller-supplied.</param>
/// <param name="AutoTargetValue">Target value for AutoConverge. Default
/// 0.25 (SAS Pro / PixInsight convention for
/// <see cref="Image.GhsConvergeTarget.Median"/>; Paul's bg-lift recipe
/// for <see cref="Image.GhsConvergeTarget.Mode"/>).</param>
/// <param name="AutoTarget">Which post-stretch metric AutoConverge
/// bisects against. <see cref="Image.GhsConvergeTarget.Median"/> matches
/// PixInsight STF (historical default); <see cref="Image.GhsConvergeTarget.Mode"/>
/// targets the bg peak instead and produces a visibly brighter result.</param>
public sealed record GhsStretchStarlessStep(
    double LnD = 1.3,
    double B = 8.0,
    double? SP = null,
    double LP = 0.0,
    double HP = 0.8,
    int Passes = 1,
    bool AutoConverge = false,
    double AutoTargetValue = 0.25,
    Image.GhsConvergeTarget AutoTarget = Image.GhsConvergeTarget.Median) : SharpenStep;

/// <summary>Per-plate MTF stretch on the starless plate. Companion to
/// <see cref="StretchStarsStep"/>; see its xmldoc for the dual-stretch
/// motivation. Applies <see cref="Image.MtfStretch"/> to the
/// most-processed starless plate (denoised, deconvolved, or raw).</summary>
/// <param name="TargetMedian">MTF target median in [0.01, 0.50]; default
/// <c>0.25</c> matches the SAS Pro / cosmicclarity convention.</param>
public sealed record StretchStarlessStep(double TargetMedian = 0.25) : SharpenStep;

/// <summary>Recombine the processed plates into the final image. Validation
/// permits only <see cref="MtfStretchFinalStep"/> or
/// <see cref="GhsStretchFinalStep"/> after this step (the post-recombine
/// stretch pair).</summary>
/// <param name="Mode">Recombine math:
/// <see cref="RecombineMode.Additive"/> = <c>bg + fg</c> (linear-light
/// correct, two light sources summing on the sensor);
/// <see cref="RecombineMode.Screen"/> = <c>1 - (1-bg) * (1-fg)</c>
/// (matches NAFNet's stretched-space training identity). For round-trip
/// consistency match the <see cref="RemoveStarsStep.SplitMode"/>.</param>
public sealed record RecombineStep(RecombineMode Mode = RecombineMode.Additive) : SharpenStep;

/// <summary>MTF stretch applied to the recombined <c>final</c> plate AFTER
/// <see cref="RecombineStep"/>. This is the "non-split" stretch path:
/// AI ops still split + recombine, but a single MTF curve shapes the
/// final composite rather than each plate getting its own stretch.
/// Mutually exclusive with <see cref="GhsStretchFinalStep"/> and with the
/// dual-stretch step set (<see cref="StretchStarsStep"/>,
/// <see cref="StretchStarlessStep"/>, <see cref="GhsStretchStarlessStep"/>,
/// <see cref="BackgroundReduceStep"/>, <see cref="CompressHighlightsStep"/>).
/// </summary>
/// <param name="TargetMedian">MTF target median in (0, 1); default
/// <c>0.25</c> matches the PixInsight STF convention.</param>
public sealed record MtfStretchFinalStep(double TargetMedian = 0.25) : SharpenStep;

/// <summary>GHS chain applied to the recombined <c>final</c> plate AFTER
/// <see cref="RecombineStep"/>. The "non-split" GHS path: AI ops split +
/// recombine, then a single GHS curve (optionally multi-stage per Cranfield's
/// canonical workflow) shapes the final composite. Field semantics mirror
/// <see cref="GhsStretchStarlessStep"/>; see its xmldoc for details on each.
/// Mutually exclusive with <see cref="MtfStretchFinalStep"/> and with the
/// dual-stretch step set.</summary>
public sealed record GhsStretchFinalStep(
    double LnD = 1.3,
    double B = 8.0,
    double? SP = null,
    double LP = 0.0,
    double HP = 0.8,
    int Passes = 1,
    bool AutoConverge = false,
    double AutoTargetValue = 0.25,
    Image.GhsConvergeTarget AutoTarget = Image.GhsConvergeTarget.Median) : SharpenStep;

/// <summary>Asinh stretch (Siril's color-aware formula) applied to the
/// stars-only plate. Calls <see cref="Image.AsinhStretch"/>. Scales all
/// channels by the same luma-derived factor so star colour is preserved
/// by construction -- the only thing that can desaturate stars under
/// this curve is the <see cref="BlackPoint"/> subtraction near zero.</summary>
/// <param name="Beta">Stretch strength (Siril "stretch" parameter).
/// Typical range 1-1000. Linear stars-only plates usually want 10-50;
/// already-stretched inputs 3-10.</param>
/// <param name="BlackPoint">Subtracted before the scaled output.
/// 0 (default) is correct for stars-only plates (the bg has already
/// been subtracted by RemoveStarsStep). Range [0, 1).</param>
/// <param name="LumaWeights">Weighting profile for the per-pixel luma
/// in the colour formula. <see cref="LumaWeighting.Rec709"/> default.</param>
public sealed record AsinhStretchStarsStep(
    double Beta = 10.0,
    double BlackPoint = 0.0,
    LumaWeighting LumaWeights = LumaWeighting.Rec709) : SharpenStep;

/// <summary>Asinh stretch applied to the starless plate. See
/// <see cref="AsinhStretchStarsStep"/> for math details; same helper,
/// different plate target. Companion to <see cref="StretchStarlessStep"/>
/// (MTF) and <see cref="GhsStretchStarlessStep"/> (GHS) under the
/// <c>--dual-stretch</c> umbrella.</summary>
public sealed record AsinhStretchStarlessStep(
    double Beta = 10.0,
    double BlackPoint = 0.0,
    LumaWeighting LumaWeights = LumaWeighting.Rec709) : SharpenStep;

/// <summary>Asinh stretch applied to the recombined <c>final</c> plate
/// AFTER <see cref="RecombineStep"/>. The non-split-workflow asinh
/// option; companion to <see cref="MtfStretchFinalStep"/> and
/// <see cref="GhsStretchFinalStep"/>. Mutually exclusive with those
/// (validation enforces at most one post-recombine stretch).</summary>
public sealed record AsinhStretchFinalStep(
    double Beta = 10.0,
    double BlackPoint = 0.0,
    LumaWeighting LumaWeights = LumaWeighting.Rec709) : SharpenStep;

/// <summary>
/// Per-plate intermediate-retention selector for <see cref="SharpenRequest.KeepIntermediates"/>.
/// Each flag corresponds to a slot on <see cref="SharpenResult"/>; the pipeline
/// releases plates whose flag is unset as soon as the downstream chain has
/// consumed them. Lets each consumer state exactly which intermediates it
/// needs -- the canonical sharpen flow wants <see cref="None"/> (composite
/// only), a dual-stretch CLI run wants <see cref="StarsAndStarlessLineage"/>,
/// a per-plate analyst wants <see cref="All"/>.
/// </summary>
[Flags]
public enum SharpenIntermediates
{
    /// <summary>Release every intermediate as soon as downstream lands. Only
    /// <see cref="SharpenResult.Final"/> survives. Trims peak memory from
    /// ~8 plates to ~3 for the canonical workflow.</summary>
    None = 0,
    /// <summary>Keep the gradient-corrected source plate (output of
    /// <see cref="GradientCorrectionStep"/>).</summary>
    GradientCorrected   = 1 << 0,
    /// <summary>Keep the raw starless plate (output of <see cref="RemoveStarsStep"/>).</summary>
    Starless            = 1 << 1,
    /// <summary>Keep the stars-only plate (source minus starless).</summary>
    StarsOnly           = 1 << 2,
    /// <summary>Keep the stellar-sharpener output.</summary>
    SharpenedStars      = 1 << 3,
    /// <summary>Keep the non-stellar deconvolver output.</summary>
    DeconvolvedStarless = 1 << 4,
    /// <summary>Keep the denoiser output (and any stretch / bg-reduce / compress
    /// mutations that overwrite this slot in dual-stretch mode).</summary>
    DenoisedStarless    = 1 << 5,
    /// <summary>Preset: both star and starless lineages (everything except
    /// gradient-corrected). What the CLI <c>--dual-stretch</c> path needs to
    /// write per-plate stretched float TIFFs from the most-processed plate
    /// on each side.</summary>
    StarsAndStarlessLineage = Starless | StarsOnly | SharpenedStars | DeconvolvedStarless | DenoisedStarless,
    /// <summary>Keep everything -- previous behaviour, what the CLI
    /// <c>--no-recombine</c> path uses since it writes each plate as a
    /// separate FITS file.</summary>
    All = GradientCorrected | StarsAndStarlessLineage,
}

/// <summary>
/// Inputs to <see cref="SharpenPipeline.ProcessAsync"/>. The pipeline runs
/// <paramref name="Steps"/> in declared order, so the request <i>is</i> the
/// program: callers compose the workflow they want by choosing which
/// <see cref="SharpenStep"/> records to include and in what order.
/// </summary>
/// <param name="Source">Linear-units source image (typically a freshly-
/// loaded calibrated stack). The pipeline reads but never mutates this.</param>
/// <param name="Steps">Ordered program of steps to run. <see cref="SharpenRequest.Canonical"/>
/// returns Frank Sackenheim's gradient -> stars -> detail -> stretch order.</param>
/// <param name="KeepIntermediates">Which intermediate plates to retain on the
/// returned <see cref="SharpenResult"/>. Default <see cref="SharpenIntermediates.All"/>
/// preserves backwards-compatible behaviour -- every plate is exposed. Set
/// to <see cref="SharpenIntermediates.None"/> for the canonical "just give
/// me the composite" path (drops peak memory from ~8 plates to ~3), or to
/// <see cref="SharpenIntermediates.StarsAndStarlessLineage"/> when a caller
/// wants per-plate outputs (e.g. dual-stretch TIFF export) without paying
/// for the gradient-corrected plate.</param>
public sealed record SharpenRequest(Image Source, ImmutableArray<SharpenStep> Steps, SharpenIntermediates KeepIntermediates = SharpenIntermediates.All)
{
    /// <summary>Returns a request with the canonical "sharpen everything"
    /// workflow per Frank Sackenheim: gradient correction, remove stars,
    /// sharpen stars, deconvolve starless, denoise starless, recombine.
    /// All steps at default blend strength.</summary>
    public static SharpenRequest Canonical(Image source) => new(source,
    [
        new GradientCorrectionStep(),
        new RemoveStarsStep(),
        new SharpenStarsStep(),
        new DeconvolveStarlessStep(),
        new DenoiseStarlessStep(),
        new RecombineStep(),
    ]);
}

/// <summary>
/// How <see cref="RemoveStarsStep.SplitMode"/> derives the stars-only plate
/// and how <see cref="RecombineStep.Mode"/> reassembles the final output.
/// </summary>
public enum RecombineMode
{
    /// <summary>
    /// Split: <c>StarsOnly = max(Source - Starless, 0)</c>;
    /// Recombine: <c>Final = SharpenedStars + DenoisedStarless</c>.
    /// Physically correct in linear-light photon space -- two light sources
    /// sum on the sensor. Default since <see cref="IImageEnhancer"/> works
    /// linear in / linear out.
    /// </summary>
    Additive = 0,

    /// <summary>
    /// Split: <c>StarsOnly = unscreen(Source, Starless) = 1 - (1-Source)/(1-Starless)</c>;
    /// Recombine: <c>Final = screen(bg, fg) = 1 - (1-bg) * (1-fg)</c>.
    /// Matches the stretched-space identity NAFNet star-removers were
    /// trained against. Use when callers pass pre-stretched data or want
    /// to round-trip through the network's training identity.
    /// </summary>
    Screen = 1,
}

/// <summary>
/// Output of <see cref="SharpenPipeline.ProcessAsync"/>. Each property is
/// non-null iff the corresponding step ran. The caller owns disposal of
/// every returned image -- call <see cref="Image.Release"/> on each when
/// done.
/// </summary>
/// <param name="Final">Composite result. Present iff the request included
/// a <see cref="RecombineStep"/>.</param>
/// <param name="Starless">Star-removal output. Present iff the request
/// included a <see cref="RemoveStarsStep"/>.</param>
/// <param name="StarsOnly">Source minus Starless (additive) or
/// <c>unscreen(source, starless)</c> (screen). Present iff the request
/// included a <see cref="RemoveStarsStep"/>. May be the SCNR-mutated plate
/// when a <see cref="ScnrStarsStep"/> ran without a preceding
/// <see cref="SharpenStarsStep"/>.</param>
/// <param name="SharpenedStars">Stellar sharpener output of
/// <paramref name="StarsOnly"/> (possibly SCNR-mutated afterwards if a
/// <see cref="ScnrStarsStep"/> followed). Present iff the request included
/// a <see cref="SharpenStarsStep"/>.</param>
/// <param name="DeconvolvedStarless">Non-stellar deconvolver output of
/// <paramref name="Starless"/>. Present iff the request included a
/// <see cref="DeconvolveStarlessStep"/>.</param>
/// <param name="DenoisedStarless">Denoiser output of the post-deconv
/// starless plate (or the raw <paramref name="Starless"/> when no
/// deconv step preceded). Present iff the request included a
/// <see cref="DenoiseStarlessStep"/>.</param>
/// <param name="GradientCorrected">Background-gradient-corrected plate
/// produced by <see cref="GradientCorrectionStep"/>. Replaces the
/// original source for downstream steps that read it. Present iff the
/// request included a <see cref="GradientCorrectionStep"/>.</param>
/// <param name="InputNoise">Per-channel σ of the input image as measured
/// by <see cref="Image.EstimateNoiseProfile"/> (MAD × 1.4826, unit-scaled).
/// Always populated. Default <see cref="ImmutableArray{T}.Empty"/> only on
/// the mostly-empty failure return; success paths always have at least
/// 1 (mono) or 3 (RGB) entries.</param>
/// <param name="FinalNoise">Per-channel σ of the most-processed LINEAR
/// plate (denoised > deconvolved > raw starless), NOT the recombined
/// composite. Pairs with <paramref name="InputNoise"/> for an apples-to-
/// apples linear-domain "AI removed X% noise" delta -- the composite is
/// excluded because <c>--dual-stretch</c> renders it in stretched space,
/// which would conflate AI noise reduction with histogram redistribution.</param>
public sealed record SharpenResult(
    Image? Final,
    Image? Starless,
    Image? StarsOnly,
    Image? SharpenedStars,
    Image? DeconvolvedStarless,
    Image? DenoisedStarless,
    Image? GradientCorrected = null,
    ImmutableArray<float> InputNoise = default,
    ImmutableArray<float> FinalNoise = default);
