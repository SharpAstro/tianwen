using System;

namespace TianWen.Lib.Stat;

/// <summary>
/// The straight line <c>y = offset + slope x</c> with the least sum of absolute deviations: the fit PixInsight's
/// <c>pcl::LinearFit</c> makes ("minimizes mean absolute deviation"), which a star or a gradient's far end moves far less
/// than it moves least squares.
/// </summary>
/// <remarks>
/// For a given slope the best offset is the median of <c>y - slope x</c>, and what remains is convex in the slope, so a
/// bracket grown from a start and a golden-section search find the minimum exactly. Each step takes one median of the
/// whole sample, so a caller holding millions of pixels samples them first.
/// </remarks>
public static class LeastAbsoluteDeviation
{
    /// <summary>The fitted line and its mean absolute deviation over the points.</summary>
    public readonly record struct Line(double Slope, double Offset, double MeanAbsoluteDeviation);

    /// <summary>
    /// The least-absolute-deviation line through (<paramref name="x"/>, <paramref name="y"/>), searched from
    /// <paramref name="startSlope"/>. NaN with fewer than three points.
    /// </summary>
    public static Line Fit(float[] x, float[] y, double startSlope)
    {
        if (x.Length != y.Length)
        {
            throw new ArgumentException("x and y must be the same length", nameof(y));
        }
        if (x.Length < 3)
        {
            return new Line(double.NaN, double.NaN, double.NaN);
        }
        var scratch = new float[x.Length];
        var start = double.IsFinite(startSlope) ? startSlope : 0;

        // A bracket grown on each side until the objective rises there.
        var step = Math.Max(Math.Abs(start), 1e-6) * 0.5;
        var lo = start - step;
        var hi = start + step;
        for (var grow = 0; grow < 60 && Objective(lo) < Objective(start); grow++)
        {
            step *= 2;
            lo = start - step;
        }
        step = Math.Max(Math.Abs(start), 1e-6) * 0.5;
        for (var grow = 0; grow < 60 && Objective(hi) < Objective(start); grow++)
        {
            step *= 2;
            hi = start + step;
        }

        // Golden section over [lo, hi]: the objective is convex, so it narrows onto the one minimum.
        var ratio = (Math.Sqrt(5.0) - 1) / 2;
        var a = hi - (ratio * (hi - lo));
        var b = lo + (ratio * (hi - lo));
        var fa = Objective(a);
        var fb = Objective(b);
        for (var iteration = 0; iteration < 80 && hi - lo > 1e-9 * Math.Max(1.0, Math.Abs(hi)); iteration++)
        {
            if (fa <= fb)
            {
                hi = b;
                b = a;
                fb = fa;
                a = hi - (ratio * (hi - lo));
                fa = Objective(a);
            }
            else
            {
                lo = a;
                a = b;
                fa = fb;
                b = lo + (ratio * (hi - lo));
                fb = Objective(b);
            }
        }
        var slope = (lo + hi) / 2;
        var sum = Objective(slope);
        return new Line(slope, OffsetAt(slope), sum / x.Length);

        // The median of y - slope x: the offset that suits this slope.
        double OffsetAt(double s)
        {
            for (var i = 0; i < x.Length; i++)
            {
                scratch[i] = (float)(y[i] - (s * x[i]));
            }
            return StatisticsHelper.MedianFast(scratch);
        }

        // sum |y - s x - median(y - s x)|: the least absolute deviation at this slope with the offset that suits it.
        double Objective(double s)
        {
            var offset = OffsetAt(s);
            double total = 0;
            for (var i = 0; i < x.Length; i++)
            {
                total += Math.Abs(y[i] - (s * x[i]) - offset);
            }
            return total;
        }
    }
}
