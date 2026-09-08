using System;
using System.Buffers;
using System.Drawing;
using TianWen.Lib.Stat;

namespace TianWen.Lib.Imaging
{
    /// <summary>Which side of a rectangle a walk comes in from.</summary>
    public enum CoverageEdge
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    /// <summary>
    /// Knobs for <see cref="CoverageEdgeWalk"/>. Every default was measured rather than picked --
    /// see the class remarks for the corpus and the numbers.
    /// </summary>
    /// <remarks>
    /// A record CLASS, not a record struct: property initialisers on a record struct are skipped by
    /// <c>new()</c> and <c>default</c>, so a struct here would hand a caller a zero band thickness and
    /// a zero margin, which trims nothing and reads as the feature being absent.
    /// </remarks>
    public sealed record CoverageEdgeWalkOptions
    {
        public static CoverageEdgeWalkOptions Default { get; } = new CoverageEdgeWalkOptions();

        /// <summary>Band depth, in px, over which one profile sample is measured.</summary>
        public int BandThickness { get; init; } = 16;

        /// <summary>Tile length along the band. One sigma per tile, so a star, a trail or a nebula
        /// edge crossing part of a band moves its own tiles and not the answer.</summary>
        public int TileLength { get; init; } = 64;

        /// <summary>Profile sampling interval, in px, and therefore the resolution of the trim.</summary>
        public int Step { get; init; } = 4;

        /// <summary>How far in the settled reference is taken from, as a fraction of the perpendicular
        /// span. Samples between half this and this are pooled, so the reference is "just inside" --
        /// the same rows or columns as the band, which is what cancels a frame-wide noise gradient.</summary>
        public double ReferenceFraction { get; init; } = 0.15;

        /// <summary>The most one edge may lose, as a fraction of the perpendicular span. Also the depth
        /// by which the profile has to have settled, so a frame whose noise keeps falling past here is
        /// declined rather than trimmed (see the class remarks).</summary>
        public double MaxTrimFraction { get; init; } = 0.05;

        /// <summary>How close to the settled level counts as settled.</summary>
        public double SettleMargin { get; init; } = 1.15;

        /// <summary>How much noisier than the settled level the outermost band must be before anything
        /// is trimmed at all. Below this the edge is indistinguishable from the interior and the walk
        /// reports nothing to do.</summary>
        public double MinimumRise { get; init; } = 1.15;

        /// <summary>Which percentile of the per-tile sigmas is the band's noise. The quietest tiles
        /// carry the least structure; p10 was the one that converged to 1.00x deep inside a real master
        /// where the median stayed at 1.25x.</summary>
        public int Percentile { get; init; } = 10;
    }

    /// <summary>What the walk found on one edge.</summary>
    /// <param name="Depth">Px to discard from that edge. Zero both when there was nothing to trim and
    /// when the walk declined -- <paramref name="Settled"/> is what separates those.</param>
    /// <param name="Settled">Whether the profile reached the settled level inside
    /// <see cref="CoverageEdgeWalkOptions.MaxTrimFraction"/>. False is a REFUSAL: the noise is still
    /// falling that far in, which is a property of the frame and not a border.</param>
    /// <param name="EdgeRatio">The outermost band's noise over the settled level, so a caller can say
    /// how bad the edge was, or that it was fine.</param>
    public readonly record struct CoverageEdgeTrim(int Depth, bool Settled, double EdgeRatio);

    /// <summary>The four edges' verdicts, and the rectangle they leave.</summary>
    public readonly record struct CoverageEdgeTrims(
        CoverageEdgeTrim Left,
        CoverageEdgeTrim Top,
        CoverageEdgeTrim Right,
        CoverageEdgeTrim Bottom)
    {
        /// <summary>True when at least one edge refused to answer.</summary>
        public bool AnyDeclined => !Left.Settled || !Top.Settled || !Right.Settled || !Bottom.Settled;

        /// <summary>Total px discarded across all four edges.</summary>
        public int TotalDepth => Left.Depth + Top.Depth + Right.Depth + Bottom.Depth;

        /// <summary>Applies the trims to the rectangle they were measured on.</summary>
        public Rectangle Apply(Rectangle rect)
        {
            var width = rect.Width - Left.Depth - Right.Depth;
            var height = rect.Height - Top.Depth - Bottom.Depth;
            return width > 0 && height > 0
                ? new Rectangle(rect.X + Left.Depth, rect.Y + Top.Depth, width, height)
                : rect;
        }
    }

    /// <summary>
    /// Walks in from each edge of a rectangle until the local noise settles, and reports how much of
    /// each edge is under-exposed rather than merely present. This is the estimate for a frame we did
    /// NOT produce; a master of ours carries its own coverage plane, and
    /// <see cref="Image.LargestCoveredRectangle(Image, double)"/> is the exact answer, which wins
    /// wherever it exists.
    /// </summary>
    /// <remarks>
    /// <para><b>The problem it addresses.</b> <see cref="Image.LargestCoveredRectangle()"/> finds where
    /// NO sub reached, so it answers "the largest rectangle inside the area at least one sub covered".
    /// A dithered stack has a further band inside that, with no zeros in it and real data, that fewer
    /// subs reached: partial coverage is not darker (row medians hold at 0.997 of the interior all the
    /// way to the edge), so only the noise gives it away.</para>
    /// <para><b>The reference is the profile's OWN level just inside, never the frame interior.</b>
    /// Measured on TianWen's own 10P Bayer-drizzle master, whose <c>.rejection.fits</c> sidecar is the
    /// accumulated per-pixel weight and therefore ground truth: the vertical-difference noise 24 px in
    /// from its left edge is 1.8x to 2.5x the centre's AT FULL COVERAGE -- every tile length, every
    /// percentile -- and it decays over about 460 px. A canvas edge is fed by fewer distinct dither
    /// phases, so its noise is less correlated and a difference-based sigma reads high there. Against
    /// the frame interior that reads as a 312 to 460 px band; the weight map says 4.</para>
    /// <para><b>A band whose end cannot be seen is not trimmed.</b> The rule is the SHALLOWEST depth
    /// from which every sample out to <see cref="CoverageEdgeWalkOptions.MaxTrimFraction"/> is within
    /// <see cref="CoverageEdgeWalkOptions.SettleMargin"/> of the settled level, so a profile still
    /// falling at that depth yields <see cref="CoverageEdgeTrim.Settled"/> false and no trim -- which is
    /// what saves the case above. Level matching against the interior, plateau matching against the
    /// edge's own deep median, and a knee/slope rule were all tried first, and all three trimmed 300+
    /// px of that master's left edge where the truth was 4.</para>
    /// <para><b>It is an estimate, and on a drizzled frame it can be blind.</b> Partial coverage pushes
    /// a difference-based sigma two ways at once -- fewer samples raise it, fewer drops per cell
    /// correlate the neighbours and lower it -- and on the same master's right edge those cancelled to
    /// 1.08x at 69% coverage. Use the coverage plane when there is one.</para>
    /// <para>Measured trims on the three real Astro Pixel Processor composites in the corpus, which
    /// carry no coverage plane: top 84 to 116 px, bottom 68 to 100, left 36 to 60, right 8 to 40,
    /// stable to about 20 px across margins 1.10 to 1.20.</para>
    /// <para>Cost is one pass over four bands per sample: ~5.5 s in the Python harness the constants
    /// were measured with (<c>tools/coverage-edge-walk/</c>), and it reads only the bands, never the
    /// whole frame. Like <see cref="Image.LargestCoveredRectangle()"/> it belongs behind an action.</para>
    /// </remarks>
    public static class CoverageEdgeWalk
    {
        /// <summary>The rectangle left once every edge's under-exposed band is discarded.</summary>
        public static Rectangle Trim(Image image, Rectangle start, CoverageEdgeWalkOptions? options = null)
            => Measure(image, start, options).Apply(start);

        /// <summary>Per-edge verdicts, so a caller can report what happened as well as apply it.</summary>
        public static CoverageEdgeTrims Measure(Image image, Rectangle start, CoverageEdgeWalkOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            var o = options ?? CoverageEdgeWalkOptions.Default;
            var rect = Rectangle.Intersect(start, new Rectangle(0, 0, image.Width, image.Height));
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return new CoverageEdgeTrims(Nothing, Nothing, Nothing, Nothing);
            }

            return new CoverageEdgeTrims(
                MeasureEdge(image, rect, CoverageEdge.Left, o),
                MeasureEdge(image, rect, CoverageEdge.Top, o),
                MeasureEdge(image, rect, CoverageEdge.Right, o),
                MeasureEdge(image, rect, CoverageEdge.Bottom, o));
        }

        private static CoverageEdgeTrim Nothing => new CoverageEdgeTrim(0, Settled: true, EdgeRatio: 1.0);

        /// <summary>One edge, internal so the tests can pin the refusal and the trim separately.</summary>
        internal static CoverageEdgeTrim MeasureEdge(Image image, Rectangle rect, CoverageEdge edge, CoverageEdgeWalkOptions o)
        {
            var horizontal = edge is CoverageEdge.Top or CoverageEdge.Bottom;
            var span = horizontal ? rect.Height : rect.Width;
            var along = horizontal ? rect.Width : rect.Height;

            var reference = (int)(o.ReferenceFraction * span);
            var maxTrim = (int)(o.MaxTrimFraction * span) / o.Step * o.Step;

            // Too small to say anything: a band needs three tiles before it has a percentile at all, and
            // the reference has to sit beyond the trim window or it would be measured inside the band.
            if (along < 3 * o.TileLength || maxTrim < 2 * o.Step || reference < 2 * maxTrim + o.BandThickness
                || reference + o.BandThickness >= span)
            {
                return Nothing;
            }

            var count = maxTrim / o.Step + 1;
            var profile = new double[count];
            for (var i = 0; i < count; i++)
            {
                profile[i] = BandNoise(image, rect, edge, i * o.Step, o);
            }
            SmoothInPlace(profile);

            var settled = SettledLevel(image, rect, edge, reference, o);
            if (!double.IsFinite(settled) || settled <= 0 || !double.IsFinite(profile[0]))
            {
                return Nothing;
            }

            var edgeRatio = profile[0] / settled;
            if (edgeRatio < o.MinimumRise)
            {
                // The outermost band is already as quiet as just inside. Nothing to remove -- and saying
                // so is not the same as declining, because the walk did answer.
                return new CoverageEdgeTrim(0, Settled: true, edgeRatio);
            }

            // Outside-in: the trim is the shallowest depth from which everything out to maxTrim is
            // settled. Read this way round because the profile is not monotone -- at very low coverage a
            // drizzle cell is fed by few drops, so its neighbours correlate and the sigma dips, which
            // puts a false floor in the middle of the ramp for a first-crossing rule to stop at.
            var first = -1;
            for (var i = count - 1; i >= 0; i--)
            {
                if (double.IsFinite(profile[i]) && profile[i] > o.SettleMargin * settled)
                {
                    break;
                }
                first = i;
            }

            // first < 0: nothing settled by maxTrim, so the noise is still coming down that far in. That
            // is the frame's own structure, not a border; refuse rather than eat MaxTrimFraction of it.
            return first switch
            {
                < 0 => new CoverageEdgeTrim(0, Settled: false, edgeRatio),
                0 => new CoverageEdgeTrim(0, Settled: true, edgeRatio),
                _ => new CoverageEdgeTrim(first * o.Step, Settled: true, edgeRatio),
            };
        }

        /// <summary>
        /// The level the edge's noise settles to: the median of samples between half
        /// <see cref="CoverageEdgeWalkOptions.ReferenceFraction"/> and all of it, sparsely sampled
        /// because it is a level and not a profile.
        /// </summary>
        private static double SettledLevel(Image image, Rectangle rect, CoverageEdge edge, int reference, CoverageEdgeWalkOptions o)
        {
            var stride = Math.Max(o.Step, 32);
            var from = reference / 2;
            var samples = new double[Math.Max(1, (reference - from + stride - 1) / stride)];
            var count = 0;
            for (var depth = from; depth < reference && count < samples.Length; depth += stride)
            {
                var value = BandNoise(image, rect, edge, depth, o);
                if (double.IsFinite(value))
                {
                    samples[count++] = value;
                }
            }
            if (count == 0)
            {
                return double.NaN;
            }
            Array.Sort(samples, 0, count);
            return samples[count / 2];
        }

        /// <summary>Median-of-three, in place: one loud band should not decide where the border ends.</summary>
        private static void SmoothInPlace(double[] profile)
        {
            if (profile.Length < 3)
            {
                return;
            }
            var previous = profile[0];
            for (var i = 1; i < profile.Length - 1; i++)
            {
                var a = previous;
                var b = profile[i];
                var c = profile[i + 1];
                previous = b;
                profile[i] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
            }
        }

        /// <summary>
        /// The noisiest channel's percentile of per-tile sigmas for the band at <paramref name="depth"/>.
        /// The worst channel decides: a band under-exposed in one channel of three is under-exposed.
        /// </summary>
        internal static double BandNoise(Image image, Rectangle rect, CoverageEdge edge, int depth, CoverageEdgeWalkOptions o)
        {
            var horizontal = edge is CoverageEdge.Top or CoverageEdge.Bottom;
            var band = BandRect(rect, edge, depth, o.BandThickness);
            if (band.Width <= 0 || band.Height <= 0
                || band.X < 0 || band.Y < 0 || band.Right > image.Width || band.Bottom > image.Height)
            {
                return double.NaN;
            }

            var worst = double.NaN;
            for (var c = 0; c < image.ChannelCount; c++)
            {
                var value = ChannelBandNoise(image.GetChannelSpan(c), image.Width, band, horizontal, o);
                if (double.IsFinite(value) && (!double.IsFinite(worst) || value > worst))
                {
                    worst = value;
                }
            }
            return worst;
        }

        private static Rectangle BandRect(Rectangle rect, CoverageEdge edge, int depth, int thickness) => edge switch
        {
            CoverageEdge.Top => new Rectangle(rect.X, rect.Y + depth, rect.Width, thickness),
            CoverageEdge.Bottom => new Rectangle(rect.X, rect.Bottom - depth - thickness, rect.Width, thickness),
            CoverageEdge.Left => new Rectangle(rect.X + depth, rect.Y, thickness, rect.Height),
            _ => new Rectangle(rect.Right - depth - thickness, rect.Y, thickness, rect.Height),
        };

        /// <summary>
        /// Per-tile sigma from the MAD of adjacent differences ALONG the band, then the requested
        /// percentile over the tiles. Differencing along the band kills any gradient across it, and
        /// across is the direction a coverage ramp runs, so the statistic answers about noise and never
        /// about level.
        /// </summary>
        private static double ChannelBandNoise(
            ReadOnlySpan<float> plane, int imageWidth, Rectangle band, bool horizontal, CoverageEdgeWalkOptions o)
        {
            var along = horizontal ? band.Width : band.Height;
            var across = horizontal ? band.Height : band.Width;
            var tiles = along / o.TileLength;
            if (tiles < 3 || across < 1)
            {
                return double.NaN;
            }

            var sigmas = ArrayPool<float>.Shared.Rent(tiles);
            var diffs = ArrayPool<float>.Shared.Rent(across * (o.TileLength - 1));
            try
            {
                var kept = 0;
                for (var t = 0; t < tiles; t++)
                {
                    var start = t * o.TileLength;
                    var n = 0;
                    for (var a = 0; a < across; a++)
                    {
                        for (var i = 0; i < o.TileLength - 1; i++)
                        {
                            int from;
                            int to;
                            if (horizontal)
                            {
                                from = (band.Y + a) * imageWidth + band.X + start + i;
                                to = from + 1;
                            }
                            else
                            {
                                from = (band.Y + start + i) * imageWidth + band.X + a;
                                to = from + imageWidth;
                            }
                            var d = plane[to] - plane[from];
                            if (float.IsFinite(d))
                            {
                                diffs[n++] = d;
                            }
                        }
                    }
                    if (n < 32)
                    {
                        continue;
                    }
                    var (_, mad) = StatisticsHelper.MedianAndMad(diffs.AsSpan(0, n));
                    // The difference of two independent samples carries sqrt(2) their sigma, and 1.4826
                    // takes a MAD to a Gaussian sigma.
                    sigmas[kept++] = (float)(1.4826 * mad / Math.Sqrt(2.0));
                }

                return kept < 3
                    ? double.NaN
                    : StatisticsHelper.PercentileFast(sigmas.AsSpan(0, kept), o.Percentile / 100.0);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(diffs);
                ArrayPool<float>.Shared.Return(sigmas);
            }
        }
    }
}
