using System;
using System.Buffers;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging
{
    /// <summary>
    /// Whether an operation preserved a frame's colour, and whether it preserved an extended object.
    ///
    /// <para><b>These exist because the obvious statistic is confounded in this domain, twice
    /// over.</b> A MAD across an interleaved CFA mosaic is dominated by the photosite LEVELS -- R, G
    /// and B differ by more than the noise does -- not by noise, so a whole-mosaic sigma cannot
    /// judge colour at all; that artifact is what produced the retracted "sxt adds 11% noise" claim
    /// in <c>docs/plans/comet-integration.md</c>. And a threshold of the form <c>median + k*MAD</c>
    /// MOVES when a denoiser runs (lower MAD, lower cut, more pixels admitted), so counting flux
    /// above one measures the denoiser rather than the object.</para>
    ///
    /// <para>Both measurements are deliberately shape-agnostic: a raw CFA mosaic reduces by
    /// photosite and an already-debayered plate by channel, so a before/after pair that straddles a
    /// debayer is still directly comparable. That is the comparison anyone auditing an OSC
    /// processing step actually needs.</para>
    ///
    /// <para>Scratch is rented from <see cref="ArrayPool{T}"/> and the maths stays in
    /// <see cref="float"/>, the native pixel type: a median needs a mutable copy (quickselect
    /// partitions in place) but nothing here needs a per-pixel widening to <c>double</c>, which on a
    /// 3008-square frame is 72 MB of garbage per plane for no added precision in a statistic that is
    /// an ORDER statistic. Only the small fixed-size results are <c>double</c>.</para>
    /// </summary>
    public static class CfaColourMetrics
    {
        /// <param name="Levels">Background level per colour, always three entries: R, G, B.</param>
        /// <param name="SeparationPercent">
        /// <c>(max - min) / mean</c> of <paramref name="Levels"/> as a percentage, and THE colour
        /// witness: these are plane MEDIANS, not noise, so no colour-correct operation may move
        /// them. A spatial kernel applied to a mosaic blends neighbouring photosites and drives this
        /// toward zero -- after which debayering cannot bring the colour back, because it is already
        /// gone from the linear data.
        /// </param>
        /// <param name="GreenSplit">
        /// Median <c>|G1 - G2|</c> for a mosaic; <see cref="double.NaN"/> for a debayered plate.
        /// CORROBORATING ONLY -- G1 and G2 sit under the same filter, so their difference is largely
        /// noise and a legitimately-working denoiser reduces it too. Never read a fall in this as
        /// damage on its own; that is what <paramref name="SeparationPercent"/> is for.
        /// </param>
        public readonly record struct ColourReading(
            (double R, double G, double B) Levels, double SeparationPercent, double GreenSplit);

        /// <summary>
        /// Background colour levels, reduced to comparable R/G/B whatever the input shape: a CFA
        /// mosaic by photosite (G being the mean of the two greens, which is what any debayer
        /// converges to on a flat background), an already-debayered plate by channel.
        /// </summary>
        public static ColourReading MeasureColour(Image image)
        {
            ArgumentNullException.ThrowIfNull(image);

            return image.ChannelCount >= 3 ? MeasureRgb(image) : MeasureMosaic(image);
        }

        private static ColourReading MeasureRgb(Image image)
        {
            var n = image.Width * image.Height;
            var scratch = ArrayPool<float>.Shared.Rent(n);
            try
            {
                var span = scratch.AsSpan(0, n);   // EXACT length: pool arrays over-allocate, and
                                                   // the slack would otherwise be median input.
                var levels = (0.0, 0.0, 0.0);
                image.GetChannelSpan(0).CopyTo(span);
                levels.Item1 = StatisticsHelper.MedianFast(span);
                image.GetChannelSpan(1).CopyTo(span);
                levels.Item2 = StatisticsHelper.MedianFast(span);
                image.GetChannelSpan(2).CopyTo(span);
                levels.Item3 = StatisticsHelper.MedianFast(span);
                return Reading(levels, double.NaN);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        private static ColourReading MeasureMosaic(Image image)
        {
            var hw = image.Width / 2;
            var hh = image.Height / 2;
            var quarter = hw * hh;
            if (quarter == 0) return Reading((0, 0, 0), double.NaN);

            // One rental, four slices: the medians are taken one at a time, but |G1-G2| needs both
            // greens still intact, so the two green planes cannot share a buffer.
            var scratch = ArrayPool<float>.Shared.Rent(quarter * 3);
            try
            {
                var r = scratch.AsSpan(0, quarter);
                var g1 = scratch.AsSpan(quarter, quarter);
                var g2 = scratch.AsSpan(quarter * 2, quarter);
                GatherPhotosites(image, r, g1, g2, out var blueMedian);

                // |G1-G2| BEFORE the medians, which partition their inputs in place.
                for (var i = 0; i < quarter; i++) r[i] = Math.Abs(g1[i] - g2[i]);
                var greenSplit = StatisticsHelper.MedianFast(r);

                var green = (StatisticsHelper.MedianFast(g1) + StatisticsHelper.MedianFast(g2)) / 2.0;

                // r was consumed by the green split, so re-gather just the red plane.
                GatherPlane(image, r, xOdd: false, yOdd: false);
                return Reading((StatisticsHelper.MedianFast(r), green, blueMedian), greenSplit);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        /// <summary>
        /// Fills the two green planes, and folds the blue plane's median in on the way so blue never
        /// needs a buffer of its own.
        /// </summary>
        private static void GatherPhotosites(
            Image image, Span<float> blueScratch, Span<float> g1, Span<float> g2, out double blueMedian)
        {
            GatherPlane(image, g1, xOdd: true, yOdd: false);
            GatherPlane(image, g2, xOdd: false, yOdd: true);
            GatherPlane(image, blueScratch, xOdd: true, yOdd: true);
            blueMedian = StatisticsHelper.MedianFast(blueScratch);
        }

        /// <summary>
        /// One photosite phase of a CFA mosaic. Phases are positional, NOT colour names: the
        /// SensorType covers every Bayer pattern (GRBG / GBRG / BGGR carry their rotation in
        /// BayerOffsetX/Y), so a rotated sensor permutes which colour lands in which slot without
        /// changing the SEPARATION a caller reads off the result.
        /// </summary>
        private static void GatherPlane(Image image, Span<float> destination, bool xOdd, bool yOdd)
        {
            var w = image.Width;
            var hw = w / 2;
            var hh = image.Height / 2;
            var src = image.GetChannelSpan(0);
            var dx = xOdd ? 1 : 0;
            var dy = yOdd ? 1 : 0;
            for (var y = 0; y < hh; y++)
            {
                var row = src.Slice((2 * y + dy) * w, w);
                var outRow = destination.Slice(y * hw, hw);
                for (var x = 0; x < hw; x++)
                {
                    outRow[x] = row[2 * x + dx];
                }
            }
        }

        private static ColourReading Reading((double R, double G, double B) levels, double greenSplit)
        {
            var mean = (levels.R + levels.G + levels.B) / 3.0;
            var max = Math.Max(levels.R, Math.Max(levels.G, levels.B));
            var min = Math.Min(levels.R, Math.Min(levels.G, levels.B));
            var separation = mean == 0 ? 0 : (max - min) / mean * 100.0;
            return new ColourReading(levels, separation, greenSplit);
        }

        /// <summary>
        /// Contrast of a FIXED region above the frame's own background,
        /// <c>(boxMean - median) / median</c>.
        ///
        /// <para>Fixed and scale-invariant on purpose. Choose the region ONCE on a reference frame
        /// (<see cref="FindBrightestBox"/>) and reuse it: expressed in FRACTIONAL coordinates it
        /// names the same piece of sky on a half-resolution mosaic and a full-resolution RGB plate
        /// alike, and dividing by the frame's own median removes both the ADU-vs-[0,1] unit
        /// difference and any overall level shift a gradient step introduced. Nothing about the
        /// frame under test can move the region, which is the entire point -- a threshold-derived
        /// measure moves under a denoiser and then reports on the denoiser.</para>
        /// </summary>
        /// <param name="image">Frame to measure.</param>
        /// <param name="fracX">Left edge of the box, as a fraction of width.</param>
        /// <param name="fracY">Top edge of the box, as a fraction of height.</param>
        /// <param name="fracSize">Box side, as a fraction of width.</param>
        public static double RegionContrast(Image image, double fracX, double fracY, double fracSize)
        {
            ArgumentNullException.ThrowIfNull(image);

            var (w, h) = LumaExtent(image);
            if (w == 0 || h == 0) return 0;

            var box = Math.Max(1, (int)(fracSize * w));
            var x0 = Math.Clamp((int)(fracX * w), 0, w - 1);
            var y0 = Math.Clamp((int)(fracY * h), 0, h - 1);

            // The box mean reads straight from the source spans -- only the background MEDIAN needs
            // a materialised plane, because an order statistic cannot be streamed.
            var sum = 0.0;
            var n = 0;
            for (var y = y0; y < Math.Min(h, y0 + box); y++)
            {
                for (var x = x0; x < Math.Min(w, x0 + box); x++) { sum += LumaAt(image, x, y); n++; }
            }
            var boxMean = n > 0 ? sum / n : 0.0;

            var total = w * h;
            var scratch = ArrayPool<float>.Shared.Rent(total);
            try
            {
                var span = scratch.AsSpan(0, total);
                FillLuma(image, span, w, h);
                var background = StatisticsHelper.MedianFast(span);
                return background == 0 ? 0 : (boxMean - background) / background;
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        /// <summary>
        /// Top-left of the brightest box, in FRACTIONAL coordinates. Call once on the untouched
        /// reference and reuse the answer for every run -- re-deriving it per frame lets each frame
        /// pick its own favourite region, and then the comparison is between different pieces of sky.
        /// </summary>
        /// <param name="image">Reference frame, ideally untouched.</param>
        /// <param name="fracSize">Box side, as a fraction of width.</param>
        public static (double X, double Y) FindBrightestBox(Image image, double fracSize)
        {
            ArgumentNullException.ThrowIfNull(image);

            var (w, h) = LumaExtent(image);
            if (w == 0 || h == 0) return (0, 0);

            var box = Math.Max(1, (int)(fracSize * w));
            var step = Math.Max(1, box / 2);
            var total = w * h;
            var scratch = ArrayPool<float>.Shared.Rent(total);
            try
            {
                var luma = scratch.AsSpan(0, total);
                FillLuma(image, luma, w, h);

                // Row-prefix sums so each candidate box costs its HEIGHT, not its area: the boxes
                // overlap by half a side in both axes, so the naive form re-reads every pixel ~4x.
                var best = double.NegativeInfinity;
                var bestX = 0;
                var bestY = 0;
                for (var y = 0; y + box <= h; y += step)
                {
                    for (var x = 0; x + box <= w; x += step)
                    {
                        var sum = 0.0;
                        for (var yy = y; yy < y + box; yy++)
                        {
                            var row = luma.Slice(yy * w + x, box);
                            for (var i = 0; i < row.Length; i++) sum += row[i];
                        }
                        if (sum > best) { best = sum; bestX = x; bestY = y; }
                    }
                }
                return ((double)bestX / w, (double)bestY / h);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        /// <summary>
        /// Luma is per-photosite for a mosaic -- a half-resolution average with NO interpolation, so
        /// the CFA pattern cannot leak into the measurement -- and the channel mean for RGB.
        /// </summary>
        private static (int Width, int Height) LumaExtent(Image image)
            => image.ChannelCount >= 3
                ? (image.Width, image.Height)
                : (image.Width / 2, image.Height / 2);

        private static float LumaAt(Image image, int x, int y)
        {
            var w = image.Width;
            if (image.ChannelCount >= 3)
            {
                var i = y * w + x;
                return (image.GetChannelSpan(0)[i] + image.GetChannelSpan(1)[i] + image.GetChannelSpan(2)[i]) / 3f;
            }
            var src = image.GetChannelSpan(0);
            var top = 2 * y * w + 2 * x;
            var bottom = (2 * y + 1) * w + 2 * x;
            return (src[top] + src[top + 1] + src[bottom] + src[bottom + 1]) / 4f;
        }

        private static void FillLuma(Image image, Span<float> destination, int w, int h)
        {
            if (image.ChannelCount >= 3)
            {
                var c0 = image.GetChannelSpan(0);
                var c1 = image.GetChannelSpan(1);
                var c2 = image.GetChannelSpan(2);
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (c0[i] + c1[i] + c2[i]) / 3f;
                }
                return;
            }

            var src = image.GetChannelSpan(0);
            var fullW = image.Width;
            for (var y = 0; y < h; y++)
            {
                var top = src.Slice(2 * y * fullW, fullW);
                var bottom = src.Slice((2 * y + 1) * fullW, fullW);
                var outRow = destination.Slice(y * w, w);
                for (var x = 0; x < w; x++)
                {
                    outRow[x] = (top[2 * x] + top[2 * x + 1] + bottom[2 * x] + bottom[2 * x + 1]) / 4f;
                }
            }
        }
    }
}
