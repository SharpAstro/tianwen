using System;
using System.Numerics;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>
/// Per-frame refinement of the bulk affine produced by
/// <see cref="SortedStarList.FindOffsetAndRotationWithRmsAsync"/>. The
/// quad-fingerprint match minimises RMS residual across the brightest
/// N stars but leaves small per-frame residuals (sub-pixel translation
/// bias from a meridian flip, plus slow rotation/scale drift from
/// atmospheric refraction + mount imperfection over multi-hour stacks)
/// that the bulk fit averages away. In bilinear-warp strategies those
/// residuals are invisible because the warp kernel smooths over 1-2 px
/// of misregistration; in <see cref="DrizzleStrategy"/> they show up
/// directly as broader stars or, in the meridian-flip case, dumbbells.
///
/// <para>Two variants live here:</para>
/// <list type="bullet">
///   <item><see cref="RefineTranslation"/> -- median (dx, dy) residual
///   only. Cheap, robust on a handful of matches, fixes meridian-flip
///   dumbbells. Kept as the low-match-count fallback.</item>
///   <item><see cref="RefineRigid"/> -- full 2D Procrustes (rotation +
///   isotropic scale + translation) via the closed-form 2D solution.
///   Closes the residual that <see cref="RefineTranslation"/> can't
///   touch, which empirically helps long-span pier-side subsets (e.g.
///   SoL pierW: 152 frames over 3 hours saw <c>matched=509/879</c> +
///   23 Tycho-2 SPCC matches vs. pierE's 1019/1165 + 177 -- rotation
///   drift the bulk fit couldn't capture).</item>
/// </list>
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

    /// <summary>
    /// Returns <paramref name="bulkAffine"/> composed with the closed-form
    /// 2D Procrustes refinement (rotation + isotropic scale + translation)
    /// that best maps the bulk-warped light-frame stars onto
    /// <paramref name="referenceStars"/>. Falls back to
    /// <see cref="RefineTranslation"/> when fewer than
    /// <paramref name="minMatchedStars"/> pairs are found (rigid refit
    /// needs ~6+ for a stable rotation estimate; translation only needs 1)
    /// or when the predicted points are collinear / coincident (norm
    /// guard) so a rotation can't be fit.
    /// </summary>
    /// <param name="lightStars">Detected stars on the light frame, in
    /// SOURCE pixel coordinates.</param>
    /// <param name="referenceStars">Detected stars on the reference
    /// frame, in REFERENCE pixel coordinates.</param>
    /// <param name="bulkAffine">Source-to-reference affine from the
    /// quad-fingerprint match.</param>
    /// <param name="matchToleranceRefPx">Maximum nearest-neighbour
    /// distance in reference space to accept a match.</param>
    /// <param name="minMatchedStars">Minimum matched pairs required to
    /// run the full rigid fit. Below this, falls back to
    /// <see cref="RefineTranslation"/> (which needs only 1 pair).</param>
    /// <returns>Refined affine plus diagnostics (scale factor, rotation
    /// in degrees, centroid-shift translation, RMS residual in reference
    /// pixels, matched-pair count). All scalars are reported for the
    /// DELTA refinement applied on top of <paramref name="bulkAffine"/>,
    /// not the absolute composed transform.</returns>
    public static (Matrix3x2 Refined, float Scale, float RotationDeg, float Tx, float Ty, float RmsResidualPx, int MatchedCount)
        RefineRigid(
            SortedStarList lightStars,
            SortedStarList referenceStars,
            Matrix3x2 bulkAffine,
            float matchToleranceRefPx = 5.0f,
            int minMatchedStars = 8)
    {
        // Step 1: snapshot reference positions for nearest-neighbour scan.
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

        // Step 2: pair each light star with its nearest ref star (in
        // reference space, after bulk warp). Brute-force O(N*M) -- same
        // complexity as RefineTranslation.
        var tolSq = matchToleranceRefPx * matchToleranceRefPx;
        var predX = new float[lightStars.Count];
        var predY = new float[lightStars.Count];
        var matchedRefX = new float[lightStars.Count];
        var matchedRefY = new float[lightStars.Count];
        var matched = 0;
        foreach (var ls in lightStars)
        {
            var predicted = Vector2.Transform(new Vector2(ls.XCentroid, ls.YCentroid), bulkAffine);
            var bestSq = float.MaxValue;
            float bestRefX = 0f, bestRefY = 0f;
            for (var j = 0; j < refCount; j++)
            {
                var dx = refX[j] - predicted.X;
                var dy = refY[j] - predicted.Y;
                var sq = dx * dx + dy * dy;
                if (sq < bestSq && sq <= tolSq)
                {
                    bestSq = sq;
                    bestRefX = refX[j];
                    bestRefY = refY[j];
                }
            }
            if (bestSq < float.MaxValue)
            {
                predX[matched] = predicted.X;
                predY[matched] = predicted.Y;
                matchedRefX[matched] = bestRefX;
                matchedRefY[matched] = bestRefY;
                matched++;
            }
        }

        // Translation-only fallback for low-match cases. Rotation
        // estimation needs ~6+ pairs to escape sample noise; below that
        // the translation median is the more honest correction.
        if (matched < minMatchedStars)
        {
            var (translatedFallback, dx, dy, _) = RefineTranslation(
                lightStars, referenceStars, bulkAffine,
                matchToleranceRefPx, minMatchedStars: 1);
            return (translatedFallback, 1f, 0f, dx, dy, 0f, matched);
        }

        // Step 3: centroids in double precision -- the cross-covariance
        // accumulators below sum N products of ~1k-px values, so single-
        // precision would lose a couple of ulps in the rotation angle on
        // dense star fields.
        double sumPX = 0, sumPY = 0, sumRX = 0, sumRY = 0;
        for (var k = 0; k < matched; k++)
        {
            sumPX += predX[k]; sumPY += predY[k];
            sumRX += matchedRefX[k]; sumRY += matchedRefY[k];
        }
        var cpx = sumPX / matched;
        var cpy = sumPY / matched;
        var crx = sumRX / matched;
        var cry = sumRY / matched;

        // Step 4: cross-covariance over centred points.
        double Hxx = 0, Hxy = 0, Hyx = 0, Hyy = 0, sumPSq = 0;
        for (var k = 0; k < matched; k++)
        {
            var px = predX[k] - cpx;
            var py = predY[k] - cpy;
            var rx = matchedRefX[k] - crx;
            var ry = matchedRefY[k] - cry;
            Hxx += px * rx;
            Hxy += px * ry;
            Hyx += py * rx;
            Hyy += py * ry;
            sumPSq += px * px + py * py;
        }

        // Step 5: closed-form 2D Procrustes.
        //   tan θ = (Hxy - Hyx) / (Hxx + Hyy)
        //   s = ((Hxx + Hyy) cos θ + (Hxy - Hyx) sin θ) / Σ |p'|²
        //     = sqrt((Hxy-Hyx)² + (Hxx+Hyy)²) / Σ |p'|²
        // Derivation: minimise Σ |s R p'_i - r'_i|² over (s, θ).
        // Reference: standard "2D rigid Procrustes" (no SVD needed for 2D).
        var num = Hxy - Hyx;
        var den = Hxx + Hyy;
        var norm = Math.Sqrt(num * num + den * den);
        if (norm < 1e-12 || sumPSq < 1e-12)
        {
            // Degenerate: predicted points are coincident (sumPSq=0) or
            // produce a zero cross-covariance for some pathological
            // arrangement (norm=0). Either way we can't fit a rotation;
            // fall back to translation refinement on the existing pairs.
            var (translatedFallback, dx, dy, _) = RefineTranslation(
                lightStars, referenceStars, bulkAffine,
                matchToleranceRefPx, minMatchedStars: 1);
            return (translatedFallback, 1f, 0f, dx, dy, 0f, matched);
        }

        var cosT = den / norm;
        var sinT = num / norm;
        var scale = norm / sumPSq;

        // Step 6: delta in System.Numerics row-vec convention. For a
        // rotation θ + isotropic scale s applied to a row-vector p:
        //   p_after = p * [[s cos θ,  s sin θ],
        //                  [-s sin θ, s cos θ]] + translation
        // The translation is centroid_ref - delta_2x2(centroid_pred), so
        // delta(centroid_pred) == centroid_ref exactly.
        var m11 = (float)(scale * cosT);
        var m12 = (float)(scale * sinT);
        var m21 = (float)(-scale * sinT);
        var m22 = (float)(scale * cosT);
        var m31 = (float)(crx - (cpx * m11 + cpy * m21));
        var m32 = (float)(cry - (cpx * m12 + cpy * m22));
        var delta = new Matrix3x2(m11, m12, m21, m22, m31, m32);

        // Step 7: compose with bulk affine. In System.Numerics row-vec,
        // `A * B` is "apply A, then B", so `bulkAffine * delta` means
        // source -> bulk-warp -> delta-refine -> reference.
        var refined = bulkAffine * delta;

        // Diagnostics: post-refinement RMS in reference pixels + rotation
        // angle in degrees + centroid translation (matches the dx/dy
        // semantic of RefineTranslation for caller continuity).
        double sumResSq = 0;
        for (var k = 0; k < matched; k++)
        {
            var pAfter = Vector2.Transform(new Vector2(predX[k], predY[k]), delta);
            var ddx = matchedRefX[k] - pAfter.X;
            var ddy = matchedRefY[k] - pAfter.Y;
            sumResSq += ddx * ddx + ddy * ddy;
        }
        var rms = (float)Math.Sqrt(sumResSq / matched);
        var rotationDeg = (float)(Math.Atan2(num, den) * 180.0 / Math.PI);
        var txCentroid = (float)(crx - cpx);
        var tyCentroid = (float)(cry - cpy);

        return (refined, (float)scale, rotationDeg, txCentroid, tyCentroid, rms, matched);
    }
}
