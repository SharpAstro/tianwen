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
    /// <item><see cref="IStarRemover"/> -> <see cref="OnnxStarRemover"/>.</item>
    /// <item><see cref="IStellarSharpener"/> -> <see cref="OnnxStellarSharpener"/>.</item>
    /// </list>
    /// Future phases will add <c>INonStellarDeconvolver</c> here. The
    /// <c>SharpenPipeline</c> orchestrator lives in <c>TianWen.Lib</c> and
    /// will be registered there.
    /// </remarks>
    public static IServiceCollection AddTianWenAi(this IServiceCollection services)
    {
        services.TryAddSingleton<IModelResolver, ModelResolver>();
        services.TryAddSingleton<IStarRemover, OnnxStarRemover>();
        services.TryAddSingleton<IStellarSharpener, OnnxStellarSharpener>();
        return services;
    }
}
