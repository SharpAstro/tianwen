using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging.BackgroundExtraction;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging;

/// <summary>
/// Extension methods that register the ONNX-backed
/// <see cref="IImageEnhancer"/> implementations into a service collection.
/// Consumers (CLI, server, GUI composition root) call
/// <see cref="AddTianWenAi"/> from their DI setup; <see cref="TianWen.Lib"/>
/// stays free of any ONNX Runtime dependency.
/// </summary>
public static class AiServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AI4 NAFNet enhancers + supporting infrastructure
    /// (<see cref="IModelResolver"/>) as singletons. Idempotent --
    /// repeated calls are no-ops (uses <c>TryAdd*</c> under the hood) so
    /// composition roots can safely call this from multiple places.
    /// </summary>
    /// <remarks>
    /// Currently registers:
    /// <list type="bullet">
    /// <item><see cref="IModelResolver"/> -> <see cref="ModelResolver"/> (default search paths).</item>
    /// <item><see cref="IPsfEstimator"/> -> <see cref="HfdPsfEstimator"/> (whole-image scalar via FindStarsAsync).</item>
    /// <item><see cref="IStarRemover"/> -> <see cref="OnnxStarRemover"/>.</item>
    /// <item><see cref="IStellarSharpener"/> -> <see cref="OnnxStellarSharpener"/>.</item>
    /// <item><see cref="INonStellarDeconvolver"/> -> <see cref="OnnxNonStellarDeconvolver"/>.</item>
    /// <item><see cref="IDenoiseEnhancer"/> -> <see cref="OnnxDenoiser"/>.</item>
    /// <item><see cref="IGradientCorrector"/> -> <see cref="FallbackGradientCorrector"/>: <see cref="OnnxBackgroundExtractor"/>
    /// (GraXpert BGE) when its weights are installed, else the classical <see cref="ClassicalBackgroundExtractor"/>,
    /// which is also registered as <see cref="IBackgroundExtractor"/> in its own right.</item>
    /// </list>
    /// The <c>SharpenPipeline</c> orchestrator (Phase 5) lives in
    /// <c>TianWen.Lib</c> and will be registered there.
    /// </remarks>
    public static IServiceCollection AddTianWenAi(this IServiceCollection services)
    {
        services.TryAddSingleton<IModelResolver, ModelResolver>();
        services.TryAddSingleton<IPsfEstimator, HfdPsfEstimator>();
        services.TryAddSingleton<IStarRemover, OnnxStarRemover>();
        services.TryAddSingleton<IStellarSharpener, OnnxStellarSharpener>();
        services.TryAddSingleton<INonStellarDeconvolver, OnnxNonStellarDeconvolver>();
        services.TryAddSingleton<IDenoiseEnhancer, OnnxDenoiser>();
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

    /// <summary>
    /// Opt in to the in-house Noise2Noise denoiser (<see cref="N2nDenoiser"/>) as the
    /// <see cref="IDenoiseEnhancer"/>, replacing the AI4 NAFNet one. Calls
    /// <see cref="AddTianWenAi"/> first, so it is the only call a consumer needs.
    /// <para>This is the REGISTRATION-time opt-in (the N2N model becomes the denoiser
    /// unconditionally). For per-call selection, composition roots that call
    /// <c>AddRcAstroAi()</c> get the N2N lane on the deferred denoiser instead:
    /// <c>EnhanceBackend.N2n</c> (<c>--ai-backend n2n</c>) selects it per enhance, and Auto
    /// rescues an OSC input with it when the SAS weights are not installed.</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is opt-in rather than the default.</b> The N2N model is measured against
    /// its own ablations on held-out astro masters, and is the best of them. It has never been
    /// compared against <see cref="OnnxDenoiser"/> on the enhance pipeline's own job. Making it the
    /// default on the strength of the first measurement would assert the second, which nobody has
    /// checked -- and it would do so silently, on every <c>--ai-backend sas</c> run.</para>
    ///
    /// <para>It is also OSC-only and throws on mono input, where <see cref="OnnxDenoiser"/> has a
    /// mono weight bundle. A composition root serving mono users wants the default.</para>
    ///
    /// <para>Uses <c>Replace</c> rather than <c>TryAdd</c> deliberately, mirroring
    /// <c>AddRcAstroAi</c>: the caller has named a preference, so a registration already made by
    /// <see cref="AddTianWenAi"/> should lose rather than win.</para>
    /// </remarks>
    public static IServiceCollection AddTianWenN2nDenoiser(this IServiceCollection services)
    {
        services.AddTianWenAi();
        services.Replace(ServiceDescriptor.Singleton<IDenoiseEnhancer, N2nDenoiser>());
        return services;
    }
}
