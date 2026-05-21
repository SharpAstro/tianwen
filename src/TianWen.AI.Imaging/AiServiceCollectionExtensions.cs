using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TianWen.AI.Imaging.Onnx;
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
        // The orchestrator lives in TianWen.Lib (zero-AI dep) but consumers
        // will want both wired together; register it here so a single
        // AddTianWenAi() call sets up the whole sharpen flow.
        services.TryAddSingleton<SharpenPipeline>();
        return services;
    }
}
