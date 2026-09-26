using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// Registers the RC-Astro CLI-backed enhancers, PREFERRING them whenever the RC-Astro CLI is present
    /// AND the relevant product is licensed on this machine. Where RC-Astro is absent or a product is
    /// unlicensed, a role falls back to TianWen's own model where it has one (the denoise role's N2N
    /// model) and otherwise reports that it cannot serve, so the canonical program leaves it out
    /// (<see cref="SharpenPipeline.CanonicalProgram"/>): without StarXTerminator the program is
    /// whole-frame.
    /// </summary>
    /// <remarks>
    /// The RC-vs-in-house decision (and its blocking, subprocess-backed license probe) is made lazily
    /// on first use via the <see cref="DeferredEnhancer"/> proxy -- NOT at registration or service
    /// resolution. So composing a service collection and building/resolving the provider (including
    /// <c>SharpenPipeline</c>) never spawns an <c>rc-astro</c> process; only the first actual enhance
    /// (or the canonical program's availability question just before it) does, once, cached thereafter.
    /// </remarks>
    public static class RcAstroServiceCollectionExtensions
    {
        public static IServiceCollection AddRcAstroAi(this IServiceCollection services)
        {
            services.AddTianWenAi();

            services.TryAddSingleton<IRcAstroCli>(sp =>
                new RcAstroCli(sp.GetService<ILogger<RcAstroCli>>()));

            services.Replace(ServiceDescriptor.Singleton<IStarRemover>(sp =>
                new DeferredStarRemover(
                    sp.GetRequiredService<IRcAstroCli>(),
                    () => new RcAstroStarRemover(sp.GetRequiredService<IRcAstroCli>(), sp.GetService<ILogger<RcAstroStarRemover>>()))));

            // The denoise role's in-house lane is the N2N model: EnhanceBackend.N2n selects it
            // explicitly, and Auto falls back to it where nxt is unlicensed. Constructed lazily like
            // the others; its model file resolves on first use, never at DI build.
            services.Replace(ServiceDescriptor.Singleton<IDenoiseEnhancer>(sp =>
                new DeferredDenoiser(
                    sp.GetRequiredService<IRcAstroCli>(),
                    () => new RcAstroDenoiser(sp.GetRequiredService<IRcAstroCli>(), sp.GetService<ILogger<RcAstroDenoiser>>()),
                    () => new N2nDenoiser(sp.GetRequiredService<IModelResolver>(), sp.GetService<ILogger<N2nDenoiser>>()))));

            services.Replace(ServiceDescriptor.Singleton<INonStellarDeconvolver>(sp =>
                new DeferredNonStellarDeconvolver(
                    sp.GetRequiredService<IRcAstroCli>(),
                    () => new RcAstroNonStellarDeconvolver(sp.GetRequiredService<IRcAstroCli>(), sp.GetService<ILogger<RcAstroNonStellarDeconvolver>>()))));

            // IImageDeblurrer (full-image BlurX) is RC-only. Registered ONLY when the CLI is installed
            // (a cheap filesystem check, no subprocess); the bxt license probe stays deferred, and an
            // installed-but-unlicensed bxt resolves to a passthrough that reports it cannot serve.
            if (RcAstroCli.IsInstalled)
            {
                services.TryAddSingleton<IImageDeblurrer>(sp =>
                    new DeferredDeblurrer(
                        sp.GetRequiredService<IRcAstroCli>(),
                        () => new RcAstroDeblurrer(sp.GetRequiredService<IRcAstroCli>(), sp.GetService<ILogger<RcAstroDeblurrer>>()),
                        () => new PassthroughDeblurrer()));
            }

            return services;
        }
    }
}
