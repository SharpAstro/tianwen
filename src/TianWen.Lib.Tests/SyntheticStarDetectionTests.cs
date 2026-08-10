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
[Collection("Imaging")]
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
    [InlineData(20, 5f)]   // moderate defocus (FWHM ~2.15), default initial pos 980 with best 1000
    [InlineData(20, 10f)]
    [InlineData(20, 15f)]
    [InlineData(50, 5f)]   // heavy defocus (FWHM ~2.83), initial pos 950 with best 1000
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

    /// <summary>
    /// Every detection must be a DISTINCT position: a star may not be reported twice.
    ///
    /// <para>This is the invariant that <c>GivenDefocusLevelThenMinimumStarCountMet</c> silently
    /// depended on and did not state. Its focused case asserted 50 detections from a 50-star field
    /// whose magnitudes span 5 to 12, so a good fraction sit under the SNR-10 floor and the target was
    /// unreachable honestly; it passed only because a saturated star's above-threshold halo extends
    /// past the <c>HfdFactor * HFD</c> star-area mask, so halo pixels re-analysed the same core and
    /// each copy was counted again. Detection now rejects a candidate whose centroid is already inside
    /// the accepted-star area, and the count fell to its true value.</para>
    ///
    /// <para>A count floor cannot catch that class of bug (duplicates only ever push the count UP,
    /// through the assertion), so this asserts the property instead. 1.5 px is comfortably below the
    /// closest genuine pair this field can produce: 50 stars over 1280x960 gives an expected 0.11
    /// pairs within 6 px, so any sub-pixel cluster is a duplicate, not a close double.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(50)]
    public async Task GivenAnyDefocusLevel_ThenNoStarIsDetectedTwice(int defocusSteps)
    {
        var ct = TestContext.Current.CancellationToken;

        var data = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: defocusSteps,
            exposureSeconds: Exposure, starCount: 50, seed: Seed, noiseSeed: 1);

        var image = ToImage(data);
        var stars = await image.FindStarsAsync(0, snrMin: 10f, maxStars: 200, cancellationToken: ct);
        var all = stars.ToArray();

        var duplicates = 0;
        (float X, float Y)? worst = null;
        for (var i = 0; i < all.Length; i++)
        {
            for (var j = i + 1; j < all.Length; j++)
            {
                if (MathF.Abs(all[i].XCentroid - all[j].XCentroid) < 1.5f
                    && MathF.Abs(all[i].YCentroid - all[j].YCentroid) < 1.5f)
                {
                    duplicates++;
                    worst ??= (all[i].XCentroid, all[i].YCentroid);
                }
            }
        }

        output.WriteLine("defocus={0} → {1} stars, {2} duplicate pair(s){3}",
            defocusSteps, all.Length, duplicates,
            worst is { } w ? $" first at ({w.X:F2}, {w.Y:F2})" : "");

        duplicates.ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 40)]    // focused → most of the 50 injected clear SNR 10; the rest are mag 11-12
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

    /// <summary>
    /// Replicates the exact FakeCameraDriver pipeline: BitDepth.Int16 with float data,
    /// to verify star detection works through the same path the session uses.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task GivenCameraDriverPipeline_ThenStarsDetected(int defocusSteps)
    {
        var ct = TestContext.Current.CancellationToken;

        var data = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: defocusSteps,
            exposureSeconds: 1.0, starCount: 50, seed: Seed, noiseSeed: 1);

        // Compute min/max like FakeCameraDriver does
        var dataMax = 0f;
        var dataMin = float.MaxValue;
        for (var y = 0; y < data.GetLength(0); y++)
        {
            for (var x = 0; x < data.GetLength(1); x++)
            {
                var val = data[y, x];
                if (val > dataMax) dataMax = val;
                if (val < dataMin) dataMin = val;
            }
        }

        // Create Image with BitDepth.Int16 (like FakeCameraDriver.GetBitDepthAsync returns)
        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(1.0),
            FrameType.Light, "", 4.63f, 4.63f, 800, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        var image = new Image([data], BitDepth.Int16, dataMax, dataMin, 0, meta);

        output.WriteLine("Camera pipeline: defocus={0}, MaxValue={1:F0}, MinValue={2:F0}", defocusSteps, dataMax, dataMin);

        var stars = await image.FindStarsAsync(0, snrMin: 15f, maxStars: 200, cancellationToken: ct);

        output.WriteLine("  → {0} stars detected", stars.Count);

        stars.Count.ShouldBeGreaterThan(0,
            $"Camera pipeline should detect stars at defocus={defocusSteps}");
    }
}
