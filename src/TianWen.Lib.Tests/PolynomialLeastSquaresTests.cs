using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class PolynomialLeastSquaresTests
{
    /// <summary>
    /// Recovers known coefficients from a tall, well-conditioned design matrix
    /// (univariate cubic): synthesise rhs from random coefficients + small noise,
    /// fit, assert the recovered coefficients are close.
    /// </summary>
    [Fact]
    public void GivenCubicWithNoiseThenRecoversCoefficients()
    {
        // y = 0.5 + 2.0 x − 1.25 x² + 0.75 x³ at x ∈ [-1, 1], 50 samples + small noise.
        var rng = new Random(42);
        const int rows = 50;
        const int cols = 4;
        var design = new double[rows, cols];
        var rhs = new double[rows];
        var expected = new[] { 0.5, 2.0, -1.25, 0.75 };
        for (var r = 0; r < rows; r++)
        {
            var x = -1.0 + 2.0 * r / (rows - 1);
            double y = 0;
            for (var c = 0; c < cols; c++)
            {
                var xp = Math.Pow(x, c);
                design[r, c] = xp;
                y += expected[c] * xp;
            }
            // ±1e-6 noise to ensure we exercise the LS estimator rather than
            // a direct solve.
            rhs[r] = y + (rng.NextDouble() - 0.5) * 2e-6;
        }

        var fit = PolynomialLeastSquares.Solve(design, rhs);
        fit.ShouldNotBeNull();
        fit.Length.ShouldBe(cols);
        for (var c = 0; c < cols; c++)
        {
            fit[c].ShouldBeInRange(expected[c] - 1e-5, expected[c] + 1e-5);
        }
    }

    /// <summary>
    /// Exact zero residual when system is square and well-conditioned.
    /// </summary>
    [Fact]
    public void GivenSquareSystemThenReturnsExactSolution()
    {
        // 3 × 3 identity-ish system with known unique solution.
        var design = new double[,]
        {
            { 1, 0, 0 },
            { 0, 2, 0 },
            { 0, 0, 3 },
        };
        var rhs = new double[] { 4, 6, 12 };

        var fit = PolynomialLeastSquares.Solve(design, rhs);
        fit.ShouldNotBeNull();
        fit[0].ShouldBe(4.0, 1e-12);
        fit[1].ShouldBe(3.0, 1e-12);
        fit[2].ShouldBe(4.0, 1e-12);
    }

    /// <summary>
    /// Rank-deficient design (column 1 is twice column 0) → AᵀA is singular.
    /// Solver should return null rather than throw or emit NaN.
    /// </summary>
    [Fact]
    public void GivenRankDeficientDesignThenReturnsNull()
    {
        var design = new double[,]
        {
            { 1, 2 },
            { 2, 4 },
            { 3, 6 },
            { 4, 8 },
        };
        var rhs = new double[] { 1, 2, 3, 4 };

        var fit = PolynomialLeastSquares.Solve(design, rhs);
        fit.ShouldBeNull();
    }

    /// <summary>
    /// Fewer rows than columns is unsolvable; return null.
    /// </summary>
    [Fact]
    public void GivenUnderdeterminedSystemThenReturnsNull()
    {
        var design = new double[,]
        {
            { 1, 0, 0 },
            { 0, 1, 0 },
        };
        var rhs = new double[] { 1, 2 };

        PolynomialLeastSquares.Solve(design, rhs).ShouldBeNull();
    }

    /// <summary>
    /// Two-variable polynomial fit — this is the shape SipPolynomial.Fit will
    /// build. Round-trip 6 known coefficients (i+j ≤ 2 over 100 random samples).
    /// </summary>
    [Fact]
    public void GivenBivariatePolynomialThenRecoversCoefficients()
    {
        // f(u, v) = 1 + 2u + 3v + 0.5 u² + 0.25 uv − 0.1 v²
        var expected = new[] { 1.0, 2.0, 3.0, 0.5, 0.25, -0.1 };
        var rng = new Random(7);
        const int rows = 100;
        const int cols = 6;
        var design = new double[rows, cols];
        var rhs = new double[rows];
        for (var r = 0; r < rows; r++)
        {
            var u = (rng.NextDouble() - 0.5) * 2.0;  // u ∈ [-1, 1]
            var v = (rng.NextDouble() - 0.5) * 2.0;
            var basis = new[] { 1.0, u, v, u * u, u * v, v * v };
            double y = 0;
            for (var c = 0; c < cols; c++)
            {
                design[r, c] = basis[c];
                y += expected[c] * basis[c];
            }
            rhs[r] = y;
        }

        var fit = PolynomialLeastSquares.Solve(design, rhs);
        fit.ShouldNotBeNull();
        for (var c = 0; c < cols; c++)
        {
            fit[c].ShouldBe(expected[c], 1e-10);
        }
    }
}
