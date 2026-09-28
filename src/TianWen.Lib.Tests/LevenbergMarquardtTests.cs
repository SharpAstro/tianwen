using System;
using Shouldly;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

public class LevenbergMarquardtTests
{
    [Fact]
    public void RosenbrocksValleyIsFollowedToItsMinimum()
    {
        // r = (10 (y - x^2), 1 - x): a curved valley whose floor a Gauss-Newton step overshoots; the minimum is (1, 1).
        var fit = LevenbergMarquardt.Fit([-1.2, 1.0], 2, static (p, r) =>
        {
            r[0] = 10 * (p[1] - (p[0] * p[0]));
            r[1] = 1 - p[0];
        }, [1e-7, 1e-7]);

        fit.Converged.ShouldBeTrue();
        fit.Parameters[0].ShouldBe(1, 1e-6);
        fit.Parameters[1].ShouldBe(1, 1e-6);
    }

    [Fact]
    public void ADecaysParametersComeBackWithinTheirStandardErrors()
    {
        // y = 5 exp(-t / 3) + 0.5 with Gaussian noise of 0.05: the fit recovers each parameter within three of its own
        // standard errors, and the errors are of the size the noise implies (not zero, not the parameter's size).
        var rng = new Random(11);
        var t = new double[200];
        var y = new double[200];
        for (var i = 0; i < t.Length; i++)
        {
            t[i] = i * 0.1;
            var u1 = 1 - rng.NextDouble();
            var u2 = rng.NextDouble();
            y[i] = (5 * Math.Exp(-t[i] / 3)) + 0.5 + (0.05 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        var fit = LevenbergMarquardt.Fit([1, 1, 0], t.Length, (p, r) =>
        {
            for (var i = 0; i < t.Length; i++)
            {
                r[i] = (p[0] * Math.Exp(-t[i] / p[1])) + p[2] - y[i];
            }
        }, [1e-6, 1e-6, 1e-6]);

        fit.Converged.ShouldBeTrue();
        double[] truth = [5, 3, 0.5];
        for (var k = 0; k < 3; k++)
        {
            Math.Abs(fit.Parameters[k] - truth[k]).ShouldBeLessThan(3 * fit.StandardErrors[k], $"parameter {k}");
            fit.StandardErrors[k].ShouldBeInRange(1e-4, 0.2, $"parameter {k}'s standard error");
        }
        Math.Sqrt(2 * fit.Cost / t.Length).ShouldBe(0.05, 0.01, "the residuals' scatter is the noise's");
    }
}
