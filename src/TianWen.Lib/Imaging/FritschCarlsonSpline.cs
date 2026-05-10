using System;
using System.Collections.Immutable;

namespace TianWen.Lib.Imaging;

/// <summary>
/// Monotonic cubic Hermite spline using the Fritsch-Carlson method.
/// Guarantees the interpolant preserves monotonicity of the control points.
/// </summary>
public readonly struct FritschCarlsonSpline
{
    private readonly ImmutableArray<float> _x;
    private readonly ImmutableArray<float> _y;
    private readonly ImmutableArray<float> _m; // slopes

    public FritschCarlsonSpline(ImmutableArray<(float X, float Y)> controlPoints)
    {
        if (controlPoints.Length < 2)
        {
            throw new ArgumentException("Need at least 2 control points", nameof(controlPoints));
        }

        var n = controlPoints.Length;
        var x = new float[n];
        var y = new float[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = Math.Clamp(controlPoints[i].X, 0f, 1f);
            y[i] = Math.Clamp(controlPoints[i].Y, 0f, 1f);
            if (i > 0 && x[i] <= x[i - 1])
            {
                throw new ArgumentException($"Control points must have strictly increasing X values (point {i}: X={x[i]})", nameof(controlPoints));
            }
        }

        _x = [.. x];
        _y = [.. y];

        // Compute secant slopes
        var d = new float[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            d[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);
        }

        // Fritsch-Carlson slope computation
        var m = new float[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];

        for (var i = 1; i < n - 1; i++)
        {
            if (d[i - 1] * d[i] <= 0)
            {
                m[i] = 0;
            }
            else
            {
                // Weighted harmonic mean of adjacent secant slopes
                var h0 = x[i] - x[i - 1];
                var h1 = x[i + 1] - x[i];
                var w0 = h0 + 2 * h1;
                var w1 = 2 * h0 + h1;
                m[i] = (w0 + w1) / (w0 / d[i - 1] + w1 / d[i]);
            }
        }

        _m = [.. m];
    }

    /// <summary>Identity spline: evaluate(x) == x.</summary>
    public static FritschCarlsonSpline Identity { get; } = new([(0f, 0f), (1f, 1f)]);

    /// <summary>Evaluates the spline at x in [0, 1].</summary>
    public float Evaluate(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        var n = _x.Length;

        // Find segment: binary search
        var lo = 0;
        var hi = n - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (_x[mid] <= x) lo = mid; else hi = mid;
        }

        var i = lo;
        if (i >= n - 1) return _y[n - 1];

        var h = _x[i + 1] - _x[i];
        if (h <= 0) return _y[i];

        var t = (x - _x[i]) / h;
        var t2 = t * t;
        var t3 = t2 * t;

        var a = 2f * t3 - 3f * t2 + 1f;
        var b = t3 - 2f * t2 + t;
        var c = -2f * t3 + 3f * t2;
        var d = t3 - t2;

        return a * _y[i] + b * h * _m[i] + c * _y[i + 1] + d * h * _m[i + 1];
    }

    /// <summary>Pre-computes a uniformly-sampled LUT of <paramref name="size"/> entries.</summary>
    public ImmutableArray<float> ComputeLUT(int size = 1024)
    {
        var builder = ImmutableArray.CreateBuilder<float>(size);
        for (var i = 0; i < size; i++)
        {
            builder.Add(Evaluate(i / (float)(size - 1)));
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Pre-computes 33 uniformly-spaced control knots at <c>i/32</c> for <c>i = 0..32</c>.
    /// Both <see cref="Image.ApplyCurveLut"/> (CPU) and the GLSL <c>applyCurveLUT</c> function
    /// expect this exact 33-knot layout: the divisor is 32 (= lut.Length - 1) so a normalized
    /// input <c>v in [0, 1]</c> maps to <c>idx = v * 32</c>. The GPU packs these 33 floats into
    /// 9 std140 vec4 slots (= 36 floats); the trailing 3 slots are left at zero by the UBO
    /// upload loop and never read by the shader.
    /// </summary>
    public ImmutableArray<float> ComputeKnots33()
    {
        var builder = ImmutableArray.CreateBuilder<float>(33);
        for (var i = 0; i < 33; i++)
        {
            builder.Add(Evaluate(i / 32f));
        }
        return builder.MoveToImmutable();
    }
}
