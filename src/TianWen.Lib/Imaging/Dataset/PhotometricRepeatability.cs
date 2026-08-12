using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging.Dataset
{
    /// <summary>
    /// How much the SAME stars move and change brightness between two independent realisations of one
    /// field. This is the reference the AI enhancers' photometric gates are calibrated against: the
    /// plan requires an aperture-flux delta and a centroid shift below a stated threshold, and the
    /// threshold has to come from what the CLASSICAL pipeline already does to the same stars, not from
    /// a number somebody liked. A gate tighter than the pipeline's own scatter blocks good models; a
    /// gate looser than it certifies nothing.
    ///
    /// <para><b>Compare two SUBS, not two masters.</b> The gate is applied to held-out subs, so the
    /// reference has to be sub-to-sub. It is also the only pairing available without re-processing:
    /// the dataset build retains one integrated master per session and tiles the half-master pair
    /// rather than keeping it whole, and tiles are input-stretched, so they carry no photometry at
    /// all.</para>
    ///
    /// <para><b>Banded by SNR, and that is not decoration.</b> Centroid uncertainty scales roughly as
    /// FWHM / (2.355 * SNR) and flux uncertainty likewise, so an unbanded aggregate reports the
    /// faint tail's scatter and the number moves with how many faint stars each frame happened to
    /// yield. That is the same failure the field-radius profile had when it was unbanded, where
    /// vignetting made corner stars fainter and the profile inverted. Bands make the threshold
    /// statable as a function of the SNR the gate will actually see.</para>
    ///
    /// <para>Pure and allocation-modest: no I/O, no image access, just two star lists. The caller
    /// decides which frames to compare.</para>
    /// </summary>
    public static class PhotometricRepeatability
    {
        /// <summary>SNR band edges, open-ended at the top. Chosen to straddle the detection floor
        /// (snrMin is 5 throughout this codebase) and to isolate the bright end, where a gate has no
        /// noise excuse and any flux change is the model's doing.</summary>
        public static readonly ImmutableArray<float> DefaultSnrEdges = [5f, 10f, 20f, 50f, 100f, float.PositiveInfinity];

        /// <summary>Below this many matched stars the percentiles are noise, so
        /// <see cref="Compare"/> returns <see langword="null"/> rather than a confident number. Same
        /// posture as <see cref="PsfProfileFit"/>'s star floor.</summary>
        public const int MinMatchedStars = 20;

        /// <summary>Stars used to estimate the global offset, brightest first. The estimate is a
        /// median so it does not need many, and an all-pairs search is quadratic, so this bounds the
        /// cost at a few hundred thousand distance tests instead of nine million.</summary>
        private const int OffsetEstimateStars = 200;

        /// <summary>One SNR band's scatter. Deltas are reported as ABSOLUTE fractional change, since a
        /// gate cares how far the flux moved and not which way; the signed median rides along
        /// separately as <paramref name="FluxBiasP50"/>, because a systematic offset means something
        /// different from scatter (a transparency or normalisation difference rather than noise).</summary>
        /// <param name="FluxDeltaP50">Median |flux_b / flux_a - 1|.</param>
        /// <param name="FluxDeltaP95">95th percentile of the same, which is the number a gate should
        /// use: a median hides a tail that a release gate exists to catch.</param>
        /// <param name="FluxBiasP50">Median SIGNED flux_b / flux_a - 1. Near zero for two subs of one
        /// session; a large value means the frames differ in throughput, not in noise.</param>
        /// <param name="CentroidShiftP50">Median residual centroid distance in px, after the global
        /// offset between the frames is removed.</param>
        /// <param name="CentroidShiftP95">95th percentile of the same.</param>
        public sealed record Band(
            float SnrLow,
            float SnrHigh,
            int Stars,
            float FluxDeltaP50,
            float FluxDeltaP95,
            float FluxBiasP50,
            float CentroidShiftP50,
            float CentroidShiftP95);

        /// <param name="OffsetX">Global x offset of frame B relative to A, in px. For two subs of one
        /// session this is the dither, which is why it MUST be removed before residual centroid
        /// scatter means anything: a 20 px dither would otherwise be reported as a 20 px centroid
        /// error.</param>
        /// <param name="Overall">Every matched star pooled. Present for completeness and for a single
        /// headline figure, but a threshold should be read off <paramref name="Bands"/>: see the type
        /// remarks for why an unbanded number tracks star population rather than the pipeline.</param>
        public sealed record Result(
            int MatchedStars,
            float OffsetX,
            float OffsetY,
            ImmutableArray<Band> Bands,
            Band Overall);

        /// <summary>
        /// Matches stars between two star lists of the same field and measures the scatter.
        /// </summary>
        /// <param name="a">Stars from the first frame.</param>
        /// <param name="b">Stars from the second frame.</param>
        /// <param name="matchTolerancePx">Residual radius, after the global offset is removed, inside
        /// which two detections are taken to be the same star. Keep it small: this is what stops a
        /// crowded field pairing a star with its neighbour, and a mismatch contributes a fabricated
        /// flux ratio.</param>
        /// <param name="coarseTolerancePx">Search radius for the initial offset estimate, so it must
        /// exceed the expected dither. Too small and no pairs are found at all; too large and the
        /// median is drawn from unrelated pairs, though the median is what makes that survivable.</param>
        /// <param name="snrEdges">Ascending band edges; defaults to <see cref="DefaultSnrEdges"/>.</param>
        /// <returns><see langword="null"/> when fewer than <see cref="MinMatchedStars"/> stars match,
        /// which is the honest answer for two frames that do not overlap or a field too sparse to
        /// measure.</returns>
        public static Result? Compare(
            ReadOnlySpan<ImagedStar> a,
            ReadOnlySpan<ImagedStar> b,
            float matchTolerancePx = 2f,
            float coarseTolerancePx = 40f,
            ImmutableArray<float>? snrEdges = null)
        {
            if (a.Length == 0 || b.Length == 0)
            {
                return null;
            }

            var edges = snrEdges ?? DefaultSnrEdges;
            if (edges.Length < 2)
            {
                throw new ArgumentException("At least two SNR band edges are required.", nameof(snrEdges));
            }

            if (!TryEstimateOffset(a, b, coarseTolerancePx, out var offsetX, out var offsetY))
            {
                return null;
            }

            var matches = MatchStars(a, b, offsetX, offsetY, matchTolerancePx);
            if (matches.Count < MinMatchedStars)
            {
                return null;
            }

            var bands = ImmutableArray.CreateBuilder<Band>(edges.Length - 1);
            for (var e = 0; e + 1 < edges.Length; e++)
            {
                var low = edges[e];
                var high = edges[e + 1];
                var inBand = new List<Sample>();
                foreach (var m in matches)
                {
                    if (m.Snr >= low && m.Snr < high)
                    {
                        inBand.Add(m);
                    }
                }
                bands.Add(Summarise(inBand, low, high));
            }

            return new Result(
                MatchedStars: matches.Count,
                OffsetX: offsetX,
                OffsetY: offsetY,
                Bands: bands.MoveToImmutable(),
                Overall: Summarise(matches, edges[0], edges[^1]));
        }

        private readonly record struct Sample(float FluxRatio, float CentroidShift, float Snr);

        /// <summary>
        /// Median offset over mutual nearest neighbours among the brightest stars. A median rather
        /// than a mean because at this stage some pairs are certainly wrong, and rather than a
        /// quad/triangle match because the two frames come from one session: the rotation between
        /// subs is negligible and a translation is all that has to be removed. A pairing that needs
        /// rotation or scale is a different problem and belongs in the registration path.
        /// </summary>
        private static bool TryEstimateOffset(
            ReadOnlySpan<ImagedStar> a,
            ReadOnlySpan<ImagedStar> b,
            float coarseTolerancePx,
            out float offsetX,
            out float offsetY)
        {
            offsetX = 0f;
            offsetY = 0f;

            var brightA = BrightestIndices(a, OffsetEstimateStars);
            var brightB = BrightestIndices(b, OffsetEstimateStars);

            var dxs = new List<float>();
            var dys = new List<float>();
            var tolSq = coarseTolerancePx * coarseTolerancePx;

            foreach (var ia in brightA)
            {
                var best = -1;
                var bestSq = float.MaxValue;
                foreach (var ib in brightB)
                {
                    var dx = b[ib].XCentroid - a[ia].XCentroid;
                    var dy = b[ib].YCentroid - a[ia].YCentroid;
                    var d2 = dx * dx + dy * dy;
                    if (d2 < bestSq)
                    {
                        bestSq = d2;
                        best = ib;
                    }
                }
                if (best >= 0 && bestSq <= tolSq)
                {
                    dxs.Add(b[best].XCentroid - a[ia].XCentroid);
                    dys.Add(b[best].YCentroid - a[ia].YCentroid);
                }
            }

            if (dxs.Count < 3)
            {
                return false;
            }

            offsetX = MedianOf(dxs);
            offsetY = MedianOf(dys);
            return true;
        }

        /// <summary>
        /// Mutual nearest neighbour inside the tolerance, so a bright star cannot claim several faint
        /// neighbours and inflate the match count with pairs that are not the same star.
        /// </summary>
        private static List<Sample> MatchStars(
            ReadOnlySpan<ImagedStar> a,
            ReadOnlySpan<ImagedStar> b,
            float offsetX,
            float offsetY,
            float matchTolerancePx)
        {
            var tolSq = matchTolerancePx * matchTolerancePx;
            var samples = new List<Sample>();

            for (var ia = 0; ia < a.Length; ia++)
            {
                var ax = a[ia].XCentroid + offsetX;
                var ay = a[ia].YCentroid + offsetY;

                var best = NearestWithin(b, ax, ay, tolSq);
                if (best < 0)
                {
                    continue;
                }

                // Mutual: the winner's own nearest, mapped back into A's frame, must be ia.
                var backX = b[best].XCentroid - offsetX;
                var backY = b[best].YCentroid - offsetY;
                if (NearestWithin(a, backX, backY, tolSq) != ia)
                {
                    continue;
                }

                var fa = a[ia].Flux;
                var fb = b[best].Flux;
                if (!(fa > 0f) || !(fb > 0f) || !float.IsFinite(fa) || !float.IsFinite(fb))
                {
                    continue;
                }

                var dx = b[best].XCentroid - ax;
                var dy = b[best].YCentroid - ay;

                samples.Add(new Sample(
                    FluxRatio: fb / fa,
                    CentroidShift: MathF.Sqrt(dx * dx + dy * dy),
                    // The LIMITING SNR of the pair: a star measured at 200 in one frame and 8 in the
                    // other is an 8-SNR measurement, and banding it as bright would blame the model
                    // for the faint frame's noise.
                    Snr: MathF.Min(a[ia].SNR, b[best].SNR)));
            }

            return samples;
        }

        private static int NearestWithin(ReadOnlySpan<ImagedStar> stars, float x, float y, float tolSq)
        {
            var best = -1;
            var bestSq = tolSq;
            for (var i = 0; i < stars.Length; i++)
            {
                var dx = stars[i].XCentroid - x;
                var dy = stars[i].YCentroid - y;
                var d2 = dx * dx + dy * dy;
                if (d2 <= bestSq)
                {
                    bestSq = d2;
                    best = i;
                }
            }
            return best;
        }

        private static List<int> BrightestIndices(ReadOnlySpan<ImagedStar> stars, int count)
        {
            var order = new List<int>(stars.Length);
            for (var i = 0; i < stars.Length; i++)
            {
                order.Add(i);
            }

            // Copy the keys out first: a comparison lambda cannot capture a span (CS8175).
            var flux = new float[stars.Length];
            for (var i = 0; i < stars.Length; i++)
            {
                flux[i] = stars[i].Flux;
            }

            order.Sort((l, r) => flux[r].CompareTo(flux[l]));
            if (order.Count > count)
            {
                order.RemoveRange(count, order.Count - count);
            }
            return order;
        }

        private static Band Summarise(List<Sample> samples, float low, float high)
        {
            if (samples.Count == 0)
            {
                return new Band(low, high, 0, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
            }

            var absDelta = new List<float>(samples.Count);
            var signed = new List<float>(samples.Count);
            var shift = new List<float>(samples.Count);
            foreach (var s in samples)
            {
                absDelta.Add(MathF.Abs(s.FluxRatio - 1f));
                signed.Add(s.FluxRatio - 1f);
                shift.Add(s.CentroidShift);
            }

            absDelta.Sort();
            signed.Sort();
            shift.Sort();

            return new Band(
                SnrLow: low,
                SnrHigh: high,
                Stars: samples.Count,
                FluxDeltaP50: Percentile(absDelta, 0.50),
                FluxDeltaP95: Percentile(absDelta, 0.95),
                FluxBiasP50: Percentile(signed, 0.50),
                CentroidShiftP50: Percentile(shift, 0.50),
                CentroidShiftP95: Percentile(shift, 0.95));
        }

        /// <summary>Nearest-rank percentile over an already-sorted list.</summary>
        private static float Percentile(List<float> sorted, double p)
            => sorted.Count == 0 ? float.NaN : sorted[Math.Clamp((int)(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

        private static float MedianOf(List<float> values)
        {
            values.Sort();
            return values[values.Count / 2];
        }
    }
}
