using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.AI.Imaging.RcAstro
{
    /// <summary>
    /// <see cref="IStarRemover"/> backed by RC-Astro StarXTerminator (sxt),
    /// driven through the CLI. The product's default <c>-o</c> output IS the
    /// starless plate -- exactly the <see cref="IStarRemover"/> contract -- so
    /// the <c>SharpenPipeline</c> derives the stars-only plate itself
    /// (<c>StarsOnly = Source - Starless</c>).
    /// </summary>
    public sealed class RcAstroStarRemover(IRcAstroCli cli, ILogger<RcAstroStarRemover>? logger = null)
        : RcAstroEnhancerBase(cli, logger), IStarRemover
    {
        public override string Name => "RC-Astro StarXTerminator (sxt)";

        protected override string ProductKey => "sxt";

        // sxt removes stars on its defaults (tile overlap 0.2). We intentionally
        // do NOT pass --stars: per `sxt --help` that output is defined as
        // "original minus starless" -- exactly the pipeline's own additive split
        // (StarsOnly = max(Source - Starless, 0); both sides clamp negatives),
        // and `--stars --unscreen` is likewise the pipeline's Screen split
        // (Image.Unscreen). Emitting it would be a duplicate plate plus a file
        // round-trip, and the split-mode choice must live in the pipeline where
        // RecombineStep.Mode has to match it. sxt has NO star-mask output --
        // derive a mask from the stars plate instead (Binarize + GaussianBlur).
        protected override IReadOnlyList<string> BuildArgs(Image input, EnhanceTuning? tuning) => [];
    }
}
