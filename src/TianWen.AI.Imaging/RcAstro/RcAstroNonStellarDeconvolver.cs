using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// <see cref="INonStellarDeconvolver"/> backed by RC-Astro BlurXTerminator
    /// (bxt), driven through the CLI. In the SharpenPipeline this runs on the
    /// STARLESS plate, so only nonstellar sharpening is meaningful.
    /// </summary>
    /// <param name="sharpenNonStellar">bxt <c>--sn</c> amount in [0, 1].
    /// RC-Astro's own default is 0 (no-op); we default to 0.90 (PixInsight-like).</param>
    public sealed class RcAstroNonStellarDeconvolver(
        IRcAstroCli cli,
        ILogger<RcAstroNonStellarDeconvolver>? logger = null,
        double sharpenNonStellar = 0.90)
        : RcAstroEnhancerBase(cli, logger), INonStellarDeconvolver
    {
        public override string Name => "RC-Astro BlurXTerminator (bxt)";

        protected override string ProductKey => "bxt";

        // On a starless plate --ss (sharpen stars) and --ash (star halos) are
        // no-ops (no stars), and --ansr (auto nonstellar PSF radius) defaults to
        // true, so we pass only --sn. bxt estimates the PSF itself, so unlike
        // OnnxNonStellarDeconvolver there is no IPsfEstimator dependency.
        protected override IReadOnlyList<string> BuildArgs(Image input, EnhanceTuning? tuning)
        {
            var sn = tuning?.DeblurSharpen is { } v ? (double)v : sharpenNonStellar;
            return ["--sn", sn.ToString("0.00", CultureInfo.InvariantCulture)];
        }
    }
}
