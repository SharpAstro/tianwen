using System;
using System.Collections.Generic;

namespace TianWen.Lib.Imaging;

/// <summary>
/// The noise left on a plate's SKY at each of the a trous (B3 spline) scales 1, 2, 4, 8, 16 and 32 px, read on its
/// luminance: what a single-pixel estimate (<see cref="Image.EstimateNoiseProfile"/>) cannot see. A denoiser's residual is
/// mostly blotchy large-scale mottle, so a plate it reports 70 % quieter can keep most of its 32 px noise (one NoiseXTerminator
/// pass on the Sculptor master's starless plate: 0.30 of its input at 1 px, 0.63 at 32 px).
/// </summary>
/// <param name="Bands">The robust sigma of each band on the sky, finest first.</param>
/// <param name="SkyPixels">How many sky pixels it was read over.</param>
/// <param name="Sky">Which pixels were sky, so a later read of the same plate (after a denoise pass) is taken on the same
/// pixels (<see cref="Measure(Image, MultiScaleNoise?)"/>).</param>
public sealed record MultiScaleNoise(float[] Bands, int SkyPixels, bool[] Sky)
{
    /// <summary>The scales measured, in pixels, finest first.</summary>
    public static IReadOnlyList<int> Scales { get; } = [1, 2, 4, 8, 16, 32];

    /// <summary>The coarse noise, the 16 and 32 px bands summed: the mottle a denoise pass is judged by.</summary>
    public float Coarse => Bands.Length >= 6 ? Bands[4] + Bands[5] : float.NaN;

    // The sky: a 31 px box mean of the luminance within this many robust sigma of its median, which keeps a galaxy and its
    // halo out, and that far in from anything that is not sky, so a 32 px band read on it does not reach into the subject or
    // the canvas ring. Never a quantile: on a flat sky a quantile threshold cuts the sky in half at random, and the erosion
    // then took all but 134 thousand of the Sculptor's pixels on its noisy plate against 2.9 million on a clean one.
    private const float SkySigma = 3f;
    private const int SkyBoxRadius = 15;
    private const int SkyErosion = 24;

    // Every k-th sky pixel is enough for a median and keeps the read to a fraction of a second on a 9 MP plate.
    private const int SampleStride = 7;

    /// <summary>Measures <paramref name="image"/>'s sky noise per scale on its luminance (Rec.709 for colour), or null when it has
    /// too little sky to read. Given <paramref name="onSkyOf"/>, a measurement of the same plate before a step, it reads the
    /// same pixels, so the two compare.</summary>
    public static MultiScaleNoise? Measure(Image image, MultiScaleNoise? onSkyOf = null)
    {
        var (channels, width, height) = image.Shape;
        if (width < 4 * (SkyErosion + 32) || height < 4 * (SkyErosion + 32))
        {
            return null;
        }
        var lum = Luminance(image, channels, width, height);
        var sky = onSkyOf is { Sky: { } same } && same.Length == lum.Length ? same : SkyMask(lum, width, height);
        var count = 0;
        foreach (var s in sky)
        {
            if (s)
            {
                count++;
            }
        }
        if (count < 10_000)
        {
            return null;
        }

        var bands = new float[Scales.Count];
        var current = lum;
        var smooth = new float[lum.Length];
        var scratch = new float[lum.Length];
        for (var j = 0; j < Scales.Count; j++)
        {
            Smooth(current, smooth, scratch, width, height, 1 << j);
            bands[j] = RobustSigmaOfDifference(current, smooth, sky);
            (current, smooth) = (smooth, current == lum ? new float[lum.Length] : current);
        }
        return new MultiScaleNoise(bands, count, sky);
    }

    private static float[] Luminance(Image image, int channels, int width, int height)
    {
        var lum = new float[width * height];
        if (channels >= 3)
        {
            var r = image.GetChannelSpan(0);
            var g = image.GetChannelSpan(1);
            var b = image.GetChannelSpan(2);
            for (var i = 0; i < lum.Length; i++)
            {
                lum[i] = (0.2126f * r[i]) + (0.7152f * g[i]) + (0.0722f * b[i]);
            }
        }
        else
        {
            image.GetChannelSpan(0).CopyTo(lum);
        }
        return lum;
    }

    private static bool[] SkyMask(float[] lum, int width, int height)
    {
        // A box mean over finite, positive pixels (the canvas ring is exact zero), from one summed-area table.
        var box = BoxMean(lum, width, height, SkyBoxRadius, static v => float.IsFinite(v) && v > 0f ? v : 0f);
        var valid = new List<float>(lum.Length / 4);
        for (var i = 0; i < lum.Length; i += 3)
        {
            if (float.IsFinite(lum[i]) && lum[i] > 0f)
            {
                valid.Add(box[i]);
            }
        }
        if (valid.Count == 0)
        {
            return new bool[lum.Length];
        }
        valid.Sort();
        var median = valid[valid.Count / 2];
        for (var i = 0; i < valid.Count; i++)
        {
            valid[i] = MathF.Abs(valid[i] - median);
        }
        valid.Sort();
        var threshold = median + (SkySigma * 1.4826f * valid[valid.Count / 2]);
        var candidate = new float[lum.Length];
        for (var i = 0; i < lum.Length; i++)
        {
            candidate[i] = float.IsFinite(lum[i]) && lum[i] > 0f && box[i] < threshold ? 1f : 0f;
        }
        // Eroded: a pixel is sky only where its whole neighbourhood is.
        var held = BoxMean(candidate, width, height, SkyErosion, static v => v);
        var sky = new bool[lum.Length];
        for (var i = 0; i < lum.Length; i++)
        {
            sky[i] = held[i] > 0.9999f;
        }
        return sky;
    }

    private static float[] BoxMean(float[] src, int width, int height, int radius, Func<float, float> value)
    {
        var w1 = width + 1;
        var sat = new double[w1 * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = 0.0;
            for (var x = 0; x < width; x++)
            {
                row += value(src[(y * width) + x]);
                sat[((y + 1) * w1) + x + 1] = sat[(y * w1) + x + 1] + row;
            }
        }
        var mean = new float[src.Length];
        for (var y = 0; y < height; y++)
        {
            var (y0, y1) = (Math.Max(0, y - radius), Math.Min(height, y + radius + 1));
            for (var x = 0; x < width; x++)
            {
                var (x0, x1) = (Math.Max(0, x - radius), Math.Min(width, x + radius + 1));
                var sum = sat[(y1 * w1) + x1] - sat[(y0 * w1) + x1] - sat[(y1 * w1) + x0] + sat[(y0 * w1) + x0];
                mean[(y * width) + x] = (float)(sum / ((y1 - y0) * (x1 - x0)));
            }
        }
        return mean;
    }

    // One a trous level: the B3 spline (1 4 6 4 1) / 16 with its taps `step` apart, separable, mirrored at the edges.
    private static void Smooth(float[] src, float[] dst, float[] scratch, int width, int height, int step)
    {
        ReadOnlySpan<float> taps = [1f / 16f, 4f / 16f, 6f / 16f, 4f / 16f, 1f / 16f];
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var k = -2; k <= 2; k++)
                {
                    sum += taps[k + 2] * src[row + Mirror(x + (k * step), width)];
                }
                scratch[row + x] = sum;
            }
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0f;
                for (var k = -2; k <= 2; k++)
                {
                    sum += taps[k + 2] * scratch[(Mirror(y + (k * step), height) * width) + x];
                }
                dst[(y * width) + x] = sum;
            }
        }
    }

    private static int Mirror(int i, int n) => i < 0 ? Math.Min(-i, n - 1) : i >= n ? Math.Max((2 * n) - i - 2, 0) : i;

    private static float RobustSigmaOfDifference(float[] fine, float[] smooth, bool[] sky)
    {
        var band = new List<float>(sky.Length / SampleStride / 2);
        for (var i = 0; i < sky.Length; i += SampleStride)
        {
            if (sky[i])
            {
                band.Add(fine[i] - smooth[i]);
            }
        }
        band.Sort();
        var median = band[band.Count / 2];
        for (var i = 0; i < band.Count; i++)
        {
            band[i] = MathF.Abs(band[i] - median);
        }
        band.Sort();
        return 1.4826f * band[band.Count / 2];
    }
}
