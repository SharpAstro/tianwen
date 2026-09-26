using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// Wraps one role's backend choice so it is made on the FIRST actual enhancement call (or the first
    /// <see cref="IEnhancerAvailability.CanServe"/>, which the canonical program asks right before it
    /// runs), not at DI registration or service resolution. This keeps the (blocking,
    /// subprocess-backed) license probe off the construction path entirely: building the service
    /// provider -- even resolving the enhancer / <c>SharpenPipeline</c> -- spawns no <c>rc-astro</c>
    /// process. The probe result is cached in <c>RcAstroCli</c>.
    /// </summary>
    /// <remarks>
    /// The choice, in order: RC-Astro when its product is licensed; else TianWen's own model where
    /// the role has one (<paramref name="inHouseFactory"/>: the denoise role's N2N today, the
    /// deconvolver's at E7); else <paramref name="declinedFactory"/> where the role has a do-nothing
    /// stand-in (the deblurrer's passthrough); else nothing, and the role reports that it cannot
    /// serve, which is what keeps the canonical program from asking for it. There is no SETI Astro
    /// fallback any more: that tier was removed on 2026-09-26 (its model licence allows use only within
    /// SASpro).
    /// </remarks>
    internal abstract class DeferredEnhancer(
        IRcAstroCli cli,
        string productKey,
        string role,
        Func<IImageEnhancer> rcFactory,
        Func<IImageEnhancer>? inHouseFactory = null,
        Func<IImageEnhancer>? declinedFactory = null) : IEnhancerAvailability
    {
        private IImageEnhancer? _rc;
        private IImageEnhancer? _inHouse;
        private IImageEnhancer? _declined;

        // The backend instances are constructed lazily + memoized (stateless wrappers, so a lost race
        // just discards a duplicate). The DECISION is re-evaluated per call from EnhanceOptions.Backend
        // (see Resolve), so a caller can force RC-Astro for one enhance and Auto for the next.
        private IImageEnhancer Rc => Memoize(ref _rc, rcFactory);

        /// <summary>The in-house TianWen model for this role, or <c>null</c> where the role has none.</summary>
        private protected IImageEnhancer? InHouse => inHouseFactory is null ? null : Memoize(ref _inHouse, inHouseFactory);

        private IImageEnhancer? Declined => declinedFactory is null ? null : Memoize(ref _declined, declinedFactory);

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
        /// Picks the backend for <paramref name="backend"/>: <see cref="EnhanceBackend.ForceRcAstro"/>
        /// -&gt; RC whenever the CLI binary is present (license gate skipped), else the in-house model;
        /// <see cref="EnhanceBackend.N2n"/> -&gt; the in-house model where this role HAS one, and the
        /// Auto behaviour where it does not -- the same options record reaches every role in a pipeline
        /// run, so a role without an in-house lane must keep working rather than throw;
        /// <see cref="EnhanceBackend.Auto"/> -&gt; RC when present AND licensed, else the in-house model.
        /// Falls to the declined stand-in, or <c>null</c>, when none of those exists.
        /// </summary>
        private protected IImageEnhancer? Resolve(EnhanceBackend backend) => (backend switch
        {
            EnhanceBackend.ForceRcAstro => cli.IsAvailable ? Rc : InHouse,
            EnhanceBackend.N2n when InHouse is { } inHouse => inHouse,
            _ => cli.IsAvailable && cli.IsLicensed(productKey) ? Rc : InHouse,
        }) ?? Declined;

        /// <summary>The backend for <paramref name="backend"/>, or a failure that names what would serve.</summary>
        private protected IImageEnhancer Require(EnhanceBackend backend)
            => Resolve(backend) ?? throw new InvalidOperationException(
                $"No backend serves {role}: RC-Astro '{productKey}' is not installed or not licensed, and TianWen has no model for this role yet.");

        /// <summary>Whether a backend serves an input of <paramref name="channelCount"/> channels under
        /// <paramref name="options"/>, deferring to that backend's own answer where it has one (the N2N
        /// model declines mono; the passthrough declines everything).</summary>
        public bool CanServe(int channelCount, EnhanceOptions options)
            => Resolve(options.Backend) is { } chosen
               && (chosen is not IEnhancerAvailability availability || availability.CanServe(channelCount, options));

        /// <summary>The Auto-resolved backend (used by <see cref="Name"/> and the param-less path).</summary>
        internal IImageEnhancer? Backend => Resolve(EnhanceBackend.Auto);

        public string Name => Backend?.Name ?? $"{role} (no backend: RC-Astro '{productKey}' unavailable)";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Require(EnhanceBackend.Auto).EnhanceAsync(input, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Require(options.Backend).EnhanceAsync(input, options, progress, cancellationToken);
    }

    /// <summary>Deferred sxt -&gt; <see cref="IStarRemover"/> dispatcher. RC-Astro only until TianWen's
    /// own star remover ships.</summary>
    internal sealed class DeferredStarRemover(IRcAstroCli cli, Func<IImageEnhancer> rcFactory)
        : DeferredEnhancer(cli, "sxt", "star removal", rcFactory), IStarRemover
    {
    }

    /// <summary>Deferred bxt -&gt; <see cref="INonStellarDeconvolver"/> dispatcher. RC-Astro only until
    /// TianWen's own deconvolver ships (deconvolver-training.md E7), which arrives as the in-house lane.</summary>
    internal sealed class DeferredNonStellarDeconvolver(IRcAstroCli cli, Func<IImageEnhancer> rcFactory)
        : DeferredEnhancer(cli, "bxt", "starless deconvolution", rcFactory), INonStellarDeconvolver
    {
    }

    /// <summary>Deferred bxt -&gt; <see cref="IImageDeblurrer"/> dispatcher (full-image
    /// deconvolution). Declines to a no-op passthrough when bxt is present but unlicensed, which
    /// reports that it cannot serve, so the canonical program leaves the step out.</summary>
    internal sealed class DeferredDeblurrer(IRcAstroCli cli, Func<IImageEnhancer> rcFactory, Func<IImageEnhancer> declinedFactory)
        : DeferredEnhancer(cli, "bxt", "whole-frame deblur", rcFactory, declinedFactory: declinedFactory), IImageDeblurrer
    {
    }

    /// <summary>
    /// Deferred nxt -&gt; <see cref="IDenoiseEnhancer"/> dispatcher: RC-Astro NoiseXTerminator where
    /// licensed, else the in-house N2N model (3-channel input), else nothing. <see cref="EnhanceBackend.N2n"/>
    /// routes to the in-house model explicitly.
    /// </summary>
    internal sealed class DeferredDenoiser(IRcAstroCli cli, Func<IImageEnhancer> rcFactory, Func<IImageEnhancer>? inHouseFactory = null)
        : DeferredEnhancer(cli, "nxt", "denoise", rcFactory, inHouseFactory), IDenoiseEnhancer
    {
        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, CancellationToken cancellationToken = default)
            => Require(EnhanceBackend.Auto) is IDenoiseEnhancer denoiser
                ? denoiser.EnhanceAsync(input, variant, cancellationToken)
                : EnhanceAsync(input, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
            => Require(options.Backend) is IDenoiseEnhancer denoiser
                ? denoiser.EnhanceAsync(input, variant, options, progress, cancellationToken)
                : EnhanceAsync(input, options, progress, cancellationToken);
    }
}
