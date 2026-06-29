using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// <see cref="IImageDeblurrer"/> backed by RC-Astro BlurXTerminator (bxt) run
    /// on the FULL image (stars + non-stellar) -- the PixInsight OSC usage, at the
    /// head of the <c>DeblurFirst</c> pipeline. Tightens stars in place, so no
    /// separate stellar-sharpen step is needed downstream.
    /// </summary>
    /// <param name="sharpenStars">bxt <c>--ss</c> in [0, 0.7]. Default 0.50.</param>
    /// <param name="sharpenNonStellar">bxt <c>--sn</c> in [0, 1]. Default 0.90.</param>
    public sealed class RcAstroDeblurrer(
        IRcAstroCli cli,
        ILogger<RcAstroDeblurrer>? logger = null,
        double sharpenStars = 0.50,
        double sharpenNonStellar = 0.90)
        : RcAstroEnhancerBase(cli, logger), IImageDeblurrer
    {
        public override string Name => "RC-Astro BlurXTerminator (bxt, full-image)";

        protected override string ProductKey => "bxt";

        // Full-image deconvolution: auto PSF (--ansr defaults true) + BOTH stellar
        // (--ss) and non-stellar (--sn) sharpening, so stars are tightened before
        // star removal. (Contrast RcAstroNonStellarDeconvolver, which runs bxt on
        // the starless plate with ss=0.)
        protected override IReadOnlyList<string> BuildArgs(Image input) =>
        [
            "--ss", sharpenStars.ToString("0.00", CultureInfo.InvariantCulture),
            "--sn", sharpenNonStellar.ToString("0.00", CultureInfo.InvariantCulture),
        ];
    }

    /// <summary>
    /// No-op <see cref="IImageDeblurrer"/>: returns the input unchanged. The
    /// DeferredDeblurrer fallback for an installed-but-unlicensed bxt -- the
    /// SharpenPipeline detects the identity return and skips the deblur step
    /// rather than failing the whole enhance.
    /// </summary>
    internal sealed class PassthroughDeblurrer : IImageDeblurrer
    {
        public string Name => "RC-Astro BlurXTerminator (unavailable; passthrough)";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }
}
