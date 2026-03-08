using System;
using System.Collections.Generic;
using System.Numerics;
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
///         distances agree within <c>quadTolerance</c>.</item>
///   <item>Outlier removal: the Dist1 ratio between matched quads is computed, and pairs
///         whose ratio deviates from the median by more than <c>quadTolerance × median</c>
///         are discarded.</item>
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
    {
        // minimum_count required, 6 for stacking, 3 for plate solving
        if (quadStarDistances1.Count < minimumCount || quadStarDistances2.Count < minimumCount)
        {
            return null;
        }

        // find a tolerance resulting in 6 or more of the best matching quads
        var matchList2 = new List<(int Idx1, int Idx2)>();

        for (int i = 0; i < quadStarDistances1.Count; i++)
        {
            for (var j = 0; j < quadStarDistances2.Count; j++)
            {
                var left = quadStarDistances1[i];
                var other = quadStarDistances2[j];
                if (left.WithinTolerance(other, quadTolerance))
                {
                    matchList2.Add((i, j));
                }
                else if (left.Dist1 < other.Dist1 + quadTolerance)
                {
                    // since the list is sorted by distance,
                    // if the current left is smaller than the current other by more than the tolerance,
                    // then it won't match with any of the following others
                    break;
                }
            }
        }

        if (matchList2.Count < minimumCount)
        {
            return null;
        }

        var ratios = new float[matchList2.Count];
        var ratiosCopy = new float[matchList2.Count];
        for (int k = 0; k < matchList2.Count; k++)
        {
            // ratio between largest length of found and reference quad
            ratiosCopy[k] = ratios[k] = quadStarDistances1[matchList2[k].Idx1].Dist1 / quadStarDistances2[matchList2[k].Idx2].Dist1;
        }

        // median is in place
        var medianRatio = Median(ratiosCopy);

        // remove outliers
        var matchList1 = new List<(int Idx1, int Idx2)>(matchList2.Count);
        for (int k = 0; k < matchList2.Count; k++)
        {
            if (Math.Abs(medianRatio - ratios[k]) <= quadTolerance * medianRatio)
            {
                matchList1.Add(matchList2[k]);
            }
        }

        // build reference table
        if (matchList1.Count >= 3)
        {
            var source = new List<Vector2>(matchList1.Count);
            var dest = new List<Vector2>(matchList1.Count);

            for (int k = 0; k < matchList1.Count; k++)
            {
                var a = quadStarDistances2[matchList1[k].Idx2];
                source.Add(new Vector2(a.X, a.Y));

                var b = quadStarDistances1[matchList1[k].Idx1];
                dest.Add(new Vector2(b.X, b.Y));
            }

            return new StarReferenceTable(source, dest);
        }

        return null;
    }

    /// <summary>
    /// Fits a least-squares affine transform from dest to source positions without validation.
    /// Returns <c>null</c> if fewer than 3 pairs or the system is singular.
    /// </summary>
    public Matrix3x2? FitAffineTransform() => Matrix3x2.FitAffineTransform(_dest, _source);

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