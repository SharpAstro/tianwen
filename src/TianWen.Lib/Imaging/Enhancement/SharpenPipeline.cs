using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
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
    ILogger<SharpenPipeline>? logger = null)
{
    public async Task<SharpenResult> ProcessAsync(SharpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ValidateRequest(request);

        var totalSw = Stopwatch.StartNew();
        var (channels, srcW, srcH) = request.Source.Shape;
        logger?.LogDebug("SharpenPipeline.ProcessAsync: input {W}x{H}x{C} steps=[{Steps}]",
            srcW, srcH, channels, string.Join(", ", request.Steps.Select(s => s.GetType().Name)));

        Image? starless = null;
        Image? starsOnly = null;
        Image? sharpenedStars = null;
        Image? deconvolvedStarless = null;
        Image? denoisedStarless = null;
        Image? final = null;

        var timings = new List<(string Name, long Ms)>(request.Steps.Length);
        var phaseSw = Stopwatch.StartNew();

        try
        {
            foreach (var step in request.Steps)
            {
                phaseSw.Restart();
                switch (step)
                {
                    case RemoveStarsStep removeStars:
                        starless = await starRemover!.EnhanceAsync(request.Source, cancellationToken);
                        // Pixel split:
                        //   Additive (default): StarsOnly = max(Source - Starless, 0).
                        //     Physically correct in linear-light photon space.
                        //   Screen: StarsOnly = unscreen(Source, Starless).
                        //     Matches the stretched-space identity NAFNet was
                        //     trained against -- prefer when callers pass
                        //     pre-stretched data or want to round-trip through
                        //     the screen identity.
                        starsOnly = removeStars.SplitMode == RecombineMode.Screen
                            ? request.Source.Unscreen(starless)
                            : request.Source.Subtract(starless);
                        timings.Add(("remove-stars+split", phaseSw.ElapsedMilliseconds));
                        break;

                    case SharpenStarsStep sharpStep:
                    {
                        // Input is whatever the stars plate currently is -- raw
                        // starsOnly normally, or an SCNR'd version if ScnrStarsStep
                        // ran earlier in the same request.
                        var inputPlate = starsOnly!;
                        var raw = await stellarSharpener!.EnhanceAsync(inputPlate, cancellationToken);
                        sharpenedStars = sharpStep.Blend < 1f
                            ? inputPlate.Lerp(raw, sharpStep.Blend)
                            : raw;
                        if (!ReferenceEquals(sharpenedStars, raw)) raw.Release();
                        timings.Add(("sharpen-stars", phaseSw.ElapsedMilliseconds));
                        break;
                    }

                    case DeconvolveStarlessStep deconvStep:
                    {
                        var inputPlate = starless!;
                        var raw = await nonStellarDeconvolver!.EnhanceAsync(inputPlate, cancellationToken);
                        deconvolvedStarless = deconvStep.Blend < 1f
                            ? inputPlate.Lerp(raw, deconvStep.Blend)
                            : raw;
                        if (!ReferenceEquals(deconvolvedStarless, raw)) raw.Release();
                        timings.Add(("deconv-starless", phaseSw.ElapsedMilliseconds));
                        break;
                    }

                    case DenoiseStarlessStep denoiseStep:
                    {
                        // Denoise sees the most-processed starless: deconv
                        // output if a DeconvolveStarlessStep ran earlier,
                        // otherwise the raw starless plate.
                        var inputPlate = deconvolvedStarless ?? starless!;
                        var raw = await denoiser!.EnhanceAsync(inputPlate, cancellationToken);
                        denoisedStarless = denoiseStep.Blend < 1f
                            ? inputPlate.Lerp(raw, denoiseStep.Blend)
                            : raw;
                        if (!ReferenceEquals(denoisedStarless, raw)) raw.Release();
                        timings.Add(("denoise-starless", phaseSw.ElapsedMilliseconds));
                        break;
                    }

                    case ScnrStarsStep scnrStep:
                    {
                        // SCNR (Subtractive Chromatic Noise Reduction) on the
                        // stellar plate only -- the starless plate keeps
                        // legitimate green nebula signal (OIII / H-beta).
                        // Mutates the most-processed stars plate in-place
                        // (sharpened if present, else raw starsOnly).
                        var inputPlate = sharpenedStars ?? starsOnly!;
                        var afterScnr = inputPlate.SubtractiveChromaticNoise(scnrStep.Mode, scnrStep.Amount);
                        if (sharpenedStars is not null)
                        {
                            sharpenedStars.Release();
                            sharpenedStars = afterScnr;
                        }
                        else
                        {
                            starsOnly!.Release();
                            starsOnly = afterScnr;
                        }
                        timings.Add(("scnr-stars", phaseSw.ElapsedMilliseconds));
                        break;
                    }

                    case RecombineStep recombineStep:
                    {
                        // Most-processed plate wins on each side:
                        //   bg = denoised > deconvolved > raw starless
                        //   fg = sharpened > raw starsOnly
                        var bg = denoisedStarless ?? deconvolvedStarless ?? starless!;
                        var fg = sharpenedStars ?? starsOnly!;
                        final = recombineStep.Mode == RecombineMode.Screen ? bg.Screen(fg) : bg.Add(fg);
                        timings.Add(("recombine", phaseSw.ElapsedMilliseconds));
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
            starless?.Release();
            starsOnly?.Release();
            sharpenedStars?.Release();
            deconvolvedStarless?.Release();
            denoisedStarless?.Release();
            final?.Release();
            throw;
        }

        logger?.LogInformation(
            "SharpenPipeline.ProcessAsync: {W}x{H}x{C} timings={Timings} total={Total}ms",
            srcW, srcH, channels,
            string.Join(" ", timings.Select(t => $"{t.Name}={t.Ms}ms")),
            totalSw.ElapsedMilliseconds);

        return new SharpenResult(
            Final: final,
            Starless: starless,
            StarsOnly: starsOnly,
            SharpenedStars: sharpenedStars,
            DeconvolvedStarless: deconvolvedStarless,
            DenoisedStarless: denoisedStarless);
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
        // produced by an earlier step. Duplicates rejected in v1.
        var hasStarless = false;
        var hasStarsOnly = false;
        var seenTypes = new HashSet<Type>();

        for (var i = 0; i < request.Steps.Length; i++)
        {
            var step = request.Steps[i];
            var t = step.GetType();
            if (!seenTypes.Add(t))
            {
                throw new ArgumentException(
                    $"SharpenRequest.Steps[{i}]: duplicate step of type {t.Name}. Each step type may appear at most once.",
                    nameof(request));
            }
            switch (step)
            {
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
                case RecombineStep:
                    if (!hasStarless || !hasStarsOnly) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: RecombineStep requires a preceding RemoveStarsStep (no plates to recombine).",
                        nameof(request));
                    if (i != request.Steps.Length - 1) throw new ArgumentException(
                        $"SharpenRequest.Steps[{i}]: RecombineStep must be the final step.",
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
public sealed record DenoiseStarlessStep(float Blend = 1.0f) : SharpenStep;

/// <summary>Subtractive Chromatic Noise Reduction on the stars-only plate
/// only -- preserves legitimate green nebula signal (OIII / H-beta) on the
/// starless side. Mutates the most-processed stars plate (sharpened if a
/// <see cref="SharpenStarsStep"/> ran first, else the raw stars plate).</summary>
/// <param name="Mode">Reference used to neutralise green:
/// <see cref="ScnrMode.Average"/> = pull G down to (R+B)/2;
/// <see cref="ScnrMode.Maximum"/> = pull G down to max(R, B).</param>
/// <param name="Amount">SCNR strength in [0, 1]. 1 = full neutralise.</param>
public sealed record ScnrStarsStep(ScnrMode Mode, float Amount = 1.0f) : SharpenStep;

/// <summary>Recombine the processed plates into the final image. Must be
/// the last step when present (validation enforces this).</summary>
/// <param name="Mode">Recombine math:
/// <see cref="RecombineMode.Additive"/> = <c>bg + fg</c> (linear-light
/// correct, two light sources summing on the sensor);
/// <see cref="RecombineMode.Screen"/> = <c>1 - (1-bg) * (1-fg)</c>
/// (matches NAFNet's stretched-space training identity). For round-trip
/// consistency match the <see cref="RemoveStarsStep.SplitMode"/>.</param>
public sealed record RecombineStep(RecombineMode Mode = RecombineMode.Additive) : SharpenStep;

/// <summary>
/// Inputs to <see cref="SharpenPipeline.ProcessAsync"/>. The pipeline runs
/// <paramref name="Steps"/> in declared order, so the request <i>is</i> the
/// program: callers compose the workflow they want by choosing which
/// <see cref="SharpenStep"/> records to include and in what order.
/// </summary>
public sealed record SharpenRequest(Image Source, ImmutableArray<SharpenStep> Steps)
{
    /// <summary>Returns a request with the canonical "sharpen everything"
    /// workflow: remove stars, sharpen stars, deconvolve starless, denoise
    /// starless, recombine. All steps at default blend strength.</summary>
    public static SharpenRequest Canonical(Image source) => new(source,
    [
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
public sealed record SharpenResult(
    Image? Final,
    Image? Starless,
    Image? StarsOnly,
    Image? SharpenedStars,
    Image? DeconvolvedStarless,
    Image? DenoisedStarless);
