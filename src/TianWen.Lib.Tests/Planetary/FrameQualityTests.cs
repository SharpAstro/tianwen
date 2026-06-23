using System;
using System.Drawing;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

public class FrameQualityTests
{
    private static Image Mono(float[,] px) => Image.FromChannel(px);

    // Broadband pseudo-random detail in [0.2, 0.8] -- high spatial frequency in all directions, so BOTH
    // the Laplacian and the Sobel estimators see strong response (a pure checkerboard sits on Sobel's
    // diagonal-Nyquist null and reads as zero gradient energy, which would be a misleading test input).
    private static float[,] Detail(int w, int h)
    {
        var a = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var hash = ((x * 131) + (y * 977) + 7) % 1000;
                a[y, x] = 0.2f + (0.6f * (hash / 1000f));
            }
        }

        return a;
    }

    private static float[,] BoxBlur(float[,] src)
    {
        int h = src.GetLength(0), w = src.GetLength(1);
        var dst = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                float sum = 0;
                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= h) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = x + dx;
                        if (xx < 0 || xx >= w) continue;
                        sum += src[yy, xx];
                        n++;
                    }
                }

                dst[y, x] = sum / n;
            }
        }

        return dst;
    }

    [Theory]
    [InlineData(true)]  // Laplacian variance (default)
    [InlineData(false)] // Sobel gradient energy
    public void Sharp_frame_scores_higher_than_blurred(bool laplacian)
    {
        IFrameQualityEstimator est = laplacian ? new LaplacianEnergyEstimator() : new GradientEnergyEstimator();
        var sharp = Detail(32, 32);
        var blurred = BoxBlur(BoxBlur(sharp));
        var region = new Rectangle(0, 0, 32, 32);

        est.Score(Mono(sharp), region).ShouldBeGreaterThan(est.Score(Mono(blurred), region));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Normalized_score_is_brightness_invariant(bool laplacian)
    {
        IFrameQualityEstimator est = laplacian
            ? new LaplacianEnergyEstimator(normalizeBrightness: true)
            : new GradientEnergyEstimator(normalizeBrightness: true);

        var px = Detail(24, 24);
        var bright = new float[24, 24];
        for (var y = 0; y < 24; y++)
        {
            for (var x = 0; x < 24; x++)
            {
                bright[y, x] = px[y, x] * 2f;
            }
        }

        var region = new Rectangle(0, 0, 24, 24);
        var s1 = est.Score(Mono(px), region);
        var s2 = est.Score(Mono(bright), region);

        s2.ShouldBe(s1, s1 * 0.02); // a uniform brightness scale must not change the relative-contrast score
    }

    [Fact]
    public void Empty_region_scores_whole_frame()
    {
        var est = new LaplacianEnergyEstimator();
        var sharp = Mono(Detail(16, 16));

        est.Score(sharp, Rectangle.Empty).ShouldBe(est.Score(sharp, new Rectangle(0, 0, 16, 16)));
    }

    [Fact]
    public void BoundingBox_locates_bright_disk_and_excludes_background()
    {
        var px = new float[64, 64];
        for (var y = 28; y < 36; y++)
        {
            for (var x = 28; x < 36; x++)
            {
                px[y, x] = 1.0f;
            }
        }

        var bbox = PlanetaryDisk.BoundingBox(Mono(px), sigmaAboveBackground: 3.0, pad: 2);

        bbox.Left.ShouldBeInRange(24, 28);
        bbox.Top.ShouldBeInRange(24, 28);
        bbox.Right.ShouldBeInRange(36, 40);
        bbox.Bottom.ShouldBeInRange(36, 40);
        (bbox.Width * bbox.Height).ShouldBeLessThan(64 * 64); // a tight disk box, not the whole frame
    }

    [Fact]
    public void BoundingBox_falls_back_to_full_frame_when_no_disk()
    {
        var flat = new float[32, 32]; // uniform: no bright pixels above threshold
        var bbox = PlanetaryDisk.BoundingBox(Mono(flat));

        bbox.ShouldBe(new Rectangle(0, 0, 32, 32));
    }
}
