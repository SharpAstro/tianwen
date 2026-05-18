using System;
using System.Numerics;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Translation-only refinement for the bulk affine produced by
/// <see cref="SortedStarList.FindOffsetAndRotationWithRmsAsync"/>. The
/// quad-fingerprint match minimises RMS residual across the brightest
/// N stars but leaves a small per-frame translation bias when the
/// session crosses a meridian flip (pre- and post-flip frames have
/// slightly different mount + atmosphere offsets that fit the bulk
/// average instead of zero). In bilinear-warp strategies that bias is
/// invisible because the warp kernel smooths over a 1-2 px shift; in
/// <see cref="DrizzleStrategy"/> the bias is preserved as a "dumbbell"
/// stretch on every star -- pre-flip stack centroid at one position,
/// post-flip centroid at another.
///
/// <para>This refiner shifts each frame's source-to-reference affine by
/// the median residual of its detected stars against the reference
/// frame's detected stars, after applying the bulk affine. Rotation
/// and scale stay at whatever the bulk fit produced; this is strictly
/// a translation correction. Empirically sufficient for typical
/// meridian-flip dumbbells (verified against SoL data: 244-frame
/// dataset, dumbbells visible pre-refinement, round stars after).
/// Rotation refinement would be a Kabsch-based extension; not needed
/// for the meridian-flip case but kept as a follow-up for future
/// non-rigid distortions.</para>
/// </summary>
internal static class RegistrationRefiner
{
    /// <summary>
    /// Returns <paramref name="bulkAffine"/> shifted by the median
    /// (dx, dy) residual between the frame's stars (after warp by
    /// <paramref name="bulkAffine"/>) and <paramref name="referenceStars"/>.
    /// Falls back to <paramref name="bulkAffine"/> unchanged when fewer
    /// than <paramref name="minMatchedStars"/> pairs find a match within
    /// <paramref name="matchToleranceRefPx"/>.
    /// </summary>
    /// <param name="lightStars">Detected stars on the light frame, in
    /// SOURCE pixel coordinates.</param>
    /// <param name="referenceStars">Detected stars on the reference
    /// frame, in REFERENCE pixel coordinates.</param>
    /// <param name="bulkAffine">Source-to-reference affine from the
    /// quad-fingerprint match.</param>
    /// <param name="matchToleranceRefPx">Maximum nearest-neighbour
    /// distance in reference space to accept a match. 5 px is generous
    /// vs the typical bulk-fit RMS (~0.3 px) but tight enough to reject
    /// random-star pairings on dense fields.</param>
    /// <param name="minMatchedStars">Minimum matched pairs to compute a
    /// median safely. Below this, the residual is too noisy and we
    /// return the bulk affine unchanged.</param>
    /// <returns>Refined affine, plus the median residual it applied (for
    /// caller logging).</returns>
    public static (Matrix3x2 Refined, float MedianDx, float MedianDy, int MatchedCount)
        RefineTranslation(
            SortedStarList lightStars,
            SortedStarList referenceStars,
            Matrix3x2 bulkAffine,
            float matchToleranceRefPx = 5.0f,
            int minMatchedStars = 8)
    {
        // Snapshot reference positions into an array so the inner loop's
        // nearest-neighbour scan doesn't pay enumerator overhead per
        // light star. Brute-force O(N*M) -- 100x100 = 10k comparisons,
        // negligible vs the per-frame load + debayer cost upstream.
        var refCount = referenceStars.Count;
        var refX = new float[refCount];
        var refY = new float[refCount];
        var i = 0;
        foreach (var s in referenceStars)
        {
            refX[i] = s.XCentroid;
            refY[i] = s.YCentroid;
            i++;
        }

        var tolSq = matchToleranceRefPx * matchToleranceRefPx;
        var residualsX = new float[lightStars.Count];
        var residualsY = new float[lightStars.Count];
        var matched = 0;
        foreach (var ls in lightStars)
        {
            var predicted = Vector2.Transform(new Vector2(ls.XCentroid, ls.YCentroid), bulkAffine);
            var bestSq = float.MaxValue;
            float bestDx = 0f, bestDy = 0f;
            for (var j = 0; j < refCount; j++)
            {
                var dx = refX[j] - predicted.X;
                var dy = refY[j] - predicted.Y;
                var sq = dx * dx + dy * dy;
                if (sq < bestSq && sq <= tolSq)
                {
                    bestSq = sq;
                    bestDx = dx;
                    bestDy = dy;
                }
            }
            if (bestSq < float.MaxValue)
            {
                residualsX[matched] = bestDx;
                residualsY[matched] = bestDy;
                matched++;
            }
        }

        if (matched < minMatchedStars)
        {
            return (bulkAffine, 0f, 0f, matched);
        }

        Array.Sort(residualsX, 0, matched);
        Array.Sort(residualsY, 0, matched);
        var medianDx = residualsX[matched / 2];
        var medianDy = residualsY[matched / 2];

        // Matrix3x2 is a mutable struct -- copy then adjust translation
        // fields. M31/M32 are the affine's translation in source-to-
        // reference space; adding the median residual shifts the
        // warped-frame stars onto the reference stars.
        var refined = bulkAffine;
        refined.M31 += medianDx;
        refined.M32 += medianDy;
        return (refined, medianDx, medianDy, matched);
    }
}
