using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// Wraps the RC-Astro-vs-SAS choice so it is made on the FIRST actual
    /// enhancement call, not at DI registration or service resolution. This
    /// keeps the (blocking, subprocess-backed) license probe off the
    /// construction path entirely: building the service provider -- even
    /// resolving the enhancer / <c>SharpenPipeline</c> -- spawns no
    /// <c>rc-astro</c> process. The chosen backend is cached after first use.
    /// </summary>
    internal abstract class DeferredEnhancer(
        IRcAstroCli cli,
        string productKey,
        Func<IImageEnhancer> rcFactory,
        Func<IImageEnhancer> fallbackFactory)
    {
        private IImageEnhancer? _backend;

        /// <summary>
        /// The resolved backend: RC-Astro when the CLI is present AND
        /// <paramref name="productKey"/> is licensed, else the SAS fallback.
        /// The license probe runs here, on first access, and is itself cached in
        /// <see cref="RcAstroCli"/>. Lock-free single init via
        /// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/>; a lost
        /// race just discards a stateless duplicate wrapper.
        /// </summary>
        internal IImageEnhancer Backend
        {
            get
            {
                var existing = _backend;
                if (existing is not null)
                {
                    return existing;
                }
                var candidate = cli.IsAvailable && cli.IsLicensed(productKey) ? rcFactory() : fallbackFactory();
                return Interlocked.CompareExchange(ref _backend, candidate, null) ?? candidate;
            }
        }

        public string Name => Backend.Name;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Backend.EnhanceAsync(input, cancellationToken);
    }

    /// <summary>Deferred sxt -&gt; <see cref="IStarRemover"/> dispatcher.</summary>
    internal sealed class DeferredStarRemover(
        IRcAstroCli cli, Func<IImageEnhancer> rcFactory, Func<IImageEnhancer> fallbackFactory)
        : DeferredEnhancer(cli, "sxt", rcFactory, fallbackFactory), IStarRemover
    {
    }

    /// <summary>Deferred bxt -&gt; <see cref="INonStellarDeconvolver"/> dispatcher.</summary>
    internal sealed class DeferredNonStellarDeconvolver(
        IRcAstroCli cli, Func<IImageEnhancer> rcFactory, Func<IImageEnhancer> fallbackFactory)
        : DeferredEnhancer(cli, "bxt", rcFactory, fallbackFactory), INonStellarDeconvolver
    {
    }

    /// <summary>Deferred bxt -&gt; <see cref="IImageDeblurrer"/> dispatcher
    /// (full-image deconvolution). Falls back to a no-op passthrough when bxt is
    /// present but unlicensed.</summary>
    internal sealed class DeferredDeblurrer(
        IRcAstroCli cli, Func<IImageEnhancer> rcFactory, Func<IImageEnhancer> fallbackFactory)
        : DeferredEnhancer(cli, "bxt", rcFactory, fallbackFactory), IImageDeblurrer
    {
    }

    /// <summary>Deferred nxt -&gt; <see cref="IDenoiseEnhancer"/> dispatcher.</summary>
    internal sealed class DeferredDenoiser(
        IRcAstroCli cli, Func<IImageEnhancer> rcFactory, Func<IImageEnhancer> fallbackFactory)
        : DeferredEnhancer(cli, "nxt", rcFactory, fallbackFactory), IDenoiseEnhancer
    {
        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, CancellationToken cancellationToken = default)
            => Backend is IDenoiseEnhancer denoiser
                ? denoiser.EnhanceAsync(input, variant, cancellationToken)
                : EnhanceAsync(input, cancellationToken);
    }
}
