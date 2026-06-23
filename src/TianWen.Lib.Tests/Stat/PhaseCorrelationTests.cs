using System;
using Shouldly;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

public class PhaseCorrelationTests
{
    // A deterministic textured field (broadband, so phase correlation has a sharp peak).
    private static float[] Texture(int w, int h)
    {
        var f = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                f[(y * w) + x] = (float)((Math.Sin(x * 0.7) * Math.Cos(y * 0.5)) + (Math.Sin((x + y) * 0.3) * 0.5) + 1.0);
            }
        }

        return f;
    }

    private static float[] CircularShift(float[] src, int w, int h, int sx, int sy)
    {
        var dst = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var srcX = (((x - sx) % w) + w) % w;
                var srcY = (((y - sy) % h) + h) % h;
                dst[(y * w) + x] = src[(srcY * w) + srcX];
            }
        }

        return dst;
    }

    private static float[] Gaussian(int w, int h, double cx, double cy, double sigma)
    {
        var f = new float[w * h];
        var twoSigma2 = 2.0 * sigma * sigma;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d2 = ((x - cx) * (x - cx)) + ((y - cy) * (y - cy));
                f[(y * w) + x] = (float)Math.Exp(-d2 / twoSigma2);
            }
        }

        return f;
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(-2, 5)]
    [InlineData(0, 0)]
    [InlineData(7, -3)]
    public void Recovers_integer_circular_shift_exactly(int sx, int sy)
    {
        const int w = 16, h = 16;
        var reference = Texture(w, h);
        var moving = CircularShift(reference, w, h, sx, sy);

        // moving(x,y) = reference(x - sx, y - sy) -> Dx == sx, Dy == sy. Window OFF: the shift is circular.
        var shift = PhaseCorrelation.Estimate(reference, moving, w, h, applyWindow: false);

        shift.Dx.ShouldBe(sx, 0.01);
        shift.Dy.ShouldBe(sy, 0.01);
    }

    [Fact]
    public void Recovers_subpixel_shift_within_tolerance()
    {
        const int w = 32, h = 32;
        const double sx = 0.4, sy = -0.3;
        var reference = Gaussian(w, h, 16.0, 16.0, 2.5);
        var moving = Gaussian(w, h, 16.0 + sx, 16.0 + sy, 2.5);

        var shift = PhaseCorrelation.Estimate(reference, moving, w, h, applyWindow: false);

        shift.Dx.ShouldBe(sx, 0.12);
        shift.Dy.ShouldBe(sy, 0.12);
    }

    [Fact]
    public void Rejects_non_power_of_two_tile()
    {
        Should.Throw<ArgumentException>(() => PhaseCorrelation.Estimate(new float[6 * 4], new float[6 * 4], 6, 4));
    }
}
