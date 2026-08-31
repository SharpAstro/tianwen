using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Numerics;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Astrometry.PlateSolve
{
    /// <summary>
    /// Recovers the plate scale from the stars alone, so the geometric seed stops depending on
    /// <c>FOCALLEN</c>.
    ///
    /// <para><b>Why this exists.</b> <see cref="PairRansacLock"/> needs a scale prior because a pair
    /// gives a distance and a distance has units, and the only prior available is whatever focal
    /// length a human typed. It is wrong often enough to be a design constraint rather than an edge
    /// case: 270 mm entered for optics that deliver 269.5 (0.37%), 203 for 202.5 (0.25%), 1200 for
    /// 1180 (1.7%), and a 130 mm lens entered as its MARKETED 135 (3.9%), which is systematic rather
    /// than a typo and put a 3,065-star frame outside the window entirely, so it did not solve at
    /// all. <c>MinPairLockScaleTolerance</c> is +/-5% to survive that, and the window's width is a
    /// direct multiplier on the candidate pairs every hypothesis is drawn from.</para>
    ///
    /// <para><b>A quad descriptor is five RATIOS, which are scale-free</b>, so a matched quad hands
    /// the scale back as the ratio of the two longest sides -- with no prior at all. That is the one
    /// thing quads do here that pairs structurally cannot, and it needs only a handful of matched
    /// quads rather than the hundreds of star correspondences the refinement loop consumes (which is
    /// why quad matching is NOT used for the correspondences; see the measurements on
    /// <see cref="StarReferenceTable"/>).</para>
    ///
    /// <para><b>Measured over the 24 frozen Vela panels</b> (<c>QuadCatalogFeasibilityProbe</c>),
    /// with the catalog projected through the header hint -- pointing wrong by up to 40 arcmin,
    /// unrotated where the real fields are rotated -- and the scale deliberately 3.9% wrong: the
    /// recovered ratio lands within <b>0.065%</b> of the frame's own solved scale on 23 of 24
    /// panels, at ~122k comparisons per panel. Rotation is not a confound and that is the point: a
    /// distance is invariant under rotation.</para>
    ///
    /// <para><b>It cannot settle the parity.</b> Reflection also preserves distances, so a mirrored
    /// field has identical descriptors. The recovered ratio is therefore the SAME for both parities
    /// and must be computed once, outside the parity race, not twice inside it.</para>
    /// </summary>
    internal static class QuadScaleRecovery
    {
        /// <summary>
        /// Tolerance on each of the five scale-free ratios. <see cref="StarQuad.Dist1"/> is
        /// deliberately NEVER compared -- it is the absolute longest side, i.e. exactly the quantity
        /// whose ratio we are trying to measure, so admitting candidates on it would assume the
        /// answer. (That is also why <see cref="StarReferenceTable.FindFit"/> cannot be reused here:
        /// its <c>WithinTolerance</c> gate and its two-pointer window both key on <c>Dist1</c>, which
        /// works for stacking, where both frames share a scale, and rejects everything here.)
        /// </summary>
        /// <remarks>
        /// 0.004 measured flat across 0.002-0.008: the candidate count moves (44 -> 56 per panel) and
        /// the recovered median does not, because the contamination the wider tolerance admits
        /// scatters and the median ignores it.
        /// </remarks>
        internal const float RatioTolerance = 0.004f;

        /// <summary>
        /// How much the candidate ratios may scatter, as MAD about their median divided by that
        /// median, before the recovery is refused.
        /// </summary>
        /// <remarks>
        /// <b>This is the whole safety property, and it was chosen from the separation rather than
        /// guessed.</b> Across the 24 frozen panels, the 23 that recover accurately have spreads of
        /// 0.0004-0.0014; the one that does not sits at 0.3699 -- a 264x gap, so 0.01 has 7x margin
        /// on one side and 37x on the other.
        /// <para><b>Do NOT use the candidate COUNT for this: it is inverted.</b> The one bad panel
        /// produced 92 candidates, MORE than any of the 23 good ones (26-74), so a count threshold
        /// would have singled out the only untrustworthy recovery as the most trustworthy. The reason
        /// is that contamination is chance ratio agreement, which is plentiful and scatters, while
        /// real shared quads are scarce and agree; a count cannot tell fifty agreeing candidates from
        /// fifty scattered ones and a spread can. <c>IQR/median</c> fails too (0.2968 and 0.1583 on
        /// two panels that recover to 0.006% and 0.001%), because the 25th/75th percentiles sit in
        /// the contaminated tails while the MAD is taken about the median.</para>
        /// </remarks>
        internal const float MaxRelativeSpread = 0.01f;

        /// <summary>
        /// Fewest candidates a median may be taken over. Low on purpose: the count is not a quality
        /// signal (see <see cref="MaxRelativeSpread"/>), so this only asks that there be enough
        /// samples for a median and a MAD to mean anything. The worst-recovering good panel had 26.
        /// </summary>
        internal const int MinCandidates = 10;

        /// <summary>
        /// Stars carried in from each side. 500 is what the detector already caps at, what ASTAP
        /// solves this class of field from, and where the shared-quad fraction peaks (15.8% at 500
        /// against 2.6% at 100).
        /// </summary>
        internal const int MaxStars = 500;

        /// <summary>
        /// Below this there are too few stars for the quad population to overlap at all, and
        /// <see cref="StarQuadList"/>'s neighbour window degenerates on a handful of points.
        /// </summary>
        internal const int MinStars = 50;

        /// <param name="Ratio">Detected pixels per projected pixel. The projection's assumed scale
        /// divided by this is the scale the stars actually imply.</param>
        /// <param name="Candidates">Quad pairs that agreed on all five ratios.</param>
        /// <param name="RelativeSpread">MAD about the median over the median: the trust signal.</param>
        internal readonly record struct Recovery(float Ratio, int Candidates, float RelativeSpread);

        /// <summary>
        /// Returns the scale the stars imply, or <c>null</c> when the candidate set is too small or
        /// too scattered to believe. A refusal is a normal outcome and the caller must keep its
        /// existing prior; a recovery is only ever an improvement on a guess.
        /// </summary>
        /// <param name="detectedPoints">Detected centroids, brightest first.</param>
        /// <param name="projectedPoints">Catalog stars projected to pixels at the assumed scale,
        /// brightest first.</param>
        internal static Recovery? TryRecover(
            ReadOnlySpan<Vector2> detectedPoints,
            ReadOnlySpan<Vector2> projectedPoints,
            ILogger? logger = null)
        {
            var detCount = Math.Min(MaxStars, detectedPoints.Length);
            var projCount = Math.Min(MaxStars, projectedPoints.Length);
            if (detCount < MinStars || projCount < MinStars)
            {
                return null;
            }

            // Truncate brightest-first FIRST, then sort by X: the truncation is a brightness
            // decision and the sort is what StarQuadList's index window requires. Copies, because
            // both inputs stay in brightest-first order for the seed that runs after this.
            var detQuads = new StarQuadList(XSorted(detectedPoints[..detCount]));
            var projQuads = new StarQuadList(XSorted(projectedPoints[..projCount]));

            var ratios = new List<float>();
            for (var i = 0; i < detQuads.Count; i++)
            {
                var q = detQuads[i];
                for (var j = 0; j < projQuads.Count; j++)
                {
                    var c = projQuads[j];
                    if (MathF.Abs(q.Dist2 - c.Dist2) <= RatioTolerance
                        && MathF.Abs(q.Dist3 - c.Dist3) <= RatioTolerance
                        && MathF.Abs(q.Dist4 - c.Dist4) <= RatioTolerance
                        && MathF.Abs(q.Dist5 - c.Dist5) <= RatioTolerance
                        && MathF.Abs(q.Dist6 - c.Dist6) <= RatioTolerance
                        && c.Dist1 > 0)
                    {
                        ratios.Add(q.Dist1 / c.Dist1);
                    }
                }
            }

            if (ratios.Count < MinCandidates)
            {
                logger?.LogDebug("QuadScaleRecovery: {Candidates} candidates from {DetQuads}x{ProjQuads} quads, below the {Min} needed for a median",
                    ratios.Count, detQuads.Count, projQuads.Count, MinCandidates);
                return null;
            }

            ratios.Sort();
            var median = Median(ratios);

            var deviations = new List<float>(ratios.Count);
            foreach (var r in ratios)
            {
                deviations.Add(MathF.Abs(r - median));
            }
            deviations.Sort();
            var spread = median > 0 ? Median(deviations) / median : float.PositiveInfinity;

            if (!float.IsFinite(median) || median <= 0 || spread > MaxRelativeSpread)
            {
                logger?.LogDebug("QuadScaleRecovery: refused, {Candidates} candidates scatter to {Spread:F4} (bar {Bar:F4}) about {Median:F4}",
                    ratios.Count, spread, MaxRelativeSpread, median);
                return null;
            }

            logger?.LogDebug("QuadScaleRecovery: scale ratio {Ratio:F5} from {Candidates} candidates ({DetQuads}x{ProjQuads} quads), spread {Spread:F4}",
                median, ratios.Count, detQuads.Count, projQuads.Count, spread);
            return new Recovery(median, ratios.Count, spread);
        }

        /// <summary>Median of an already-sorted list.</summary>
        private static float Median(List<float> sorted)
            => sorted.Count == 0
                ? float.NaN
                : sorted.Count % 2 == 1
                    ? sorted[sorted.Count / 2]
                    : 0.5f * (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]);

        private static Vector2[] XSorted(ReadOnlySpan<Vector2> points)
        {
            var copy = points.ToArray();
            Array.Sort(copy, static (a, b) => a.X.CompareTo(b.X));
            return copy;
        }
    }
}
