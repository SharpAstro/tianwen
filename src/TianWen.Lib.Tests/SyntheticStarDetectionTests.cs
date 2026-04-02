using Shouldly;
using System;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests star detection on synthetic star field images at various defocus levels.
/// Verifies that FindStarsAsync can detect stars from perfectly focused through
/// moderately defocused images, matching what the rough focus phase encounters.
/// </summary>
public class SyntheticStarDetectionTests(ITestOutputHelper output)
{
    private const int Width = 1280;
    private const int Height = 960;
    private const int Seed = 42;
    private const double Exposure = 2.0;

    private static Image ToImage(float[,] data)
    {
        var h = data.GetLength(0);
        var w = data.GetLength(1);
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = data[y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(Exposure),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        return new Image([data], BitDepth.Float32, max, min, 0, meta);
    }

    [Theory]
    [InlineData(0, 5f)]    // perfectly focused
    [InlineData(0, 10f)]
    [InlineData(0, 15f)]
    [InlineData(10, 5f)]   // slight defocus (FWHM ~2.04)
    [InlineData(10, 10f)]
    [InlineData(10, 15f)]
    [InlineData(20, 5f)]   // moderate defocus (FWHM ~2.15) — default initial pos 980 with best 1000
    [InlineData(20, 10f)]
    [InlineData(20, 15f)]
    [InlineData(50, 5f)]   // heavy defocus (FWHM ~2.83) — initial pos 950 with best 1000
    [InlineData(50, 10f)]
    [InlineData(50, 15f)]
    [InlineData(100, 5f)]  // very heavy defocus (FWHM ~4.47)
    [InlineData(100, 10f)]
    [InlineData(100, 15f)]
    public async Task GivenDefocusedSyntheticImageWhenFindingStarsThenDetected(int defocusSteps, float snrMin)
    {
        var ct = TestContext.Current.CancellationToken;

        var data = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: defocusSteps,
            exposureSeconds: Exposure, starCount: 50, seed: Seed, noiseSeed: 1);

        var image = ToImage(data);
        var stars = await image.FindStarsAsync(0, snrMin: snrMin, maxStars: 200, cancellationToken: ct);

        output.WriteLine(
            "defocus={0} snrMin={1:F0} → {2} stars detected (FWHM expected ≈ {3:F2})",
            defocusSteps, snrMin, stars.Count,
            2.0 * Math.Cosh(Math.Asinh(defocusSteps / 50.0)));

        stars.Count.ShouldBeGreaterThan(0,
            $"No stars detected at defocus={defocusSteps} snrMin={snrMin}");
    }

    [Theory]
    [InlineData(0, 50)]    // focused → detect most of 50 stars
    [InlineData(20, 30)]   // slight defocus → still many
    [InlineData(50, 15)]   // moderate → some
    public async Task GivenDefocusLevelThenMinimumStarCountMet(int defocusSteps, int minExpected)
    {
        var ct = TestContext.Current.CancellationToken;

        var data = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: defocusSteps,
            exposureSeconds: Exposure, starCount: 50, seed: Seed, noiseSeed: 1);

        var image = ToImage(data);
        var stars = await image.FindStarsAsync(0, snrMin: 10f, maxStars: 200, cancellationToken: ct);

        output.WriteLine("defocus={0} → {1} stars (need ≥{2})", defocusSteps, stars.Count, minExpected);

        stars.Count.ShouldBeGreaterThanOrEqualTo(minExpected);
    }

    [Fact]
    public async Task GivenRoughFocusConditions_ThenAtLeast15StarsDetected()
    {
        // Simulates exact rough focus conditions: initial pos 980, best 1000, 1s exposure, snrMin 15
        var ct = TestContext.Current.CancellationToken;
        var defocus = Math.Abs(980 - 1000); // 20 steps

        var data = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: defocus,
            exposureSeconds: 1.0, starCount: 50, seed: Seed, noiseSeed: 1);

        var image = ToImage(data);
        var stars = await image.FindStarsAsync(0, snrMin: 15f, maxStars: 200, cancellationToken: ct);

        output.WriteLine("Rough focus conditions: defocus={0}, 1s exposure → {1} stars", defocus, stars.Count);

        stars.Count.ShouldBeGreaterThanOrEqualTo(15,
            "Rough focus should detect ≥15 stars at 20 steps defocus with 1s exposure");
    }
}
