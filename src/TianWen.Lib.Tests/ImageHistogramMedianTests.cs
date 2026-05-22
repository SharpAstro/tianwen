using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Direct assertions on <see cref="Image.GetPedestralMedianAndMADScaledToUnit"/>'s
/// returned median. Until this commit the histogram median calc used
/// <c>histogram.Count / 2.0</c> (bin count) instead of <c>hist_total / 2.0</c>
/// (pixel count), so for distributions where the actual median sits well
/// inside the bin range it would land off by a wide margin. Symptom
/// commonly hidden because typical astro frames have a tight background
/// dominating the low bins, so both thresholds resolve to the same bin and
/// the bug stays latent.
/// </summary>
[Collection("Imaging")]
public class ImageHistogramMedianTests
{
    private static Image Constant(float value, int width, int height)
    {
        var arr = new float[height, width];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                arr[y, x] = value;
        return new Image([arr], BitDepth.Float32, 1f, 0f, 0f, default);
    }

    /// <summary>Pixel values uniformly distributed across [0, 1].</summary>
    private static Image UniformRamp(int width, int height)
    {
        var arr = new float[height, width];
        var pixels = width * height;
        for (var i = 0; i < pixels; i++)
        {
            // Distribute pixels uniformly over [0, 1]. Pixel order doesn't
            // matter for median; we just need the *distribution* to span
            // the full range with uniform density.
            arr[i / width, i % width] = (float)i / (pixels - 1);
        }
        return new Image([arr], BitDepth.Float32, 1f, 0f, 0f, default);
    }

    [Fact]
    public void Median_Constant_ReturnsConstant()
    {
        // 256x256 happens to have hist_total == histogram.Count, so this
        // test passes regardless of the medianlength fix. Pure sanity.
        var img = Constant(0.42f, 256, 256);
        var (_, median, _) = img.GetPedestralMedianAndMADScaledToUnit(0);
        median.ShouldBe(0.42f, tolerance: 1f / 65535 * 2);
    }

    [Fact]
    public void Median_UniformRamp_512x512_ReturnsHalf()
    {
        // 512x512 = 262144 pixels > histogram.Count (65536), so the buggy
        // medianlength (= 32768 = histogram.Count/2) lands BELOW the actual
        // median (= 131072 = hist_total/2). Old: median ~0.13. Fixed: ~0.5.
        // Distribution is uniform [0, 1] so 50th percentile is 0.5 by
        // construction.
        var img = UniformRamp(512, 512);
        var (_, median, _) = img.GetPedestralMedianAndMADScaledToUnit(0);
        median.ShouldBe(0.5f, tolerance: 0.01f);
    }

    [Fact]
    public void Median_UniformRamp_1024x1024_ReturnsHalf()
    {
        // Stress at higher pixel count -- more aggressive bug expression
        // since hist_total/histogram.Count = 16 here. Bigger gap between
        // buggy and correct medianlength.
        var img = UniformRamp(1024, 1024);
        var (_, median, _) = img.GetPedestralMedianAndMADScaledToUnit(0);
        median.ShouldBe(0.5f, tolerance: 0.005f);
    }

    [Fact]
    public void Median_AstroBackground_ReturnsBackgroundLevel()
    {
        // Realistic shape: 99% of pixels at the sky background (~0.05),
        // 1% bright outliers spread between 0.3 and 0.95. Pixel median
        // is the background since it dominates. This case typically works
        // even with the bug because the dominant background bin's cumulative
        // count exceeds both buggy and correct thresholds -- but worth
        // pinning down to catch any future regression.
        const int W = 512;
        const int H = 512;
        var arr = new float[H, W];
        const float bg = 0.05f;
        var brightCount = W * H / 100;  // 1% bright
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                arr[y, x] = bg;
        // Drop bright pixels at deterministic positions across [0.3, 0.95].
        var rng = new System.Random(42);
        for (var i = 0; i < brightCount; i++)
        {
            var x = rng.Next(W);
            var y = rng.Next(H);
            arr[y, x] = 0.3f + (float)rng.NextDouble() * 0.65f;
        }
        var img = new Image([arr], BitDepth.Float32, 1f, 0f, 0f, default);
        var (_, median, _) = img.GetPedestralMedianAndMADScaledToUnit(0);
        median.ShouldBe(bg, tolerance: 0.005f);
    }

    [Fact]
    public void Median_BimodalDominantHigh_512x512_ReturnsHighMode()
    {
        // 80% at 0.6, 20% at 0.2. Pixel median is in the dominant 0.6 mode
        // (60th percentile of all pixels). The bug would land us in the
        // 0.2 bin because the buggy threshold is reached before getting
        // to the bin with the bulk.
        const int W = 512;
        const int H = 512;
        var arr = new float[H, W];
        var lowCount = W * H * 2 / 10;   // 20%
        var idx = 0;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                arr[y, x] = idx < lowCount ? 0.2f : 0.6f;
                idx++;
            }
        }
        var img = new Image([arr], BitDepth.Float32, 1f, 0f, 0f, default);
        var (_, median, _) = img.GetPedestralMedianAndMADScaledToUnit(0);
        median.ShouldBe(0.6f, tolerance: 0.01f);
    }
}
