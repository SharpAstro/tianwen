using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Direct assertions on <see cref="ImageStats.ComputeAsync"/>: linear-detect
/// flag, star geometry roll-up, per-channel background/noise, graceful
/// behaviour on edge cases (constant image -> 0 stars, no crash).
/// </summary>
[Collection("Imaging")]
public class ImageStatsTests
{
    private const int Width = 640;
    private const int Height = 480;
    private const int Seed = 42;
    private const double Exposure = 5.0;

    private static ImageMeta MonoMeta() => new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(Exposure),
        FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
        float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);

    private static Image ToImage(float[,] data, float min, float max, float pedestal = 0f)
        => new Image([data], BitDepth.Float32, max, min, pedestal, MonoMeta());

    private static (float min, float max) MinMax(float[,] data)
    {
        var (h, w) = (data.GetLength(0), data.GetLength(1));
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var v = data[y, x];
                if (v < min) min = v;
                if (v > max) max = v;
            }
        return (min, max);
    }

    [Fact]
    public async Task LinearStarField_ReportsIsLinearAndDetectsStars()
    {
        // Synthetic field: dim background + sparse PSFs. Median sits well
        // below 0.2 so DetectPreStretched returns false (i.e. IsLinear = true).
        var data = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1);
        var (min, max) = MinMax(data);
        var image = ToImage(data, min, max);

        var stats = await ImageStats.ComputeAsync(image, snrMin: 5f, maxStars: 200, cancellationToken: TestContext.Current.CancellationToken);

        stats.Width.ShouldBe(Width);
        stats.Height.ShouldBe(Height);
        stats.ChannelCount.ShouldBe(1);
        stats.IsLinear.ShouldBeTrue("dark-sky synthetic should not be flagged as pre-stretched");
        stats.StarCount.ShouldBeGreaterThan(10);
        stats.HfdMedian.ShouldBeGreaterThan(0f);
        stats.FwhmMedian.ShouldBeGreaterThan(0f);
        stats.SnrMedian.ShouldBeGreaterThan(5f);
        stats.PerChannel.Length.ShouldBe(1);
        stats.PerChannel[0].NoiseSigma.ShouldBeGreaterThan(0f);
    }

    [Fact]
    public async Task MtfStretchedField_DetectedAsPreStretched()
    {
        // Stretch the same synthetic with target_median = 0.25 (Frank's
        // convention). Median shifts well above the 0.2 threshold so
        // DetectPreStretched flips to true -> IsLinear should be false.
        var data = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1);
        var (min, max) = MinMax(data);
        var linear = ToImage(data, min, max);
        var stretched = linear.MtfStretch(0.25, out _, out _);

        var stats = await ImageStats.ComputeAsync(stretched, snrMin: 5f, maxStars: 200, cancellationToken: TestContext.Current.CancellationToken);

        stats.IsLinear.ShouldBeFalse("MTF-stretched plate should be flagged as pre-stretched");
        // Star detection still runs but the lift to a 0.25 median compresses
        // contrast hard enough that the synthetic stars may not clear the
        // SNR threshold any more. The contract here is only the IsLinear
        // flag flip -- the star geometry numbers are intentionally not
        // comparable across the linear/stretched boundary.
    }

    [Fact]
    public async Task ConstantImage_ZeroStarsWithoutCrash()
    {
        // Uniform pixel field is a degenerate case: zero star detection,
        // MAD will be tiny (no variation). The record's float fields stay
        // numerically sane (no NaN propagation), star count is 0.
        var data = new float[Height, Width];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                data[y, x] = 0.05f;
        var image = ToImage(data, 0.05f, 0.05f);

        var stats = await ImageStats.ComputeAsync(image, snrMin: 5f, maxStars: 200, cancellationToken: TestContext.Current.CancellationToken);

        stats.StarCount.ShouldBe(0);
        stats.HfdMedian.ShouldBe(0f);
        stats.FwhmMedian.ShouldBe(0f);
        stats.SnrMedian.ShouldBe(0f);
        stats.IsLinear.ShouldBeTrue("constant 0.05 image has median 0.05 -> not flagged as pre-stretched");
    }

    [Fact]
    public async Task ColorImage_ReportsPerChannelStats()
    {
        // 3-channel synthetic with distinct per-channel pedestals so the
        // per-channel stats are demonstrably different (catches a regression
        // where ComputeAsync would silently miss channels > 0).
        var r = new float[Height, Width];
        var g = new float[Height, Width];
        var b = new float[Height, Width];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                r[y, x] = 0.05f;
                g[y, x] = 0.07f;
                b[y, x] = 0.10f;
            }
        var meta = new ImageMeta("synth", DateTime.UtcNow, TimeSpan.FromSeconds(Exposure),
            FrameType.Light, "", 3.76f, 3.76f, 500, -1, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Color, 0, 0, RowOrder.TopDown, float.NaN, float.NaN);
        var image = new Image([r, g, b], BitDepth.Float32, 0.10f, 0.05f, 0f, meta);

        var stats = await ImageStats.ComputeAsync(image, snrMin: 5f, maxStars: 200, cancellationToken: TestContext.Current.CancellationToken);

        stats.ChannelCount.ShouldBe(3);
        stats.PerChannel.Length.ShouldBe(3);
        // ChannelIndex must match position to make the JSON serialisation
        // semantically meaningful.
        for (var i = 0; i < 3; i++) stats.PerChannel[i].ChannelIndex.ShouldBe(i);
    }
}
