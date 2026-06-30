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
        // do NOT pass --stars: that would also emit a separate stars-only file,
        // which the pipeline computes itself.
        protected override IReadOnlyList<string> BuildArgs(Image input, EnhanceTuning? tuning) => [];
    }
}
