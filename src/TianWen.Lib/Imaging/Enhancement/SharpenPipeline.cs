using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Imaging.Enhancement;

/// <summary>
/// Composes the three atomic AI4 enhancers into the canonical sharpen flow:
/// (1) star removal -> starless plate, (2) split <c>StarsOnly = Source -
/// Starless</c>, (3a) stellar sharpening on the stars-only plate, (3b)
/// non-stellar deconvolution on the starless plate, (4) optional
/// recombine <c>Final = SharpenedStars + DeconvolvedStarless</c>.
/// </summary>
/// <remarks>
/// <para>Lives in <c>TianWen.Lib</c> with zero ONNX dependency -- the
/// orchestrator only talks to the role-typed enhancer interfaces
/// (<see cref="IStarRemover"/>, <see cref="IStellarSharpener"/>,
/// <see cref="INonStellarDeconvolver"/>), so consumers can substitute
/// classical fallbacks or alternative model backends without changing the
/// pipeline. All three enhancer dependencies are nullable so the pipeline
/// is constructible even when no concrete impls are registered; it throws
/// only when <see cref="ProcessAsync"/> is invoked with a step enabled
/// whose corresponding enhancer wasn't supplied.</para>
///
/// <para>Each step is optional and the request validation enforces the
/// few cross-step constraints (stellar sharpening + non-stellar deconv
/// need the starless plate; recombine needs at least one of the two).
/// Intermediates are returned in <see cref="SharpenResult"/> so callers
/// can stop at any step (starless export, deconvolved-but-no-stars, etc.).</para>
/// </remarks>
public sealed class SharpenPipeline(
    IStarRemover? starRemover = null,
    IStellarSharpener? stellarSharpener = null,
    INonStellarDeconvolver? nonStellarDeconvolver = null,
    ILogger<SharpenPipeline>? logger = null)
{
    public async Task<SharpenResult> ProcessAsync(SharpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ValidateRequest(request);

        var totalSw = Stopwatch.StartNew();
        var (channels, srcW, srcH) = request.Source.Shape;
        logger?.LogDebug("SharpenPipeline.ProcessAsync: input {W}x{H}x{C} runs=[star={RunStar}, stellar={RunStellar}, deconv={RunDeconv}, recombine={Recombine}]",
            srcW, srcH, channels,
            request.RunStarRemoval, request.RunStellarSharpen, request.RunNonStellarDeconv, request.Recombine);

        Image? starless = null;
        Image? starsOnly = null;
        Image? sharpenedStars = null;
        Image? deconvolvedStarless = null;
        Image? final = null;

        long darkstarMs = 0, splitMs = 0, stellarMs = 0, deconvMs = 0, recombineMs = 0;
        var phaseSw = Stopwatch.StartNew();

        try
        {
            if (request.RunStarRemoval)
            {
                starless = await starRemover!.EnhanceAsync(request.Source, cancellationToken);
                darkstarMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();

                // Pixel split: extract the stars-only plate.
                // Additive (default): StarsOnly = max(Source - Starless, 0).
                //   Physically correct in linear-light photon space (the
                //   default contract for AI4 enhancers in this codebase).
                // Screen: StarsOnly = unscreen(Source, Starless).
                //   Matches the stretched-space identity NAFNet was trained
                //   against -- prefer when callers pass pre-stretched data
                //   or want to round-trip through the screen identity.
                starsOnly = request.Mode == RecombineMode.Screen
                    ? request.Source.Unscreen(starless)
                    : request.Source.Subtract(starless);
                splitMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();
            }

            // Stellar sharpen + non-stellar deconv run sequentially in v1.
            // Both are CPU-bound through ORT and the singleton sessions
            // serialize themselves anyway; parallelising adds complexity for
            // little gain on a single-GPU box. Worth revisiting if a future
            // user has multiple inference accelerators.
            if (request.RunStellarSharpen)
            {
                var rawStellar = await stellarSharpener!.EnhanceAsync(starsOnly!, cancellationToken);
                // AI-strength blend: lerp from the input plate (StarsOnly) toward
                // the network output. Default StellarBlend = 1.0 keeps the existing
                // "full AI" behaviour; values < 1 mitigate the "snap to pixel"
                // artefact AI4 produces on tight star fields.
                sharpenedStars = request.StellarBlend < 1f
                    ? starsOnly!.Lerp(rawStellar, request.StellarBlend)
                    : rawStellar;
                if (!ReferenceEquals(sharpenedStars, rawStellar)) rawStellar.Release();
                stellarMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();
            }

            if (request.RunNonStellarDeconv)
            {
                var rawDeconv = await nonStellarDeconvolver!.EnhanceAsync(starless!, cancellationToken);
                deconvolvedStarless = request.DeconvBlend < 1f
                    ? starless!.Lerp(rawDeconv, request.DeconvBlend)
                    : rawDeconv;
                if (!ReferenceEquals(deconvolvedStarless, rawDeconv)) rawDeconv.Release();
                deconvMs = phaseSw.ElapsedMilliseconds; phaseSw.Restart();
            }

            // SCNR (Subtractive Chromatic Noise Reduction) on the stellar
            // branch ONLY. Applied to (SharpenedStars ?? StarsOnly) before
            // recombine -- the starless plate is left untouched to preserve
            // legitimate green nebula signal (OIII / H-beta emission). Skipped
            // outright when no stellar plate is in flight (RunStarRemoval=false).
            if (request.StarsScnrMode != ScnrMode.None && starsOnly is not null)
            {
                var beforeScnr = sharpenedStars ?? starsOnly;
                var afterScnr = beforeScnr.SubtractiveChromaticNoise(request.StarsScnrMode, request.StarsScnrAmount);
                if (sharpenedStars is not null)
                {
                    sharpenedStars.Release();
                    sharpenedStars = afterScnr;
                }
                else
                {
                    starsOnly.Release();
                    starsOnly = afterScnr;
                }
            }

            if (request.Recombine)
            {
                // Recombine: Final = bg + fg (additive) or screen(bg, fg).
                // Pass-through when a step was disabled.
                var bg = deconvolvedStarless ?? starless!;
                var fg = sharpenedStars ?? starsOnly!;
                final = request.Mode == RecombineMode.Screen ? bg.Screen(fg) : bg.Add(fg);
                recombineMs = phaseSw.ElapsedMilliseconds;
            }
        }
        catch
        {
            // On failure, release any intermediates we've allocated so the
            // camera buffer pool doesn't grow unboundedly across failed
            // runs.
            starless?.Release();
            starsOnly?.Release();
            sharpenedStars?.Release();
            deconvolvedStarless?.Release();
            final?.Release();
            throw;
        }

        logger?.LogInformation(
            "SharpenPipeline.ProcessAsync: {W}x{H}x{C} darkstar={Darkstar}ms split={Split}ms stellar={Stellar}ms deconv={Deconv}ms recombine={Recombine}ms total={Total}ms",
            srcW, srcH, channels, darkstarMs, splitMs, stellarMs, deconvMs, recombineMs, totalSw.ElapsedMilliseconds);

        return new SharpenResult(
            Final: final,
            Starless: starless,
            StarsOnly: starsOnly,
            SharpenedStars: sharpenedStars,
            DeconvolvedStarless: deconvolvedStarless);
    }

    private void ValidateRequest(SharpenRequest request)
    {
        // Cross-step dependencies. Star removal is the gate -- everything
        // downstream operates on either the starless or stars-only plate.
        if (request.RunStellarSharpen && !request.RunStarRemoval)
        {
            throw new ArgumentException(
                "SharpenRequest: RunStellarSharpen requires RunStarRemoval (no starless plate -> no stars-only plate).",
                nameof(request));
        }
        if (request.RunNonStellarDeconv && !request.RunStarRemoval)
        {
            throw new ArgumentException(
                "SharpenRequest: RunNonStellarDeconv requires RunStarRemoval (deconv operates on the starless plate).",
                nameof(request));
        }
        if (request.Recombine && !request.RunStarRemoval)
        {
            throw new ArgumentException(
                "SharpenRequest: Recombine requires RunStarRemoval (nothing to recombine).",
                nameof(request));
        }
        // Recombine is meaningful when ANY plate transformation runs: AI
        // sharpen (stellar / deconv) OR SCNR on the stars plate. If nothing
        // touches a plate, Recombine would just reproduce Source.
        if (request.Recombine && !request.RunStellarSharpen && !request.RunNonStellarDeconv && request.StarsScnrMode == ScnrMode.None)
        {
            throw new ArgumentException(
                "SharpenRequest: Recombine requires at least one of RunStellarSharpen, RunNonStellarDeconv, " +
                "or StarsScnrMode (otherwise Final would just equal Source -- caller can skip the pipeline).",
                nameof(request));
        }

        // DI-availability: enabled steps must have the corresponding enhancer
        // registered.
        if (request.RunStarRemoval && starRemover is null)
        {
            throw new InvalidOperationException(
                "SharpenPipeline: RunStarRemoval requested but no IStarRemover registered. " +
                "Call AddTianWenAi() (or register a custom IStarRemover) in your composition root.");
        }
        if (request.RunStellarSharpen && stellarSharpener is null)
        {
            throw new InvalidOperationException(
                "SharpenPipeline: RunStellarSharpen requested but no IStellarSharpener registered.");
        }
        if (request.RunNonStellarDeconv && nonStellarDeconvolver is null)
        {
            throw new InvalidOperationException(
                "SharpenPipeline: RunNonStellarDeconv requested but no INonStellarDeconvolver registered.");
        }
    }
}

/// <summary>
/// Inputs to <see cref="SharpenPipeline.ProcessAsync"/>. Each step is
/// independently togglable; cross-step constraints are enforced at the top
/// of <c>ProcessAsync</c> before any enhancer is invoked.
/// </summary>
public sealed record SharpenRequest(
    Image Source,
    bool RunStarRemoval = true,
    bool RunStellarSharpen = true,
    bool RunNonStellarDeconv = true,
    bool Recombine = true,
    RecombineMode Mode = RecombineMode.Additive,

    /// <summary>AI strength for the stellar branch. 0 keeps stars at their
    /// original size + colour; 1 uses the network output verbatim; ~0.5 is a
    /// typical good value for tight star fields where AI4 over-sharpens.</summary>
    float StellarBlend = 1.0f,

    /// <summary>AI strength for the non-stellar deconv branch. Same scale as
    /// <see cref="StellarBlend"/>; the nebula often tolerates higher values.</summary>
    float DeconvBlend = 1.0f,

    /// <summary>If not <see cref="ScnrMode.None"/>, applies subtractive
    /// chromatic noise reduction to the stellar plate (only -- the starless
    /// plate keeps legitimate green nebula signal). Run AFTER the stellar
    /// sharpen pass, BEFORE recombine.</summary>
    ScnrMode StarsScnrMode = ScnrMode.None,

    /// <summary>SCNR strength in [0, 1]. 1 = full green-neutralise to the
    /// chosen reference (Average or Maximum). Ignored when
    /// <see cref="StarsScnrMode"/> is <see cref="ScnrMode.None"/>.</summary>
    float StarsScnrAmount = 1.0f);

/// <summary>
/// How <see cref="SharpenPipeline"/> splits the source into a stars-only +
/// starless pair and how it recombines the processed plates into the final
/// output.
/// </summary>
public enum RecombineMode
{
    /// <summary>
    /// <c>StarsOnly = max(Source - Starless, 0)</c>;
    /// <c>Final = SharpenedStars + DeconvolvedStarless</c>. Physically
    /// correct in linear-light photon space -- two light sources sum on
    /// the sensor. Default for TianWen because the
    /// <see cref="IImageEnhancer"/> contract is linear in / linear out.
    /// </summary>
    Additive = 0,

    /// <summary>
    /// <c>StarsOnly = unscreen(Source, Starless) = 1 - (1-Source)/(1-Starless)</c>;
    /// <c>Final = screen(SharpenedStars, DeconvolvedStarless) =
    /// 1 - (1-SharpenedStars) * (1-DeconvolvedStarless)</c>. Matches the
    /// stretched-space identity NAFNet star-removers were trained against
    /// (<c>stretched_source = screen(stretched_starless, stretched_stars)</c>).
    /// Use when callers pass pre-stretched data or want to round-trip
    /// through the network's training identity rather than the
    /// linear-additive one.
    /// </summary>
    Screen = 1,
}

/// <summary>
/// Output of <see cref="SharpenPipeline.ProcessAsync"/>. Each property is
/// non-null iff the corresponding step ran. The caller owns disposal of
/// every returned image -- call <see cref="Image.Release"/> on each when
/// done.
/// </summary>
/// <param name="Final">Composite result. Present iff
/// <see cref="SharpenRequest.Recombine"/>.</param>
/// <param name="Starless">Star-removal output (in source units, but NOT a
/// linear-domain function of the input -- see PLAN-ai-enhancement.md
/// "Domain semantics"). Present iff
/// <see cref="SharpenRequest.RunStarRemoval"/>.</param>
/// <param name="StarsOnly">Source minus Starless, clamped to >= 0. Present
/// iff <see cref="SharpenRequest.RunStarRemoval"/>.</param>
/// <param name="SharpenedStars">Stellar sharpener output of
/// <paramref name="StarsOnly"/>. Present iff
/// <see cref="SharpenRequest.RunStellarSharpen"/>.</param>
/// <param name="DeconvolvedStarless">Non-stellar deconvolver output of
/// <paramref name="Starless"/>. Present iff
/// <see cref="SharpenRequest.RunNonStellarDeconv"/>.</param>
public sealed record SharpenResult(
    Image? Final,
    Image? Starless,
    Image? StarsOnly,
    Image? SharpenedStars,
    Image? DeconvolvedStarless);
