using TianWen.Lib.Imaging;

namespace TianWen.Lib.Imaging.Enhancement
{
    /// <summary>
    /// Approach-A fixed dual-stretch for the per-plate (stars / starless) export
    /// used by <c>stack --split-plates</c>. It reuses the canonical
    /// <see cref="Image"/> stretch math AND the <see cref="SharpenStep"/> record
    /// defaults as the single sources of truth, so the stack's split TIFFs match
    /// what <c>image sharpen --dual-stretch</c> produces at default settings -- no
    /// duplicated constants, no duplicated math.
    ///
    /// <para>The knob-driven, mode-rich variant (GHS / asinh / per-flag amounts)
    /// stays in the <see cref="SharpenPipeline"/> step program that
    /// <c>image sharpen</c> builds. This composite is only the fixed-default tail
    /// for the no-knobs stack path; if/when the stack grows full stretch knobs
    /// (approach B), both paths should converge on a shared step-list builder.</para>
    ///
    /// <para>Both methods leave the input plate untouched (the caller owns it) and
    /// return a freshly-allocated stretched plate the caller must
    /// <see cref="Image.Release"/>; any internal intermediates are released here.</para>
    /// </summary>
    internal static class DualStretchPlates
    {
        /// <summary>
        /// Frank-style fixed-curve StarStretch on the stars-only plate. In the
        /// BlurX-first flow the plate has already been SCNR'd upstream (the
        /// <c>ScnrStarsStep</c> in <see cref="SharpenRequest.DeblurFirst"/>), so no
        /// SCNR is re-applied here -- just the stretch. Amount mirrors
        /// <see cref="StretchStarsStep"/>'s default.
        /// </summary>
        public static Image StretchStars(Image starsOnly)
            => starsOnly.StarStretch(new StretchStarsStep().Amount);

        /// <summary>
        /// SAS-Pro statistical-stretch shape on the starless plate: MTF auto-stretch
        /// to a target median, then pull the background down, then soft-compress the
        /// highlights. Parameters mirror <see cref="StretchStarlessStep"/>,
        /// <see cref="BackgroundReduceStep"/> and <see cref="CompressHighlightsStep"/>
        /// defaults; the background peak is auto-derived from the (already cropped)
        /// plate histogram, matching the pipeline executor.
        /// </summary>
        public static Image StretchStarless(Image starless)
        {
            var mtf = starless.MtfStretch(new StretchStarlessStep().TargetMedian, out _, out _);

            var reduced = mtf.ReduceBackground(mtf.EstimateBackgroundPeak(), new BackgroundReduceStep().Compression);
            if (!ReferenceEquals(reduced, mtf)) mtf.Release();

            var hi = new CompressHighlightsStep();
            var compressed = reduced.CompressHighlights(hi.Knee, hi.Amount);
            if (!ReferenceEquals(compressed, reduced)) reduced.Release();

            return compressed;
        }
    }
}
