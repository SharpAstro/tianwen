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
        private IImageEnhancer? _rc;
        private IImageEnhancer? _sas;

        // The two backend instances are constructed lazily + memoized (stateless wrappers, so a
        // lost race just discards a duplicate). The RC-vs-SAS DECISION is no longer cached: it is
        // re-evaluated per call from EnhanceOptions.Backend (see Resolve), so a caller can force
        // SAS for one enhance and Auto for the next. The (blocking, subprocess-backed) license
        // probe is still only hit on the first Auto/ForceRcAstro call -- never at DI build -- and
        // is itself cached in RcAstroCli.
        private IImageEnhancer Rc => Memoize(ref _rc, rcFactory);
        private IImageEnhancer Sas => Memoize(ref _sas, fallbackFactory);

        private static IImageEnhancer Memoize(ref IImageEnhancer? slot, Func<IImageEnhancer> factory)
        {
            var existing = slot;
            if (existing is not null)
            {
                return existing;
            }
            var candidate = factory();
            return Interlocked.CompareExchange(ref slot, candidate, null) ?? candidate;
        }

        /// <summary>
        /// Picks the backend for <paramref name="backend"/>: <see cref="EnhanceBackend.ForceSas"/>
        /// -&gt; SAS unconditionally; <see cref="EnhanceBackend.ForceRcAstro"/> -&gt; RC whenever the
        /// CLI binary is present (license gate skipped); <see cref="EnhanceBackend.Auto"/> -&gt; RC
        /// when present AND licensed, else SAS.
        /// </summary>
        private protected IImageEnhancer Resolve(EnhanceBackend backend) => backend switch
        {
            EnhanceBackend.ForceSas      => Sas,
            EnhanceBackend.ForceRcAstro  => cli.IsAvailable ? Rc : Sas,
            _                            => cli.IsAvailable && cli.IsLicensed(productKey) ? Rc : Sas,
        };

        /// <summary>The Auto-resolved backend (used by <see cref="Name"/> and the param-less path).</summary>
        internal IImageEnhancer Backend => Resolve(EnhanceBackend.Auto);

        public string Name => Backend.Name;

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Backend.EnhanceAsync(input, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Resolve(options.Backend).EnhanceAsync(input, options, progress, cancellationToken);
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

        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        {
            var backend = Resolve(options.Backend);
            return backend is IDenoiseEnhancer denoiser
                ? denoiser.EnhanceAsync(input, variant, options, progress, cancellationToken)
                : backend.EnhanceAsync(input, options, progress, cancellationToken);
        }
    }
}
