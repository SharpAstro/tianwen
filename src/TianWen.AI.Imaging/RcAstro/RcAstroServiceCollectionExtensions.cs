using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// Registers the RC-Astro CLI-backed enhancers, PREFERRING them over the
    /// SETI Astro ONNX enhancers whenever the RC-Astro CLI is present AND the
    /// relevant product is licensed on this machine. When RC-Astro is absent or
    /// a product is unlicensed, each role transparently falls back to the SAS
    /// ONNX implementation.
    /// </summary>
    /// <remarks>
    /// The RC-vs-SAS decision (and its blocking, subprocess-backed license
    /// probe) is made lazily on first use via the <see cref="DeferredEnhancer"/>
    /// proxy -- NOT at registration or service resolution. So composing a
    /// service collection and building/resolving the provider (including
    /// <c>SharpenPipeline</c>) never spawns an <c>rc-astro</c> process; only the
    /// first actual <c>EnhanceAsync</c> does, once, cached thereafter.
    /// </remarks>
    public static class RcAstroServiceCollectionExtensions
    {
        public static IServiceCollection AddRcAstroAi(this IServiceCollection services)
        {
            services.AddTianWenAi();

            services.TryAddSingleton<IRcAstroCli>(sp =>
                new RcAstroCli(sp.GetService<ILogger<RcAstroCli>>()));

            PreferRcAstro<IStarRemover, OnnxStarRemover>(services, sp =>
                new DeferredStarRemover(
                    sp.GetRequiredService<IRcAstroCli>(),
                    () => new RcAstroStarRemover(sp.GetRequiredService<IRcAstroCli>(), sp.GetService<ILogger<RcAstroStarRemover>>()),
                    () => sp.GetRequiredService<OnnxStarRemover>()));

            PreferRcAstro<IDenoiseEnhancer, OnnxDenoiser>(services, sp =>
                new DeferredDenoiser(
                    sp.GetRequiredService<IRcAstroCli>(),
                    () => new RcAstroDenoiser(sp.GetRequiredService<IRcAstroCli>(), sp.GetService<ILogger<RcAstroDenoiser>>()),
                    () => sp.GetRequiredService<OnnxDenoiser>()));

            PreferRcAstro<INonStellarDeconvolver, OnnxNonStellarDeconvolver>(services, sp =>
                new DeferredNonStellarDeconvolver(
                    sp.GetRequiredService<IRcAstroCli>(),
                    () => new RcAstroNonStellarDeconvolver(sp.GetRequiredService<IRcAstroCli>(), sp.GetService<ILogger<RcAstroNonStellarDeconvolver>>()),
                    () => sp.GetRequiredService<OnnxNonStellarDeconvolver>()));

            return services;
        }

        /// <summary>
        /// Registers the SAS <typeparamref name="TFallback"/> by its concrete
        /// type (so the proxy can resolve it as the singleton fallback) and
        /// replaces the <typeparamref name="TRole"/> registration with the
        /// deferred proxy produced by <paramref name="proxyFactory"/>.
        /// </summary>
        private static void PreferRcAstro<TRole, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TFallback>(
            IServiceCollection services,
            Func<IServiceProvider, TRole> proxyFactory)
            where TRole : class
            where TFallback : class, TRole
        {
            services.TryAddSingleton<TFallback>();
            services.Replace(ServiceDescriptor.Singleton<TRole>(proxyFactory));
        }
    }
}
