using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public class StarReferenceTable
{
    private readonly List<Vector2> _source;
    private readonly List<Vector2> _dest;

    private StarReferenceTable(List<Vector2> source, List<Vector2> dest)
    {
        _source = source;
        _dest = dest;
    }

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

    public Task<Matrix3x2?> FindOffsetAndRotationAsync(float solutionTolerance = 1e-3f)
    {
        if (Matrix3x2.FitAffineTransform(_dest, _source) is { } solution)
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