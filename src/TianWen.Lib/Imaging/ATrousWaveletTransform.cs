using System;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

/// <summary>
/// The "a trous" (with holes) / starlet wavelet transform with a B3-spline scaling function -- the
/// multi-scale decomposition Registax/AstroSurface use for planetary sharpening. A plane is split into
/// <c>scaleCount</c> detail layers (finest first) plus a smooth residual. Each successive approximation is
/// the previous one convolved with the B3 kernel dilated by 2^j (the "holes"), and the detail at scale j is
/// the difference of consecutive approximations. The layers sum back to the original exactly
/// (<c>c0 = residual + sum(detail)</c>), so reconstruction with per-layer gains is a linear, artefact-
/// controlled sharpener. Boundary handling is mirror (reflect), which keeps the kernel unit-sum at the edge.
/// </summary>
public static class ATrousWaveletTransform
{
    // B3-spline (cubic) 1D scaling kernel; the 2D kernel is its separable (outer) product. Sums to 1.
    private const float K0 = 1f / 16f;  // taps at +/-2*step
    private const float K1 = 4f / 16f;  // taps at +/-1*step
    private const float K2 = 6f / 16f;  // centre tap

    /// <summary>
    /// Decomposes a single <paramref name="width"/> x <paramref name="height"/> plane (row-major) into
    /// <paramref name="scaleCount"/> detail layers plus a residual. The detail layers are ordered finest
    /// first (scale 0 = the highest spatial frequencies).
    /// </summary>
    public static WaveletDecomposition Decompose(ReadOnlySpan<float> plane, int width, int height, int scaleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(scaleCount);
        if (plane.Length != width * height)
        {
            throw new ArgumentException($"Plane length {plane.Length} does not match {width}x{height}.", nameof(plane));
        }

        // c holds the current approximation c_j; next receives the smoother c_{j+1}; scratch is the
        // horizontal-pass intermediate. Buffers are swapped between scales so we allocate only three.
        var c = plane.ToArray();
        var next = new float[plane.Length];
        var scratch = new float[plane.Length];
        var detail = new float[scaleCount][];

        for (var j = 0; j < scaleCount; j++)
        {
            var step = 1 << j;
            ConvolveSeparable(c, next, scratch, width, height, step);

            var d = new float[plane.Length];
            for (var i = 0; i < d.Length; i++)
            {
                d[i] = c[i] - next[i];
            }

            detail[j] = d;
            (c, next) = (next, c); // c_{j+1} becomes the input for the next scale; reuse the old buffer
        }

        // c is now the residual c_N (the smoothest approximation left after all detail was peeled off).
        return new WaveletDecomposition(detail, c, width, height);
    }

    /// <summary>
    /// Separable B3 convolution with a hole step (dilation): horizontal pass <paramref name="src"/> -&gt;
    /// <paramref name="scratch"/>, vertical pass <paramref name="scratch"/> -&gt; <paramref name="dst"/>.
    /// Mirror (reflect-101) boundary. Arrays are flat row-major of length width*height.
    /// </summary>
    internal static void ConvolveSeparable(float[] src, float[] dst, float[] scratch, int width, int height, int step)
    {
        var s2 = step * 2;

        // Horizontal pass.
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var xm2 = Reflect(x - s2, width);
                var xm1 = Reflect(x - step, width);
                var xp1 = Reflect(x + step, width);
                var xp2 = Reflect(x + s2, width);
                scratch[row + x] =
                    (K0 * (src[row + xm2] + src[row + xp2]))
                    + (K1 * (src[row + xm1] + src[row + xp1]))
                    + (K2 * src[row + x]);
            }
        });

        // Vertical pass.
        Parallel.For(0, height, y =>
        {
            var ym2 = Reflect(y - s2, height) * width;
            var ym1 = Reflect(y - step, height) * width;
            var yp1 = Reflect(y + step, height) * width;
            var yp2 = Reflect(y + s2, height) * width;
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                dst[row + x] =
                    (K0 * (scratch[ym2 + x] + scratch[yp2 + x]))
                    + (K1 * (scratch[ym1 + x] + scratch[yp1 + x]))
                    + (K2 * scratch[row + x]);
            }
        });
    }

    // Reflect-101 index mirror (a[-1]=a[1], a[n]=a[n-2]); loops to cover large dilation steps near small dims.
    private static int Reflect(int i, int n)
    {
        if (n == 1)
        {
            return 0;
        }

        while (i < 0 || i >= n)
        {
            if (i < 0)
            {
                i = -i;
            }

            if (i >= n)
            {
                i = (2 * n) - 2 - i;
            }
        }

        return i;
    }
}
