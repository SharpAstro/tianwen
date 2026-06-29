using System;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Tests;

/// <summary>
/// Shared helpers for the RC-Astro CLI-wrapper tests: the present+licensed gate
/// (so integration tests skip cleanly off this machine), synthetic plate
/// builders, and pixel/noise measurements.
/// </summary>
internal static class RcAstroTestSupport
{
    /// <summary>
    /// True when the RC-Astro CLI is installed AND <paramref name="productKey"/>
    /// is licensed here; otherwise sets <paramref name="skipMessage"/>.
    /// </summary>
    public static bool ProductAvailable(string productKey, out string skipMessage)
    {
        var cli = new RcAstroCli();
        if (cli.IsAvailable && cli.IsLicensed(productKey))
        {
            skipMessage = string.Empty;
            return true;
        }
        skipMessage = cli.IsAvailable
            ? $"RC-Astro CLI found but '{productKey}' is not licensed on this machine."
            : "RC-Astro CLI not installed (set RC_ASTRO_CLI to enable this test).";
        return false;
    }

    public static int CountBrightPixels(Image image, float threshold)
    {
        var count = 0;
        var (channels, _, _) = image.Shape;
        for (var c = 0; c < channels; c++)
        {
            var span = image.GetChannelSpan(c);
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] > threshold)
                {
                    count++;
                }
            }
        }
        return count;
    }

    /// <summary>Mean robust-σ across channels (from EstimateNoiseProfile).</summary>
    public static float MeanSigma(Image image)
    {
        var profile = image.EstimateNoiseProfile();
        if (profile.IsDefaultOrEmpty)
        {
            return 0f;
        }
        var sum = 0f;
        foreach (var s in profile)
        {
            sum += s;
        }
        return sum / profile.Length;
    }

    public static bool AllFinite(Image image)
    {
        var (channels, _, _) = image.Shape;
        for (var c = 0; c < channels; c++)
        {
            var span = image.GetChannelSpan(c);
            for (var i = 0; i < span.Length; i++)
            {
                if (!float.IsFinite(span[i]))
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>RMS difference between two same-shaped images.</summary>
    public static double RmsDifference(Image a, Image b)
    {
        var (channels, _, _) = a.Shape;
        var sumSq = 0.0;
        long n = 0;
        for (var c = 0; c < channels; c++)
        {
            var sa = a.GetChannelSpan(c);
            var sb = b.GetChannelSpan(c);
            var len = Math.Min(sa.Length, sb.Length);
            for (var i = 0; i < len; i++)
            {
                var d = sa[i] - sb[i];
                sumSq += d * d;
                n++;
            }
        }
        return n == 0 ? 0.0 : Math.Sqrt(sumSq / n);
    }

    /// <summary>RGB plate: flat background with a handful of Gaussian stars.</summary>
    public static Image BuildRgbWithStars(int w, int h, float bg = 0.10f)
    {
        var r = Fill(w, h, bg);
        var g = Fill(w, h, bg);
        var b = Fill(w, h, bg);

        (int X, int Y, float Sigma, float Peak)[] stars =
        [
            (w / 4, h / 4, 1.8f, 0.9f),
            (3 * w / 4, h / 4, 2.0f, 0.85f),
            (w / 2, h / 2, 1.5f, 0.95f),
            (w / 3, 2 * h / 3, 1.6f, 0.8f),
            (2 * w / 3, 3 * h / 4, 2.2f, 0.75f),
        ];
        foreach (var (sx, sy, sigma, peak) in stars)
        {
            for (var dy = -8; dy <= 8; dy++)
            {
                var y = sy + dy;
                if ((uint)y >= (uint)h) continue;
                for (var dx = -8; dx <= 8; dx++)
                {
                    var x = sx + dx;
                    if ((uint)x >= (uint)w) continue;
                    var weight = peak * MathF.Exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
                    r[y, x] = MathF.Min(1f, r[y, x] + weight);
                    g[y, x] = MathF.Min(1f, g[y, x] + weight);
                    b[y, x] = MathF.Min(1f, b[y, x] + weight);
                }
            }
        }
        return ToImage(r, g, b);
    }

    /// <summary>RGB plate: flat background plus deterministic Gaussian noise.</summary>
    public static Image BuildNoisyRgb(int w, int h, float bg, float noiseSigma, int seed)
    {
        var rng = new Random(seed);
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                r[y, x] = Clamp01(bg + noiseSigma * (float)Gaussian(rng));
                g[y, x] = Clamp01(bg + noiseSigma * (float)Gaussian(rng));
                b[y, x] = Clamp01(bg + noiseSigma * (float)Gaussian(rng));
            }
        }
        return ToImage(r, g, b);
    }

    /// <summary>RGB plate: a broad smooth "nebula" bump plus light noise -- gives
    /// a nonstellar deconvolver (bxt) low-frequency structure to sharpen.</summary>
    public static Image BuildNebula(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var r = new float[h, w];
        var g = new float[h, w];
        var b = new float[h, w];
        var cx = w / 2f;
        var cy = h / 2f;
        var sigma = w / 6f;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var blob = 0.40f * MathF.Exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
                var v = Clamp01(0.08f + blob + 0.01f * (float)Gaussian(rng));
                r[y, x] = v;
                g[y, x] = v;
                b[y, x] = v;
            }
        }
        return ToImage(r, g, b);
    }

    private static float[,] Fill(int w, int h, float value)
    {
        var a = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                a[y, x] = value;
            }
        }
        return a;
    }

    private static Image ToImage(float[,] r, float[,] g, float[,] b)
        => new([r, g, b], BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });

    private static double Gaussian(Random rng)
    {
        // Box-Muller.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
