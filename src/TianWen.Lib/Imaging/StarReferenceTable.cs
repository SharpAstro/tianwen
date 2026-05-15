using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

/// <summary>
/// A table of matched star position pairs (source → dest) produced by geometric quad matching,
/// used to compute the affine transform between two star fields.
///
/// <para><b>Matching pipeline</b></para>
/// <list type="number">
///   <item><see cref="FindFit"/> compares <see cref="StarQuad"/> invariants from two
///         <see cref="StarQuadList"/> instances, finding quads whose 6 normalised pairwise
///         distances agree within <c>quadTolerance</c>. Both lists are sorted by Dist1 so
///         the candidate scan uses a two-pointer sliding window over the Dist1 axis
///         (O(Q1 + Q2 + W) where W is the total candidates inside the window) instead of
///         the naive O(Q1·Q2) cross-product.</item>
///   <item>Outlier removal via RANSAC: random 3-pair samples are used to fit an affine,
///         and candidates whose dest→source residual is below <c>RansacInlierThresholdPx</c>
///         are counted as inliers. Iterations are capped adaptively based on the best
///         inlier fraction seen so far. Unlike a median-ratio gate, this rejects outliers
///         by <em>geometric consensus</em>, so it tolerates loose <c>quadTolerance</c>
///         values where the median-ratio filter would let bad pairs through.</item>
///   <item>The surviving quad centres become the matched point pairs stored in this table.</item>
/// </list>
///
/// <para><b>Affine fitting</b></para>
/// <list type="bullet">
///   <item><see cref="FitAffineTransform"/> — raw least-squares affine (dest → source).</item>
///   <item><see cref="FindOffsetAndRotationAsync"/> — same fit but validated via
///         <see cref="Matrix3x2Helper.Decompose"/>: rejects non-uniform scale (&gt; <c>solutionTolerance</c>),
///         significant skew, or negative scale (mirror flip).</item>
/// </list>
///
/// <para><b>Applicability</b></para>
/// <para>Quad matching requires both star lists to represent similar stellar populations
/// (same detection characteristics, comparable star counts). This holds for <b>image stacking</b>
/// where both frames are captured with the same camera and optics. It does <em>not</em> hold for
/// <b>catalog plate solving</b>, where projected catalog stars and detected image stars have
/// different populations (faint catalog stars below the detection threshold, detected artefacts
/// absent from the catalog). The differing nearest-neighbour sets produce incompatible quad
/// geometries, yielding too few matches for a reliable affine fit. Plate solving therefore uses
/// proximity-based matching with brightness-rank penalties instead
/// (see <c>CatalogPlateSolver</c>).</para>
/// </summary>
public class StarReferenceTable
{
    private readonly List<Vector2> _source;
    private readonly List<Vector2> _dest;

    private StarReferenceTable(List<Vector2> source, List<Vector2> dest)
    {
        _source = source;
        _dest = dest;
    }

    /// <summary>Number of matched star pairs in this table.</summary>
    public int Count => _source.Count;

    /// <summary>
    /// Matches quads from two <see cref="StarQuadList"/> instances and builds a reference table
    /// of corresponding star positions.
    /// </summary>
    /// <param name="quadStarDistances1">Quads from the first star field (positions become <em>dest</em>).</param>
    /// <param name="quadStarDistances2">Quads from the second star field (positions become <em>source</em>).</param>
    /// <param name="minimumCount">Minimum number of quad matches required (6 for stacking, 3 for sparse fields).</param>
    /// <param name="quadTolerance">Maximum absolute difference allowed on each of the 6 quad distances.
    /// Note: <see cref="StarQuad.Dist1"/> is in absolute pixels while Dist2–Dist6 are normalised ratios,
    /// so this tolerance has mixed units — it works because stacking images have near-identical Dist1 values.</param>
    /// <returns>A reference table of matched pairs, or <c>null</c> if too few matches survive outlier removal.</returns>
    public static StarReferenceTable? FindFit(StarQuadList quadStarDistances1, StarQuadList quadStarDistances2, int minimumCount = 6, float quadTolerance = 0.008f)
        => FindFitWithDiagnostics(quadStarDistances1, quadStarDistances2, minimumCount, quadTolerance).Table;

    /// <summary>
    /// Diagnostic version of <see cref="FindFit"/> that surfaces the intermediate
    /// gate counts so callers can see why a particular pair didn't match without
    /// re-running the inner loops. Test-only -- production code calls
    /// <see cref="FindFit"/>, which discards the diagnostic record.
    /// </summary>
    /// <returns>The reference table (or null if no match) plus a record carrying
    /// the raw quad-pair count, post-outlier-filter count, and median Dist1 ratio.</returns>
    /// <summary>Pixel residual threshold for RANSAC inlier classification (reference-frame units).
    /// Matches the convention used by OpenCV's <c>cv2.estimateAffine2D(method=cv2.RANSAC)</c>.</summary>
    internal const float RansacInlierThresholdPx = 3.0f;

    /// <summary>Hard cap on RANSAC iterations. Adaptive shrink runs the loop down based on the best
    /// inlier fraction observed so far, but never above this ceiling.</summary>
    internal const int RansacMaxIterations = 1024;

    /// <summary>Minimum inliers required to accept a RANSAC fit. The 3-point sample exactly
    /// determines the 6-DOF affine, so a sample always self-fits with zero residual; "3 inliers"
    /// is therefore meaningless on its own. Requiring at least one extra consistent pair beyond
    /// the sample (>=4) eliminates the degenerate self-fit case observed when raw-pair counts
    /// are near the minimumCount floor. Real matches show 8+ inliers; this floor mostly catches
    /// near-empty candidate sets that would otherwise produce a nonsense affine that gate 4
    /// has to reject anyway.</summary>
    internal const int RansacMinInliers = 4;

    /// <summary>Confidence level for adaptive iteration count: stop when probability of having sampled
    /// at least one all-inlier triplet exceeds this value.</summary>
    private const float RansacConfidence = 0.99f;

    internal static (StarReferenceTable? Table, FindFitDiagnostics Diagnostics) FindFitWithDiagnostics(
        StarQuadList quadStarDistances1, StarQuadList quadStarDistances2, int minimumCount = 6, float quadTolerance = 0.008f)
    {
        var diag = new FindFitDiagnostics(quadStarDistances1.Count, quadStarDistances2.Count, 0, 0, float.NaN, quadTolerance);

        // minimum_count required, 6 for stacking, 3 for plate solving
        if (quadStarDistances1.Count < minimumCount || quadStarDistances2.Count < minimumCount)
        {
            return (null, diag);
        }

        // Candidate search via two-pointer sliding window over the Dist1 axis. Both lists are
        // sorted ascending by Dist1 (see StarQuadList ctor), so j_lo and j_hi only advance
        // forward as i increases. Inside the window, we still need to call WithinTolerance to
        // check Dist2..Dist6 (normalised ratios).
        var matchList2 = new List<(int Idx1, int Idx2)>();
        int q2Count = quadStarDistances2.Count;
        int jLo = 0, jHi = 0;

        for (int i = 0; i < quadStarDistances1.Count; i++)
        {
            var left = quadStarDistances1[i];
            var lower = left.Dist1 - quadTolerance;
            var upper = left.Dist1 + quadTolerance;

            // Advance jLo past quads with Dist1 < lower (monotonic over i).
            while (jLo < q2Count && quadStarDistances2[jLo].Dist1 < lower) jLo++;
            // Advance jHi past quads with Dist1 <= upper (one-past-the-end).
            while (jHi < q2Count && quadStarDistances2[jHi].Dist1 <= upper) jHi++;

            for (int j = jLo; j < jHi; j++)
            {
                if (left.WithinTolerance(quadStarDistances2[j], quadTolerance))
                {
                    matchList2.Add((i, j));
                }
            }
        }

        diag = diag with { RawPairs = matchList2.Count };

        if (matchList2.Count < minimumCount)
        {
            return (null, diag);
        }

        // Materialise the candidate point arrays once -- RANSAC reads them O(iter) times.
        // dest = quad centre in light frame (matchList2[k].Idx1 -> quadStarDistances1)
        // source = quad centre in reference frame (matchList2[k].Idx2 -> quadStarDistances2)
        var allDest = new Vector2[matchList2.Count];
        var allSource = new Vector2[matchList2.Count];
        var ratios = new float[matchList2.Count];
        for (int k = 0; k < matchList2.Count; k++)
        {
            var (i1, i2) = matchList2[k];
            var d = quadStarDistances1[i1];
            var s = quadStarDistances2[i2];
            allDest[k] = new Vector2(d.X, d.Y);
            allSource[k] = new Vector2(s.X, s.Y);
            // Median ratio kept as a diagnostic so callers can still see the implied scale factor.
            ratios[k] = d.Dist1 / s.Dist1;
        }
        var medianRatio = MedianFast(ratios); // in-place; ratios is no longer used after this

        var bestInliers = RansacAffineInliers(allDest, allSource, RansacInlierThresholdPx, RansacMaxIterations, RansacConfidence);

        diag = diag with { FilteredPairs = bestInliers.Count, MedianRatio = medianRatio };

        // Build reference table only when RANSAC found a non-degenerate consensus. The 3-point
        // affine fit is exact, so the sample triplet itself is always reported as 3 inliers --
        // requiring >=4 means we have at least one extra pair agreeing, which is what makes the
        // fit meaningful.
        if (bestInliers.Count >= RansacMinInliers)
        {
            var source = new List<Vector2>(bestInliers.Count);
            var dest = new List<Vector2>(bestInliers.Count);

            foreach (var k in bestInliers)
            {
                source.Add(allSource[k]);
                dest.Add(allDest[k]);
            }

            return (new StarReferenceTable(source, dest), diag);
        }

        return (null, diag);
    }

    /// <summary>
    /// RANSAC inlier search for an affine transform that maps <paramref name="allDest"/> points to
    /// <paramref name="allSource"/> points. Returns the indices of all candidates whose residual,
    /// after applying the best-seen 3-point model, is below <paramref name="inlierThresholdPx"/>.
    /// </summary>
    private static List<int> RansacAffineInliers(
        ReadOnlySpan<Vector2> allDest,
        ReadOnlySpan<Vector2> allSource,
        float inlierThresholdPx,
        int maxIterations,
        float confidence)
    {
        var n = allDest.Length;
        if (n < 3) return [];

        // Deterministic seed -- the matcher must be reproducible for tests and for the
        // ladder retry strategy in callers (same inputs => same result).
        var rng = new Random(42);
        var bestInliers = new List<int>();
        var workingInliers = new List<int>(n);
        var thresholdSq = inlierThresholdPx * inlierThresholdPx;
        var dynamicCap = maxIterations;

        Span<Vector2> sampleDest = stackalloc Vector2[3];
        Span<Vector2> sampleSource = stackalloc Vector2[3];

        for (int iter = 0; iter < dynamicCap; iter++)
        {
            // Three distinct random indices. With n>=6 (minimumCount) the rejection loop is cheap.
            int a = rng.Next(n);
            int b; do { b = rng.Next(n); } while (b == a);
            int c; do { c = rng.Next(n); } while (c == a || c == b);

            sampleDest[0] = allDest[a]; sampleSource[0] = allSource[a];
            sampleDest[1] = allDest[b]; sampleSource[1] = allSource[b];
            sampleDest[2] = allDest[c]; sampleSource[2] = allSource[c];

            // Fit affine: matches StarReferenceTable.FitAffineTransform which calls
            // Matrix3x2.FitAffineTransform(dest, source) -- the returned matrix maps dest -> source.
            var model = Matrix3x2.FitAffineTransform(sampleDest, sampleSource);
            if (model is null) continue; // collinear sample, try another
            var m = model.Value;

            // Count inliers: transform each dest point through m, compare to source.
            workingInliers.Clear();
            for (int k = 0; k < n; k++)
            {
                var predicted = Vector2.Transform(allDest[k], m);
                if (Vector2.DistanceSquared(predicted, allSource[k]) < thresholdSq)
                {
                    workingInliers.Add(k);
                }
            }

            if (workingInliers.Count > bestInliers.Count)
            {
                (bestInliers, workingInliers) = (workingInliers, bestInliers);

                // Adaptive iteration count: N = log(1 - p) / log(1 - w^s), s = 3 (sample size).
                // Once we've seen a high-inlier model, fewer additional iterations are needed for
                // the same confidence -- bail out early instead of churning to maxIterations.
                var inlierFrac = (double)bestInliers.Count / n;
                if (inlierFrac > 1e-3 && inlierFrac < 1.0)
                {
                    var w3 = inlierFrac * inlierFrac * inlierFrac;
                    var required = (int)Math.Ceiling(Math.Log(1.0 - confidence) / Math.Log(1.0 - w3));
                    dynamicCap = Math.Min(dynamicCap, Math.Max(iter + 1, required));
                }
            }
        }

        return bestInliers;
    }

    /// <summary>Intermediate gate counts from <see cref="FindFitWithDiagnostics"/>.</summary>
    internal readonly record struct FindFitDiagnostics(
        int Quads1,
        int Quads2,
        int RawPairs,
        int FilteredPairs,
        float MedianRatio,
        float QuadTolerance);

    /// <summary>
    /// Fits a least-squares affine transform from dest to source positions without validation.
    /// Returns <c>null</c> if fewer than 3 pairs or the system is singular.
    /// </summary>
    public Matrix3x2? FitAffineTransform() => Matrix3x2.FitAffineTransform(CollectionsMarshal.AsSpan(_dest), CollectionsMarshal.AsSpan(_source));

    /// <summary>
    /// Fits an affine transform and validates it via <see cref="Matrix3x2Helper.Decompose"/>:
    /// both scale components must be positive (rejects mirror flips), their ratio must be within
    /// <paramref name="solutionTolerance"/> of 1.0 (uniform scale), and skew must be below the
    /// same threshold.
    /// </summary>
    /// <param name="solutionTolerance">Maximum allowed deviation for scale ratio and skew
    /// (1e-3 for stacking, higher for noisier matches).</param>
    /// <returns>The validated affine transform, or <c>null</c> if validation fails.</returns>
    public Task<Matrix3x2?> FindOffsetAndRotationAsync(float solutionTolerance = 1e-3f)
    {
        if (FitAffineTransform() is { } solution)
        {
            var (scale, skew, _, _) = solution.Decompose();

            if (scale.X > 0f && scale.Y > 0f &&
                MathF.Abs(scale.X / scale.Y - 1.0f) <= solutionTolerance &&
                MathF.Abs(skew.X) <= solutionTolerance && MathF.Abs(skew.Y) <= solutionTolerance)
            {
                return Task.FromResult<Matrix3x2?>(solution);
            }
        }
        return Task.FromResult<Matrix3x2?>(null);
    }
}