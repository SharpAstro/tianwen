using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public sealed class ConditionDeteriorationTests
{
    private const int Width = 640;
    private const int Height = 480;
    private const int Seed = 42;
    private const double Exposure = 5.0;

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

    [Fact]
    public async Task ClearFrame_HasMoreStarsThanCloudyFrame()
    {
        var clearData = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1);
        var clearStars = await ToImage(clearData).FindStarsAsync(0, snrMin: 5, maxStars: 200);

        var cloudyData = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1,
            cloudCoverage: 0.6, cloudSeed: 77);
        var cloudyStars = await ToImage(cloudyData).FindStarsAsync(0, snrMin: 5, maxStars: 200);

        clearStars.Count.ShouldBeGreaterThan(10, "Clear sky should detect plenty of stars");
        cloudyStars.Count.ShouldBeLessThan(clearStars.Count,
            "Cloudy sky should detect fewer stars due to attenuation");
    }

    [Fact]
    public async Task HeavyClouds_DrasticallyReduceStarCount()
    {
        var clearData = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1);
        var clearStars = await ToImage(clearData).FindStarsAsync(0, snrMin: 5, maxStars: 200);

        var overcastData = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1,
            cloudCoverage: 0.9, cloudSeed: 77);
        var overcastStars = await ToImage(overcastData).FindStarsAsync(0, snrMin: 5, maxStars: 200);

        // 90% coverage attenuates most stars but bright ones punch through — expect significant drop
        overcastStars.Count.ShouldBeLessThan(clearStars.Count,
            "Heavy clouds should reduce detected star count");
        var ratio = (float)overcastStars.Count / clearStars.Count;
        ratio.ShouldBeLessThan(0.7f,
            $"Heavy clouds should reduce stars to <70% of clear ({overcastStars.Count}/{clearStars.Count} = {ratio:F2})");
    }

    [Fact]
    public void CloudMap_ZeroCoverage_AllZero()
    {
        var map = SyntheticStarFieldRenderer.GenerateCloudMap(100, 100, coverage: 0, seed: 1);

        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                map[y, x].ShouldBe(0f);
            }
        }
    }

    [Fact]
    public void CloudMap_FullCoverage_HasMostPixelsNonZero()
    {
        var map = SyntheticStarFieldRenderer.GenerateCloudMap(200, 200, coverage: 1.0, seed: 42);

        var nonZero = 0;
        for (var y = 0; y < 200; y++)
        {
            for (var x = 0; x < 200; x++)
            {
                if (map[y, x] > 0) nonZero++;
            }
        }

        nonZero.ShouldBeGreaterThan(200 * 200 / 2, "Full coverage should have most pixels non-zero");
    }

    [Fact]
    public void CloudMap_Deterministic_SameSeedSameResult()
    {
        var map1 = SyntheticStarFieldRenderer.GenerateCloudMap(100, 100, coverage: 0.5, seed: 99);
        var map2 = SyntheticStarFieldRenderer.GenerateCloudMap(100, 100, coverage: 0.5, seed: 99);

        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                map1[y, x].ShouldBe(map2[y, x]);
            }
        }
    }

    [Fact]
    public void WriteClearAndCloudyFitsFiles()
    {
        var outputDir = SharedTestData.CreateTempTestOutputDir();

        var clearData = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1);
        var clearImage = ToImage(clearData);
        clearImage.WriteToFitsFile(Path.Combine(outputDir, "clear.fits"));

        var cloudyData = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1,
            cloudCoverage: 0.7, cloudSeed: 77);
        var cloudyImage = ToImage(cloudyData);
        cloudyImage.WriteToFitsFile(Path.Combine(outputDir, "cloudy_70pct.fits"));

        var overcastData = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1,
            cloudCoverage: 0.9, cloudSeed: 77);
        var overcastImage = ToImage(overcastData);
        overcastImage.WriteToFitsFile(Path.Combine(outputDir, "overcast_90pct.fits"));

        File.Exists(Path.Combine(outputDir, "clear.fits")).ShouldBeTrue();
        File.Exists(Path.Combine(outputDir, "cloudy_70pct.fits")).ShouldBeTrue();
        File.Exists(Path.Combine(outputDir, "overcast_90pct.fits")).ShouldBeTrue();
    }

    [Fact]
    public async Task BrightStarsStillVisibleThroughThinClouds()
    {
        var cloudyData = SyntheticStarFieldRenderer.Render(
            Width, Height, defocusSteps: 0, exposureSeconds: Exposure,
            starCount: 80, seed: Seed, noiseSeed: 1,
            cloudCoverage: 0.4, cloudSeed: 77);
        var cloudyStars = await ToImage(cloudyData).FindStarsAsync(0, snrMin: 5, maxStars: 200);

        cloudyStars.Count.ShouldBeGreaterThan(0,
            "Bright stars should still be visible through thin clouds");
    }
}
