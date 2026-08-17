using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.Onnx;
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
        Func<IImageEnhancer> fallbackFactory,
        Func<IImageEnhancer>? inHouseFactory = null)
    {
        private IImageEnhancer? _rc;
        private IImageEnhancer? _sas;
        private IImageEnhancer? _inHouse;

        // The backend instances are constructed lazily + memoized (stateless wrappers, so a
        // lost race just discards a duplicate). The RC-vs-SAS DECISION is no longer cached: it is
        // re-evaluated per call from EnhanceOptions.Backend (see Resolve), so a caller can force
        // SAS for one enhance and Auto for the next. The (blocking, subprocess-backed) license
        // probe is still only hit on the first Auto/ForceRcAstro call -- never at DI build -- and
        // is itself cached in RcAstroCli.
        private IImageEnhancer Rc => Memoize(ref _rc, rcFactory);
        private protected IImageEnhancer Sas => Memoize(ref _sas, fallbackFactory);

        /// <summary>The in-house TianWen model for this role, or <c>null</c> where the role has
        /// none. Only the denoise role wires one today (<see cref="N2nDenoiser"/>).</summary>
        private protected IImageEnhancer? InHouse => inHouseFactory is null ? null : Memoize(ref _inHouse, inHouseFactory);

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
        /// CLI binary is present (license gate skipped); <see cref="EnhanceBackend.N2n"/> -&gt; the
        /// in-house model where this role HAS one, and the Auto behaviour where it does not -- the
        /// same options record reaches every role in a pipeline run, so a role without an in-house
        /// lane must keep working rather than throw; <see cref="EnhanceBackend.Auto"/> -&gt; RC when
        /// present AND licensed, else SAS.
        /// </summary>
        private protected IImageEnhancer Resolve(EnhanceBackend backend) => backend switch
        {
            EnhanceBackend.ForceSas => Sas,
            EnhanceBackend.ForceRcAstro => cli.IsAvailable ? Rc : Sas,
            EnhanceBackend.N2n when InHouse is { } inHouse => inHouse,
            _ => cli.IsAvailable && cli.IsLicensed(productKey) ? Rc : Sas,
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

    /// <summary>
    /// Deferred nxt -&gt; <see cref="IDenoiseEnhancer"/> dispatcher. The one role with an in-house
    /// lane: <see cref="EnhanceBackend.N2n"/> routes here explicitly, and Auto gains a RESCUE tier
    /// -- when it lands on SAS but the AI4 weights are not installed, an OSC default-variant input
    /// is served by the in-house N2N model instead of dying on a missing file. The rescue replaces
    /// a crash, never a measured backend's result: with the AI4 weights present, Auto behaves
    /// byte-for-byte as before this lane existed.
    /// </summary>
    internal sealed class DeferredDenoiser(
        IRcAstroCli cli,
        Func<IImageEnhancer> rcFactory,
        Func<IImageEnhancer> fallbackFactory,
        Func<IImageEnhancer>? n2nFactory = null,
        IModelResolver? modelResolver = null,
        ILogger? logger = null)
        : DeferredEnhancer(cli, "nxt", rcFactory, fallbackFactory, n2nFactory), IDenoiseEnhancer
    {
        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, CancellationToken cancellationToken = default)
            => Pick(EnhanceBackend.Auto, input, variant) is IDenoiseEnhancer denoiser
                ? denoiser.EnhanceAsync(input, variant, cancellationToken)
                : EnhanceAsync(input, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        {
            var backend = Pick(options.Backend, input, variant);
            return backend is IDenoiseEnhancer denoiser
                ? denoiser.EnhanceAsync(input, variant, options, progress, cancellationToken)
                : backend.EnhanceAsync(input, options, progress, cancellationToken);
        }

        /// <summary>
        /// <see cref="DeferredEnhancer.Resolve"/> plus the Auto rescue tier. The rescue fires only
        /// when ALL of: the request is Auto and resolved to SAS; the input is something the N2N
        /// model serves (3 channels, <see cref="DenoiseVariant.Default"/>); the SAS weights for
        /// this input are NOT on disk; and the N2N weights ARE. A mono input or a Lite/Walking
        /// variant falls through to SAS unchanged, whose missing-model error names the fetch
        /// script -- the right message for the one bundle that could serve it.
        /// </summary>
        private IImageEnhancer Pick(EnhanceBackend backend, Image input, DenoiseVariant variant)
        {
            var chosen = Resolve(backend);
            if (backend is not EnhanceBackend.Auto
                || !ReferenceEquals(chosen, Sas)
                || InHouse is not { } inHouse
                || modelResolver is null
                || variant is not DenoiseVariant.Default
                || input.ChannelCount != 3)
            {
                return chosen;
            }

            var sasModel = OnnxDenoiser.ModelFileNameFor(input.ChannelCount, variant);
            if (modelResolver.TryResolve(sasModel, out _) || !modelResolver.TryResolve(N2nDenoiser.ModelFileName, out _))
            {
                return chosen;
            }

            logger?.LogWarning(
                "Auto denoise: the SAS AI4 weights ({SasModel}) are not installed; serving this OSC input with the in-house N2N model ({N2nModel}) instead. " +
                "Run tools/tianwen-ai-models-fetch.ps1 to install the AI4 bundle, or select a backend explicitly with --ai-backend.",
                sasModel, N2nDenoiser.ModelFileName);
            return inHouse;
        }
    }
}
