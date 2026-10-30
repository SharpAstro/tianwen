using System;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="GuideStarReadout"/> on synthetic frames (#1915): a Gaussian star of known position, width and peak over a flat
/// noisy sky, so the centroid, the HFD, the saturation and the choice among stars are checked against what was drawn.
/// </summary>
[Collection("Guider")]
public class GuideStarReadoutTests
{
    private const float FullScale = 65535f;
    private const int Width = 320;
    private const int Height = 240;

    private static float[,] Sky(int seed = 7, float level = 500f, float noise = 5f)
    {
        var rng = new Random(seed);
        var frame = new float[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                // Sum of two uniforms minus one: noise of bounded spread, no Box-Muller needed.
                frame[y, x] = level + noise * (float)(rng.NextDouble() + rng.NextDouble() - 1.0);
            }
        }
        return frame;
    }

    private static void AddStar(float[,] frame, double cx, double cy, double sigma, double peak)
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy < 400)
                {
                    frame[y, x] += (float)(peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma)));
                }
            }
        }
    }

    [Fact]
    public void AStarIsReadAtItsCentroidWithItsPeakAndNoSaturation()
    {
        var frame = Sky();
        AddStar(frame, 120.3, 90.7, 2.0, 20000);

        var read = GuideStarReadout.Measure(frame, FullScale);

        read.ShouldNotBeNull();
        read.Value.X.ShouldBe(120.3, 0.15);
        read.Value.Y.ShouldBe(90.7, 0.15);
        read.Value.Saturated.ShouldBeFalse();
        read.Value.Peak.ShouldBeInRange(15000, 21000);
        read.Value.Snr.ShouldBeGreaterThan(50);
        read.Value.HProfile.Length.ShouldBe(2 * GuideStarReadout.Radius + 1);
        read.Value.VProfile.Length.ShouldBe(2 * GuideStarReadout.Radius + 1);
        read.Value.HProfile[GuideStarReadout.Radius].ShouldBeGreaterThan(10000f);
    }

    [Fact]
    public void ABroaderStarReadsALargerHfd()
    {
        var sharp = Sky();
        AddStar(sharp, 100, 100, 1.5, 20000);
        var soft = Sky();
        AddStar(soft, 100, 100, 3.5, 20000);

        var sharpHfd = GuideStarReadout.Measure(sharp, FullScale)!.Value.Hfd;
        var softHfd = GuideStarReadout.Measure(soft, FullScale)!.Value.Hfd;

        softHfd.ShouldBeGreaterThan(sharpHfd * 1.5);
        // A Gaussian's half-flux diameter is about 2.35 * sigma * 0.5 * 2, here read inside a 16 px aperture.
        sharpHfd.ShouldBeInRange(2.0, 4.5);
    }

    [Fact]
    public void AClippedStarIsSaidToBeSaturatedAgainstTheFramesFullScale()
    {
        var frame = Sky();
        AddStar(frame, 150, 120, 2.5, 90000);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                frame[y, x] = MathF.Min(frame[y, x], FullScale);
            }
        }

        var read = GuideStarReadout.Measure(frame, FullScale);

        read.ShouldNotBeNull();
        read.Value.Saturated.ShouldBeTrue();
        read.Value.PeakFraction.ShouldBeGreaterThanOrEqualTo(0.98);
    }

    [Fact]
    public void ABrightStarBelowAFullScaleDeclaredLowerIsSaturated()
    {
        // A 12-bit camera in a 16-bit container: the frame's own MaxValue says nothing, the declared scale does.
        var frame = Sky();
        AddStar(frame, 150, 120, 2.5, 3700);

        GuideStarReadout.Measure(frame, 4095f)!.Value.Saturated.ShouldBeTrue();
        GuideStarReadout.Measure(frame, FullScale)!.Value.Saturated.ShouldBeFalse();
    }

    [Fact]
    public void AutoPrefersAnUnsaturatedStarOverABrighterClippedOne()
    {
        var frame = Sky();
        AddStar(frame, 80, 80, 2.5, 90000);
        AddStar(frame, 220, 150, 2.0, 12000);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                frame[y, x] = MathF.Min(frame[y, x], FullScale);
            }
        }

        var read = GuideStarReadout.Measure(frame, FullScale);

        read.ShouldNotBeNull();
        read.Value.Saturated.ShouldBeFalse();
        read.Value.X.ShouldBe(220, 0.3);
        read.Value.Y.ShouldBe(150, 0.3);
    }

    [Fact]
    public void AutoSkipsAStarWithABrightNeighbourAndKeepsTheLoneOne()
    {
        var frame = Sky();
        AddStar(frame, 100, 100, 2.0, 30000);
        AddStar(frame, 110, 100, 2.0, 28000);
        AddStar(frame, 240, 160, 2.0, 15000);

        var found = string.Join("; ", new GuiderCentroidTracker(GuideStarReadout.Radius).FindStars(frame)
            .ConvertAll(s => $"({s.X:F1},{s.Y:F1} flux {s.Flux:F0} snr {s.SNR:F1})"));
        var read = GuideStarReadout.Measure(frame, FullScale);

        read.ShouldNotBeNull();
        read.Value.X.ShouldBe(240, 0.3, $"candidates: {found}");
    }

    [Fact]
    public void ANamedPositionAFewPixelsOffLandsOnTheStarAndTheSecondStarIsLeftAlone()
    {
        var frame = Sky();
        AddStar(frame, 100, 100, 2.0, 30000);
        AddStar(frame, 200, 100, 2.0, 30000);

        var read = GuideStarReadout.Measure(frame, FullScale, (205.0, 104.0));

        read.ShouldNotBeNull();
        read.Value.X.ShouldBe(200, 0.3);
        read.Value.Y.ShouldBe(100, 0.3);
    }

    [Fact]
    public void ANamedPositionInEmptySkyFindsNothing()
    {
        var frame = Sky();
        AddStar(frame, 100, 100, 2.0, 30000);

        GuideStarReadout.Measure(frame, FullScale, (250.0, 200.0)).ShouldBeNull();
    }

    [Fact]
    public void AnEmptyFrameHasNoStar()
    {
        GuideStarReadout.Measure(Sky(), FullScale).ShouldBeNull();
    }

    [Fact]
    public void AnImageReadsAgainstItsDeclaredFullScale()
    {
        var frame = Sky();
        AddStar(frame, 150, 120, 2.5, 3700);
        var meta = new ImageMeta("synth", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1),
            FrameType.Light, "", 3.76f, 3.76f, 500, FocuserPositions.None, Filter.Luminance, 1, 1,
            float.NaN, SensorType.Monochrome, 0, 0, RowOrder.TopDown, float.NaN, float.NaN, SensorFullScaleAdu: 4095f);
        var image = new Image([frame], BitDepth.Int16, maxValue: 4000f, minValue: 0f, pedestal: 0f, imageMeta: meta);

        GuideStarReadout.FullScaleOf(image).ShouldBe(4095f);
        GuideStarReadout.Measure(image)!.Value.Saturated.ShouldBeTrue();
    }
}
