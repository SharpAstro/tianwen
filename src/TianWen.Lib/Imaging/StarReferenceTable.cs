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

    public bool FindOffsetAndRotation()
    {
        if (LsqFit(out var xs, out var ys))
        {
            var xy_sqr_ratio = (MathF.Pow(xs[0], 2) + MathF.Pow(xs[1], 2)) / MathF.Pow(ys[0], 2) + MathF.Pow(ys[1], 2);

            // if dimensions x, y are not the same, something wrong.
            if (xy_sqr_ratio is >= 0.9f and <= 1.1f)
            {


                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Find the solution vector of an overdetermined system of linear equations according to the method of least squares using GIVENS rotations
    ///
    /// Solve x of A x = b with the least-squares method
    ///
    /// In matrix calculations,
    /// <code>b_matrix[0..nr_columns-1, 0..nr_equations-1]:=solution_vector[0..2] * A_XYpositions[0..nr_columns-1, 0..nr_equations-1]</code>
    ///
    /// see also Montenbruck &amp; Pfleger, Astronomy on the personal computer
    /// </summary>
    /// <param name="xs">Solution vector for x coordinates</param>
    /// <param name="ys">Solution vector for y coordinates</param>
    /// <returns>True if the solution is found, otherwise false</returns>
    private bool LsqFit(out float[] xs, out float[] ys)
    {
        const double tiny = 1E-10;
        int nrEquations = _aXYs.GetLength(1);
        int nrColumns = _aXYs.GetLength(0);

        Span<(float[] BMatrix, float[] Solution)> matrices = [(_bXs, new float[nrColumns]), (_bYs, new float[nrColumns])];

        float[,] tempMatrix = new float[nrColumns, nrEquations];
        foreach (var (bMatrix, xMatrix) in matrices)
        {
            Buffer.BlockCopy(_aXYs, 0, tempMatrix, 0, _aXYs.Length * sizeof(float));
 
            for (int j = 0; j < nrColumns; j++)
            {
                for (int i = j + 1; i < nrEquations; i++)
                {
                    if (tempMatrix[j, i] != 0)
                    {
                        // calculate p, q and new temp_matrix[j,j]; set temp_matrix[j,i]=0
                        double p, q, h;
                        if (Math.Abs(tempMatrix[j, j]) < tiny * Math.Abs(tempMatrix[j, i]))
                        {
                            p = 0;
                            q = 1;
                            tempMatrix[j, j] = -tempMatrix[j, i];
                            tempMatrix[j, i] = 0;
                        }
                        else
                        {
                            // Notes:
                            // Zero the left bottom corner of the matrix
                            // Residuals are r1..rn
                            // The sum of the sqr(residuals) should be minimised.
                            // Take two numbers where (p^2+q^2) = 1.
                            // Then (r1^2+r2^2) = (p^2+q^2)*(r1^2+r2^2)
                            // Choose p and h as follows:
                            // p = +A11/h
                            // q = -A21/h
                            // where h= +-sqrt(A11^2+A21^2)
                            // A21=q*A11+p*A21 = (-A21*A11 + A21*A11)/h=0
                            h = Math.Sqrt(tempMatrix[j, j] * tempMatrix[j, j] + tempMatrix[j, i] * tempMatrix[j, i]);
                            if (tempMatrix[j, j] < 0) h = -h;
                            p = tempMatrix[j, j] / h;
                            q = -tempMatrix[j, i] / h;
                            tempMatrix[j, j] = (float)h;
                            tempMatrix[j, i] = 0;
                        }

                        // calculate the rest of the line
                        for (int k = j + 1; k < nrColumns; k++)
                        {
                            h = p * tempMatrix[k, j] - q * tempMatrix[k, i];
                            tempMatrix[k, i] = (float)(q * tempMatrix[k, j] + p * tempMatrix[k, i]);
                            tempMatrix[k, j] = (float)h;
                        }

                        h = p * bMatrix[j] - q * bMatrix[i];
                        bMatrix[i] = (float)(q * bMatrix[j] + p * bMatrix[i]);
                        bMatrix[j] = (float)h;
                    }
                }
            }

            for (int i = nrColumns - 1; i >= 0; i--)
            {
                double h = bMatrix[i];
                for (int k = i + 1; k < nrColumns; k++)
                {
                    h -= tempMatrix[k, i] * xMatrix[k];
                }
                if (Math.Abs(tempMatrix[i, i]) > 1E-30)
                {
                    xMatrix[i] = (float)(h / tempMatrix[i, i]);
                }
                else
                {
                    xs = [];
                    ys = [];
                    return false; // Prevent runtime error dividing by zero
                }
            }
        }

        xs = matrices[0].Solution;
        ys = matrices[1].Solution;

        return true;
    }
}