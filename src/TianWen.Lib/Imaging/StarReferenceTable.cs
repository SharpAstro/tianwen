using System;
using System.Collections.Generic;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public class StarReferenceTable
{
    private readonly float[,] _aXYs;
    private readonly float[] _bXs;
    private readonly float[] _bYs;

    private StarReferenceTable(float[,] aXYs, float[] bXs, float[] bYs)
    {
        _aXYs = aXYs;
        _bXs = bXs;
        _bYs = bYs;
    }

    public static StarReferenceTable? FindFit(IReadOnlyList<StarQuad> quadStarDistances1, IReadOnlyList<StarQuad> quadStarDistances2, int minimumCount = 6, float quadTolerance = 0.008f)
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
            for (int j = 0; j < quadStarDistances2.Count; j++)
            {
                if (MathF.Abs(quadStarDistances1[i].Dist1 - quadStarDistances2[j].Dist1) <= quadTolerance &&
                    MathF.Abs(quadStarDistances1[i].Dist2 - quadStarDistances2[j].Dist2) <= quadTolerance &&
                    MathF.Abs(quadStarDistances1[i].Dist3 - quadStarDistances2[j].Dist3) <= quadTolerance &&
                    MathF.Abs(quadStarDistances1[i].Dist4 - quadStarDistances2[j].Dist4) <= quadTolerance &&
                    MathF.Abs(quadStarDistances1[i].Dist5 - quadStarDistances2[j].Dist5) <= quadTolerance
                )
                {
                    matchList2.Add((i, j));
                }
            }
        }

        if (matchList2.Count < minimumCount)
        {
            return null;
        }

        var ratios = new float[matchList2.Count];
        for (int k = 0; k < matchList2.Count; k++)
        {
            // ratio between largest length of found and reference quad
            ratios[k] = quadStarDistances1[matchList2[k].Idx1].Dist1 / quadStarDistances2[matchList2[k].Idx2].Dist1;
        }

        var medianRatio = Median(ratios);

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
            var aXYPositions = new float[3, matchList1.Count];
            var bXRefPositions = new float[matchList1.Count];
            var bYRefPositions = new float[matchList1.Count];

            for (int k = 0; k < matchList1.Count; k++)
            {
                aXYPositions[0, k] = quadStarDistances2[matchList1[k].Idx2].X;
                aXYPositions[1, k] = quadStarDistances2[matchList1[k].Idx2].Y;
                aXYPositions[2, k] = 1;

                bXRefPositions[k] = quadStarDistances1[matchList1[k].Idx1].X;
                bYRefPositions[k] = quadStarDistances1[matchList1[k].Idx1].Y;
            }

            return new StarReferenceTable(aXYPositions, bXRefPositions, bYRefPositions);
        }

        return null;
    }


}