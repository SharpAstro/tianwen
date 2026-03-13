using System;
using System.Collections.Generic;

namespace TianWen.Lib.Stat;

/// <summary>
/// Catmull-Rom spline interpolation for smooth curves through a sequence of points.
/// Converts control points to cubic Bezier segments for rendering.
/// </summary>
public static class CatmullRomSpline
{
    /// <summary>
    /// Generates interpolated points along a Catmull-Rom spline that passes through all input points.
    /// </summary>
    /// <param name="points">Input points (minimum 2). Must be ordered (e.g., by X).</param>
    /// <param name="segmentsPerSpan">Number of interpolated points between each pair of input points.</param>
    /// <param name="alpha">Parameterization: 0 = uniform, 0.5 = centripetal (recommended), 1 = chordal.</param>
    /// <returns>Smoothly interpolated points including the original endpoints.</returns>
    public static (double X, double Y)[] Interpolate(
        ReadOnlySpan<(double X, double Y)> points,
        int segmentsPerSpan = 16,
        double alpha = 0.5)
    {
        if (points.Length < 2)
        {
            return points.Length == 1
                ? [(points[0].X, points[0].Y)]
                : [];
        }

        if (points.Length == 2)
        {
            return [(points[0].X, points[0].Y), (points[1].X, points[1].Y)];
        }

        var result = new List<(double X, double Y)>((points.Length - 1) * segmentsPerSpan + 1);

        for (var i = 0; i < points.Length - 1; i++)
        {
            // Catmull-Rom needs 4 points: P0, P1, P2, P3
            // For the first/last segment, mirror the endpoint to create a virtual point
            var p0 = i > 0 ? points[i - 1] : Mirror(points[0], points[1]);
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i + 2 < points.Length ? points[i + 2] : Mirror(points[^1], points[^2]);

            var tCount = i < points.Length - 2 ? segmentsPerSpan : segmentsPerSpan + 1;
            for (var s = 0; s < tCount; s++)
            {
                var t = (double)s / segmentsPerSpan;
                result.Add(EvaluateCentripetal(p0, p1, p2, p3, t, alpha));
            }
        }

        return [.. result];
    }

    /// <summary>
    /// Converts a Catmull-Rom spline through the given points into cubic Bezier control points.
    /// Each span between adjacent input points produces 4 Bezier control points (start, cp1, cp2, end).
    /// </summary>
    /// <param name="points">Input points (minimum 2).</param>
    /// <returns>
    /// Array of Bezier segments. Each segment is (P0, C1, C2, P3) where P0/P3 are on-curve
    /// and C1/C2 are off-curve control points.
    /// </returns>
    public static ((double X, double Y) P0, (double X, double Y) C1, (double X, double Y) C2, (double X, double Y) P3)[] ToCubicBezierSegments(
        ReadOnlySpan<(double X, double Y)> points)
    {
        if (points.Length < 2)
        {
            return [];
        }

        var segments = new ((double, double), (double, double), (double, double), (double, double))[points.Length - 1];

        for (var i = 0; i < points.Length - 1; i++)
        {
            var p0 = i > 0 ? points[i - 1] : Mirror(points[0], points[1]);
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = i + 2 < points.Length ? points[i + 2] : Mirror(points[^1], points[^2]);

            // Catmull-Rom to cubic Bezier conversion:
            // C1 = P1 + (P2 - P0) / 6
            // C2 = P2 - (P3 - P1) / 6
            var c1 = (p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = (p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);

            segments[i] = (p1, c1, c2, p2);
        }

        return segments;
    }

    /// <summary>
    /// Mirrors point <paramref name="a"/> across point <paramref name="b"/>.
    /// Used to create virtual control points at the start/end of the spline.
    /// </summary>
    private static (double X, double Y) Mirror((double X, double Y) a, (double X, double Y) b)
        => (2 * a.X - b.X, 2 * a.Y - b.Y);

    /// <summary>
    /// Evaluates a centripetal Catmull-Rom spline at parameter t in [0, 1] between P1 and P2.
    /// </summary>
    private static (double X, double Y) EvaluateCentripetal(
        (double X, double Y) p0,
        (double X, double Y) p1,
        (double X, double Y) p2,
        (double X, double Y) p3,
        double t,
        double alpha)
    {
        static double Knot(double ti, (double X, double Y) a, (double X, double Y) b, double alpha)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            return ti + Math.Pow(dx * dx + dy * dy, alpha * 0.5);
        }

        var t0 = 0.0;
        var t1 = Knot(t0, p0, p1, alpha);
        var t2 = Knot(t1, p1, p2, alpha);
        var t3 = Knot(t2, p2, p3, alpha);

        // Remap t from [0,1] to [t1,t2]
        var tt = t1 + t * (t2 - t1);

        // Barry and Goldman's pyramidal formulation
        var a1 = Lerp(p0, p1, t0, t1, tt);
        var a2 = Lerp(p1, p2, t1, t2, tt);
        var a3 = Lerp(p2, p3, t2, t3, tt);

        var b1 = Lerp(a1, a2, t0, t2, tt);
        var b2 = Lerp(a2, a3, t1, t3, tt);

        return Lerp(b1, b2, t1, t2, tt);
    }

    private static (double X, double Y) Lerp(
        (double X, double Y) a,
        (double X, double Y) b,
        double ta,
        double tb,
        double t)
    {
        var denom = tb - ta;
        if (Math.Abs(denom) < 1e-10)
        {
            return a;
        }

        var f = (t - ta) / denom;
        return (a.X + f * (b.X - a.X), a.Y + f * (b.Y - a.Y));
    }
}
