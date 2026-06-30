using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// <see cref="IDenoiseEnhancer"/> backed by RC-Astro NoiseXTerminator (nxt),
    /// driven through the CLI. By default the denoise strength (<c>--dn</c>) is
    /// chosen automatically from the input's measured background noise so short
    /// (noisy) integrations get a heavier touch than deep (clean) ones; pass
    /// <c>autoStrength: false</c> to use a fixed <paramref name="denoise"/>.
    /// </summary>
    /// <param name="autoStrength">When true (default), derive <c>--dn</c> from
    /// <see cref="Image.EstimateNoiseProfile"/>. When false, use
    /// <paramref name="denoise"/> verbatim.</param>
    /// <param name="denoise">Fixed <c>--dn</c> when <paramref name="autoStrength"/>
    /// is false. RC-Astro's own default is 0 (no-op); we default to 0.90.</param>
    /// <param name="minDenoise">Lower bound of the auto <c>--dn</c> band (cleanest input).</param>
    /// <param name="maxDenoise">Upper bound of the auto <c>--dn</c> band (noisiest input).</param>
    /// <param name="iterations">nxt <c>--it</c> denoise iterations (CLI default 2).</param>
    public sealed class RcAstroDenoiser(
        IRcAstroCli cli,
        ILogger<RcAstroDenoiser>? logger = null,
        bool autoStrength = true,
        double denoise = 0.90,
        double minDenoise = 0.70,
        double maxDenoise = 0.95,
        int iterations = 2)
        : RcAstroEnhancerBase(cli, logger), IDenoiseEnhancer
    {
        public override string Name => "RC-Astro NoiseXTerminator (nxt)";

        protected override string ProductKey => "nxt";

        /// <summary>
        /// RC-Astro nxt is a single model; the SAS-specific
        /// <see cref="DenoiseVariant"/> (Lite / Walking) has no nxt equivalent,
        /// so it is ignored and the base denoise path runs.
        /// </summary>
        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, CancellationToken cancellationToken = default)
            => EnhanceAsync(input, variant, EnhanceOptions.Default, null, cancellationToken);

        public Task<Image> EnhanceAsync(Image input, DenoiseVariant variant, EnhanceOptions options, IProgress<float>? progress = null, CancellationToken cancellationToken = default)
        {
            if (variant != DenoiseVariant.Default)
            {
                logger?.LogDebug("RC-Astro nxt ignores DenoiseVariant.{Variant} (single model).", variant);
            }
            // nxt is a single model -> variant is irrelevant; route to the options-aware base path
            // so --dn / --it tuning overrides and progress relay still apply.
            return EnhanceAsync(input, options, progress, cancellationToken);
        }

        protected override IReadOnlyList<string> BuildArgs(Image input, EnhanceTuning? tuning)
        {
            var auto = autoStrength && tuning?.DenoiseStrength is null;
            var dn = tuning?.DenoiseStrength is { } d
                ? (double)d
                : (autoStrength ? MapNoiseToStrength(input.EstimateNoiseProfile(), minDenoise, maxDenoise) : denoise);
            var it = tuning?.DenoiseIterations ?? iterations;
            logger?.LogDebug("RC-Astro nxt dn={Dn:F2} (auto={Auto}) it={It}", dn, auto, it);
            return
            [
                "--dn", dn.ToString("0.00", CultureInfo.InvariantCulture),
                "--it", it.ToString(CultureInfo.InvariantCulture),
            ];
        }

        /// <summary>
        /// Maps a per-channel background-σ profile (from
        /// <see cref="Image.EstimateNoiseProfile"/>, robust σ on the [0, 1]
        /// scale) to a denoise strength in [<paramref name="minStrength"/>,
        /// <paramref name="maxStrength"/>]. Linear in log10(σ) between
        /// <paramref name="sigmaClean"/> (-&gt; min) and
        /// <paramref name="sigmaNoisy"/> (-&gt; max); σ spans several decades on
        /// real data so a log map is the right shape.
        /// </summary>
        internal static double MapNoiseToStrength(
            ImmutableArray<float> perChannelSigma,
            double minStrength,
            double maxStrength,
            double sigmaClean = 1e-4,
            double sigmaNoisy = 1e-2)
        {
            if (perChannelSigma.IsDefaultOrEmpty)
            {
                return (minStrength + maxStrength) / 2.0;
            }

            var sum = 0.0;
            var count = 0;
            foreach (var s in perChannelSigma)
            {
                if (float.IsFinite(s) && s > 0f)
                {
                    sum += s;
                    count++;
                }
            }
            if (count == 0)
            {
                // No measurable noise (e.g. a synthetic flat) -> lightest touch.
                return minStrength;
            }

            var sigma = sum / count;
            var lo = Math.Log10(sigmaClean);
            var hi = Math.Log10(sigmaNoisy);
            var t = Math.Clamp((Math.Log10(sigma) - lo) / (hi - lo), 0.0, 1.0);
            return minStrength + t * (maxStrength - minStrength);
        }
    }
}
