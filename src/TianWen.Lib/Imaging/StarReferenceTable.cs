using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public class StarReferenceTable
{
    private readonly float[,] _aXYs;
    private readonly float[] _bXs;
    private readonly float[] _bYs;
    private readonly float[] _errors;

    private StarReferenceTable(float[,] aXYs, float[] bXs, float[] bYs, float[] errors)
    {
        _aXYs = aXYs;
        _bXs = bXs;
        _bYs = bYs;
        _errors = errors;
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
            var aXYPositions = new float[3, matchList1.Count];
            var bXRefPositions = new float[matchList1.Count];
            var bYRefPositions = new float[matchList1.Count];
            var errors = new float[matchList1.Count];

            for (int k = 0; k < matchList1.Count; k++)
            {
                var a = quadStarDistances2[matchList1[k].Idx2];
                aXYPositions[0, k] = a.X;
                aXYPositions[1, k] = a.Y;
                aXYPositions[2, k] = 1;

                var b = quadStarDistances1[matchList1[k].Idx1];
                bXRefPositions[k] = b.X;
                bYRefPositions[k] = b.Y;

                errors[k] = a.Error(b);
            }

            return new StarReferenceTable(aXYPositions, bXRefPositions, bYRefPositions, errors);
        }

        return null;
    }

    public async Task<Matrix3x2?> FindOffsetAndRotationAsync(float solutionTolerance = 1e-3f)
    {
        if (await LsqFitAsync() is { } solution)
        {
            var (scale, skew, _, _) = solution.Decompose();

            if (scale.X > 0f && scale.Y > 0f &&
                MathF.Abs(scale.X / scale.Y - 1.0f) <= solutionTolerance &&
                MathF.Abs(skew.X) <= solutionTolerance && MathF.Abs(skew.Y) <= solutionTolerance)
            {
                return solution;
            }
        }
        return null;
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
    /// <returns>The solution matrix if there's a valid solution</returns>
    private async ValueTask<Matrix3x2?> LsqFitAsync(CancellationToken cancellationToken = default)
    {
        int nrEquations = _aXYs.GetLength(1);
        int nrColumns = _aXYs.GetLength(0);

        LsqFitParams[] matrices = [
            new LsqFitParams(_bXs, new float[nrColumns]),
            new LsqFitParams(_bYs, new float[nrColumns])
        ];

        await Parallel.ForEachAsync(matrices, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount
        },
        async (@params, ct) =>
        {
            await Task.Run(() =>
            {
                var tempMatrix = new float[nrColumns, nrEquations];

                Buffer.BlockCopy(_aXYs, 0, tempMatrix, 0, _aXYs.Length * sizeof(float));

                DoLsqFit(@params.BMatrix, @params.XMatrix, tempMatrix);
            }, ct);
        });

        return new Matrix3x2(
            matrices[0].XMatrix[0], matrices[0].XMatrix[1],
            matrices[1].XMatrix[0], matrices[1].XMatrix[1],
            matrices[0].XMatrix[2], matrices[1].XMatrix[2]
        );
    }

    record LsqFitParams(float[] BMatrix, float[] XMatrix);

    private void DoLsqFit(float[] bMatrix, float[] xMatrix, float[,] tempMatrix)
    {
        int nrEquations = _aXYs.GetLength(1);
        int nrColumns = _aXYs.GetLength(0);

        const float tiny = 1E-10f;
        for (int j = 0; j < nrColumns; j++)
        {
            for (int i = j + 1; i < nrEquations; i++)
            {
                if (tempMatrix[j, i] != 0)
                {
                    // calculate p, q and new temp_matrix[j,j]; set temp_matrix[j,i]=0
                    double p, q, h;
                    if (MathF.Abs(tempMatrix[j, j]) < tiny * MathF.Abs(tempMatrix[j, i]))
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
                        h = MathF.Sqrt(tempMatrix[j, j] * tempMatrix[j, j] + tempMatrix[j, i] * tempMatrix[j, i]);
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
            if (MathF.Abs(tempMatrix[i, i]) > 1E-30f)
            {
                xMatrix[i] = (float)(h / tempMatrix[i, i]);
            }
            else
            {
                // avoid dividing by 0
                return;
            }
        }
    }
}