using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging;

/// <summary>
/// Extension methods that register TianWen's own <see cref="IImageEnhancer"/> implementations into a
/// service collection. Consumers (CLI, server, viewer composition roots) call <see cref="AddTianWenAi"/>
/// from their DI setup, usually through <c>AddRcAstroAi()</c>, which layers the RC-Astro roles on top;
/// <see cref="TianWen.Lib"/> stays free of any ONNX Runtime dependency.
/// </summary>
public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the enhancers TianWen ships itself, plus supporting infrastructure, as singletons.
    /// Idempotent: repeated calls are no-ops (uses <c>TryAdd*</c> under the hood) so composition
    /// roots can safely call this from multiple places.
    /// </summary>
    /// <remarks>
    /// Registers:
    /// <list type="bullet">
    /// <item><see cref="IModelResolver"/> -> <see cref="ModelResolver"/> (default search paths).</item>
    /// <item><see cref="IPsfEstimator"/> -> <see cref="HfdPsfEstimator"/> (whole-image scalar via FindStarsAsync).</item>
    /// <item><see cref="IDenoiseEnhancer"/> -> <see cref="N2nDenoiser"/>, the in-house OSC model; it
    /// declines mono input through <see cref="IEnhancerAvailability"/>, so the canonical program simply
    /// leaves the denoise out there.</item>
    /// <item><see cref="IGradientCorrector"/> -> <see cref="FallbackGradientCorrector"/>: <see cref="OnnxBackgroundExtractor"/>
    /// (GraXpert BGE) when its weights are installed, else the classical <see cref="ClassicalBackgroundExtractor"/>,
    /// which is also registered as <see cref="IBackgroundExtractor"/> in its own right.</item>
    /// <item><see cref="SharpenPipeline"/>, the orchestrator.</item>
    /// </list>
    /// <para><b>No star remover, stellar sharpener or starless deconvolver is registered here</b>: TianWen
    /// has no model for those roles yet (the SETI Astro ones went with the SAS tier on 2026-09-26, whose
    /// licence allows use only within SASpro). <c>AddRcAstroAi()</c> serves them where RC-Astro is
    /// licensed, and <see cref="SharpenPipeline.CanonicalProgram"/> runs whole-frame where nothing
    /// does. An in-house model for a role arrives as one more registration here.</para>
    /// </remarks>
    public static IServiceCollection AddTianWenAi(this IServiceCollection services)
    {
        services.TryAddSingleton<IModelResolver, ModelResolver>();
        services.TryAddSingleton<IPsfEstimator, HfdPsfEstimator>();
        services.TryAddSingleton<IDenoiseEnhancer>(sp => new N2nDenoiser(
            sp.GetRequiredService<IModelResolver>(),
            sp.GetService<ILogger<N2nDenoiser>>()));
        // Gradient correction is the one role with an AI-free implementation in TianWen.Lib, so a missing
        // GraXpert install degrades to it instead of to a missing-model failure.
        services.TryAddSingleton<OnnxBackgroundExtractor>();
        services.AddClassicalBackgroundExtractor();
        services.TryAddSingleton<IGradientCorrector>(sp => new FallbackGradientCorrector(
            sp.GetRequiredService<IModelResolver>(),
            sp.GetRequiredService<OnnxBackgroundExtractor>(),
            sp.GetRequiredService<ClassicalBackgroundExtractor>(),
            sp.GetService<ILogger<FallbackGradientCorrector>>()));
        // The orchestrator lives in TianWen.Lib (zero-AI dep) but consumers
        // will want both wired together; register it here so a single
        // AddTianWenAi() call sets up the whole sharpen flow.
        services.TryAddSingleton<SharpenPipeline>();
        return services;
    }
}
