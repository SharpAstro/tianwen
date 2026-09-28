using Shouldly;
using System;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Image.SeparableGaussianBlur"/> was rewritten for speed (parallel rows, a tap added across a whole row
/// at a time, vectorised) on the promise that every output value is bit for bit the plain two-loop blur's. That is
/// the promise the noise estimator's validated numbers rest on (docs/plans/denoiser-training.md, "the noise
/// estimator against the half pairs"), so it is checked here against the plain loop itself, kept verbatim below,
/// on the serial path and the parallel one, on frames narrower than the kernel, and with NaN-free and extreme data.
/// </summary>
[Collection("Imaging")]
public class GaussianBlurParityTests
{
    [Theory]
    [InlineData(1, 1, 4f)]
    [InlineData(5, 3, 4f)]       // narrower and shorter than the 13-tap radius: every tap clamps
    [InlineData(37, 29, 2f)]
    [InlineData(256, 256, 2f)]   // a tile's plane, the serial path
    [InlineData(256, 256, 4f)]
    [InlineData(613, 487, 4f)]   // over the parallel threshold, widths not a multiple of any vector length
    [InlineData(1024, 257, 0.5f)]
    [InlineData(700, 400, 7.3f)]
    public void TheBlurIsBitForBitThePlainLoop(int w, int h, float sigma)
    {
        var rng = new Random((w * 7919) + h);
        var src = new float[w * h];
        for (var i = 0; i < src.Length; i++)
        {
            // Mostly a stretched sky, with the occasional star, a few exact zeros and negative excursions.
            var r = rng.NextDouble();
            src[i] = r < 0.01 ? (float)(rng.NextDouble() * 50.0) : r < 0.02 ? 0f : (float)((rng.NextDouble() * 0.4) - 0.05);
        }

        var fast = Image.SeparableGaussianBlur(src, w, h, sigma);
        var plain = PlainLoopBlur(src, w, h, sigma);

        fast.Length.ShouldBe(plain.Length);
        var mismatches = 0;
        for (var i = 0; i < fast.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(fast[i]) != BitConverter.SingleToInt32Bits(plain[i]))
            {
                mismatches++;
            }
        }
        mismatches.ShouldBe(0, $"{w}x{h} sigma {sigma}");
    }

    /// <summary>The blur as it was before the rewrite, verbatim: the reference, not a second implementation.</summary>
    internal static float[] PlainLoopBlur(float[] src, int w, int h, float sigma)
    {
        var radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3f));
        var kernel = new float[radius * 2 + 1];
        var twoSigmaSq = 2f * sigma * sigma;
        var norm = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var k = MathF.Exp(-i * i / twoSigmaSq);
            kernel[i + radius] = k;
            norm += k;
        }
        var inv = 1f / norm;
        for (var i = 0; i < kernel.Length; i++) kernel[i] *= inv;

        var tmp = new float[src.Length];
        for (var y = 0; y < h; y++)
        {
            var rowBase = y * w;
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var t = -radius; t <= radius; t++)
                {
                    var sx = x + t;
                    if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
                    sum += src[rowBase + sx] * kernel[t + radius];
                }
                tmp[rowBase + x] = sum;
            }
        }

        var dst = new float[src.Length];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0f;
                for (var t = -radius; t <= radius; t++)
                {
                    var sy = y + t;
                    if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
                    sum += tmp[sy * w + x] * kernel[t + radius];
                }
                dst[y * w + x] = sum;
            }
        }
        return dst;
    }
}
