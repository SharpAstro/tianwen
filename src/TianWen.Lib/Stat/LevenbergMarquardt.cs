using System;

namespace TianWen.Lib.Stat;

/// <summary>What a Levenberg-Marquardt fit came to.</summary>
/// <param name="Parameters">The fitted parameters.</param>
/// <param name="Cost">Half the sum of squared residuals at <see cref="Parameters"/>.</param>
/// <param name="StandardErrors">Each parameter's standard error, from the Jacobian at the solution and the residuals'
/// own scatter (NaN where the normal matrix is singular).</param>
/// <param name="Iterations">Iterations taken.</param>
/// <param name="Converged">True when a step changed the cost by less than the tolerance; false when the iteration budget
/// ran out first.</param>
public readonly record struct LevenbergMarquardtResult(double[] Parameters, double Cost, double[] StandardErrors, int Iterations, bool Converged);

/// <summary>
/// Non-linear least squares by Levenberg-Marquardt: minimises half the sum of squares of a residual vector over a handful
/// of parameters, with a forward-difference Jacobian. For a model of tens of parameters over up to a few hundred thousand
/// residuals (a planet's limb, a seeing model, a blur kernel), where each residual evaluation is the costly part.
/// </summary>
public static class LevenbergMarquardt
{
    /// <summary>
    /// Fits <paramref name="initial"/> by writing the residuals of a parameter vector into the span
    /// <paramref name="residuals"/> fills: <c>residuals(parameters, destination)</c>, where <c>destination</c> has
    /// <paramref name="residualCount"/> elements.
    /// </summary>
    /// <param name="step">Each parameter's finite-difference step for the Jacobian: about a hundredth of the precision it is
    /// wanted to (a pixel's thousandth for a position), so the model is linear across it.</param>
    public static LevenbergMarquardtResult Fit(ReadOnlySpan<double> initial, int residualCount, ResidualFunction residuals,
        ReadOnlySpan<double> step, int maxIterations = 100, double relativeTolerance = 1e-10)
    {
        var n = initial.Length;
        if (step.Length != n)
        {
            throw new ArgumentException($"{step.Length} steps for {n} parameters", nameof(step));
        }
        var p = initial.ToArray();
        var trial = new double[n];
        var r = new double[residualCount];
        var rTrial = new double[residualCount];
        var jacobian = new double[residualCount * n];
        var normal = new double[n * n];
        var gradient = new double[n];
        var delta = new double[n];

        residuals(p, r);
        var cost = HalfSumOfSquares(r);
        var lambda = 1e-3;
        var converged = false;
        var iteration = 0;
        for (; iteration < maxIterations; iteration++)
        {
            Jacobian(p, r, residuals, step, jacobian, rTrial);
            Normal(jacobian, r, residualCount, n, normal, gradient);

            var improved = false;
            while (lambda < 1e12)
            {
                if (!Solve(normal, gradient, lambda, n, delta))
                {
                    lambda *= 10;
                    continue;
                }
                for (var k = 0; k < n; k++)
                {
                    trial[k] = p[k] - delta[k];
                }
                residuals(trial, rTrial);
                var trialCost = HalfSumOfSquares(rTrial);
                if (trialCost < cost)
                {
                    var change = cost - trialCost;
                    Array.Copy(trial, p, n);
                    Array.Copy(rTrial, r, residualCount);
                    cost = trialCost;
                    lambda = Math.Max(lambda / 10, 1e-12);
                    improved = true;
                    converged = change <= relativeTolerance * Math.Max(cost, double.Epsilon);
                    break;
                }
                lambda *= 10;
            }
            if (!improved || converged)
            {
                converged = true;
                iteration++;
                break;
            }
        }

        Jacobian(p, r, residuals, step, jacobian, rTrial);
        Normal(jacobian, r, residualCount, n, normal, gradient);
        return new LevenbergMarquardtResult(p, cost, StandardErrors(normal, cost, residualCount, n), iteration, converged);
    }

    /// <summary>Writes the residuals of <c>parameters</c> into <c>destination</c>.</summary>
    public delegate void ResidualFunction(ReadOnlySpan<double> parameters, Span<double> destination);

    private static double HalfSumOfSquares(ReadOnlySpan<double> r)
    {
        var sum = 0.0;
        foreach (var v in r)
        {
            sum += v * v;
        }
        return 0.5 * sum;
    }

    // Forward differences: column k is (r(p + h_k e_k) - r(p)) / h_k. p is the fit's own vector, so each parameter is moved in
    // place and its exact value put back: the residuals see what a shifted copy would have shown them.
    private static void Jacobian(double[] p, double[] r, ResidualFunction residuals, ReadOnlySpan<double> step, double[] jacobian, double[] scratch)
    {
        var n = p.Length;
        var m = r.Length;
        for (var k = 0; k < n; k++)
        {
            var h = step[k];
            var held = p[k];
            p[k] = held + h;
            residuals(p, scratch);
            p[k] = held;
            for (var i = 0; i < m; i++)
            {
                jacobian[(i * n) + k] = (scratch[i] - r[i]) / h;
            }
        }
    }

    // J^T J and J^T r.
    private static void Normal(double[] jacobian, double[] r, int m, int n, double[] normal, double[] gradient)
    {
        Array.Clear(normal);
        Array.Clear(gradient);
        for (var i = 0; i < m; i++)
        {
            var row = i * n;
            for (var a = 0; a < n; a++)
            {
                var ja = jacobian[row + a];
                if (ja == 0)
                {
                    continue;
                }
                gradient[a] += ja * r[i];
                for (var b = a; b < n; b++)
                {
                    normal[(a * n) + b] += ja * jacobian[row + b];
                }
            }
        }
        for (var a = 0; a < n; a++)
        {
            for (var b = 0; b < a; b++)
            {
                normal[(a * n) + b] = normal[(b * n) + a];
            }
        }
    }

    // (J^T J + lambda diag(J^T J)) delta = J^T r, by Cholesky; false when not positive definite.
    private static bool Solve(double[] normal, double[] gradient, double lambda, int n, double[] delta)
    {
        var a = new double[n * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                a[(i * n) + j] = normal[(i * n) + j];
            }
            a[(i * n) + i] += lambda * Math.Max(normal[(i * n) + i], 1e-300);
        }
        return Cholesky(a, n) && Substitute(a, gradient, n, delta);
    }

    private static bool Cholesky(double[] a, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var d = a[(j * n) + j];
            for (var k = 0; k < j; k++)
            {
                d -= a[(j * n) + k] * a[(j * n) + k];
            }
            if (!(d > 0))
            {
                return false;
            }
            var l = Math.Sqrt(d);
            a[(j * n) + j] = l;
            for (var i = j + 1; i < n; i++)
            {
                var s = a[(i * n) + j];
                for (var k = 0; k < j; k++)
                {
                    s -= a[(i * n) + k] * a[(j * n) + k];
                }
                a[(i * n) + j] = s / l;
            }
        }
        return true;
    }

    private static bool Substitute(double[] l, double[] b, int n, double[] x)
    {
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var s = b[i];
            for (var k = 0; k < i; k++)
            {
                s -= l[(i * n) + k] * y[k];
            }
            y[i] = s / l[(i * n) + i];
        }
        for (var i = n - 1; i >= 0; i--)
        {
            var s = y[i];
            for (var k = i + 1; k < n; k++)
            {
                s -= l[(k * n) + i] * x[k];
            }
            x[i] = s / l[(i * n) + i];
        }
        return Array.TrueForAll(x, double.IsFinite);
    }

    // sqrt(diag((J^T J)^-1) * s^2), with s^2 = sum of squares / (m - n). The fit is done with `normal`, so it is factorised in place.
    private static double[] StandardErrors(double[] normal, double cost, int m, int n)
    {
        var errors = new double[n];
        var a = normal;
        if (m <= n || !Cholesky(a, n))
        {
            Array.Fill(errors, double.NaN);
            return errors;
        }
        var variance = 2 * cost / (m - n);
        var unit = new double[n];
        var column = new double[n];
        for (var k = 0; k < n; k++)
        {
            Array.Clear(unit);
            unit[k] = 1;
            errors[k] = Substitute(a, unit, n, column) ? Math.Sqrt(column[k] * variance) : double.NaN;
        }
        return errors;
    }
}
