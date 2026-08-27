using System;
using System.Drawing;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class NormalizerTests
{
    // 5x3 = 15 floats, not a Vector<float>.Count multiple (4/8/16), so the
    // scalar tail of NormalizeVec gets exercised by every test.
    private static Image Mono(float[] values, float pedestal = 0f)
    {
        values.Length.ShouldBe(15);
        var arr = new float[3, 5];
        for (var i = 0; i < 15; i++)
        {
            arr[i / 5, i % 5] = values[i];
        }
        return new Image([arr], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: pedestal,
            imageMeta: new ImageMeta { Instrument = "synth", SensorType = SensorType.Monochrome });
    }

    private static float[] Flatten(Image image, int channel = 0)
    {
        var arr = new float[image.Height * image.Width];
        for (var h = 0; h < image.Height; h++)
            for (var w = 0; w < image.Width; w++)
                arr[h * image.Width + w] = image[channel, h, w];
        return arr;
    }

    [Fact]
    public void ComputeStats_Mono_FloorIsThePedestalAndMedianComesFromThePixels()
    {
        // Values 0.0..0.14 in increments of 0.01 -> median 0.07; the floor is the pedestal,
        // whatever the pixels' minimum happens to be.
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.01f * i;
        var image = Mono(values, pedestal: 0.03f);

        var stats = Normalizer.ComputeStats(image);

        stats.PerChannelFloor.Length.ShouldBe(1);
        stats.PerChannelMedian.Length.ShouldBe(1);
        stats.PerChannelFloor[0].ShouldBe(0.03f, tolerance: 1e-5f);
        // Sort-based median is exact: pixel 7 (out of 15) = value 0.07.
        stats.PerChannelMedian[0].ShouldBe(0.07f, tolerance: 1e-5f);
    }

    [Fact]
    public void Apply_NormalizesMedianToTargetAndFloorToZero()
    {
        // Values 0.10 .. 0.24 over a pedestal of 0.10: the pedestal maps to 0, the median (0.17) to
        // the target, and the minimum pixel is just a pixel.
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.10f + 0.01f * i;
        var image = Mono(values, pedestal: 0.10f);

        var stats = Normalizer.ComputeStats(image);
        var result = Normalizer.Apply(image, stats, targetMedian: 0.5f);

        var resultFlat = Flatten(result);
        // (0.10 - 0.10) * scale = 0 for the pixel sitting on the pedestal.
        resultFlat[0].ShouldBe(0f, tolerance: 1e-5f);
        // Index 7 (value 0.17) is the median -> lands exactly on target after normalization.
        resultFlat[7].ShouldBe(0.5f, tolerance: 1e-5f);
        result.Pedestal.ShouldBe(0f);
    }

    /// <summary>
    /// The defect this class exists to keep out. The floor used to be the frame's minimum pixel, so
    /// two frames identical in every pixel but one (a hot pixel, a cosmic ray, a demosaic overshoot
    /// beside a saturated star, a flat that reaches zero) were normalised with different gains: on a
    /// real 89-frame session the red channel's gain wandered by x3.7 from frame to frame. A single
    /// pixel may not change the map for the other fourteen.
    /// </summary>
    [Fact]
    public void Apply_AnOutlierPixelDoesNotChangeTheFrameGain()
    {
        var clean = new float[15];
        for (var i = 0; i < 15; i++) clean[i] = 0.20f + 0.01f * i;
        var spiked = (float[])clean.Clone();
        spiked[3] = -50f; // one interpolation overshoot, a thousand times the frame's range

        var normClean = Normalizer.Apply(Mono(clean), Normalizer.ComputeStats(Mono(clean)), 0.5f);
        var normSpiked = Normalizer.Apply(Mono(spiked), Normalizer.ComputeStats(Mono(spiked)), 0.5f);

        var fClean = Flatten(normClean);
        var fSpiked = Flatten(normSpiked);
        for (var i = 0; i < 15; i++)
        {
            if (i == 3) continue;
            fSpiked[i].ShouldBe(fClean[i], tolerance: 1e-5f, $"pixel {i} must not see the outlier at pixel 3");
        }
    }

    [Fact]
    public void Apply_TwoFramesAtDifferentBrightness_ConvergeToSameMedian()
    {
        // Frame A bright, frame B dim, the same scene through a 5x transparency change: after
        // normalization to the same target both produce the same output, which is the point.
        var bright = new float[15];
        var dim = new float[15];
        for (var i = 0; i < 15; i++)
        {
            bright[i] = 0.5f + 0.01f * i;  // 0.50..0.64
            dim[i]    = 0.1f + 0.002f * i; // 0.100..0.128 (same shape, 5x less)
        }
        var imgA = Mono(bright);
        var imgB = Mono(dim);

        var normA = Normalizer.Apply(imgA, Normalizer.ComputeStats(imgA), 0.5f);
        var normB = Normalizer.Apply(imgB, Normalizer.ComputeStats(imgB), 0.5f);

        var fA = Flatten(normA);
        var fB = Flatten(normB);
        for (var i = 0; i < 15; i++)
        {
            fA[i].ShouldBe(fB[i], tolerance: 1e-4f);
        }
    }

    [Fact]
    public void Apply_MedianAtTheFloor_FallsBackToIdentityScale()
    {
        // Pathological: every pixel sits on the pedestal, so there is no sky to normalize on.
        // ComputeScale falls back to 1.0 rather than dividing by zero; the output is the frame
        // shifted to its floor, i.e. all zero.
        var values = new float[15];
        Array.Fill(values, 0.5f);
        var image = Mono(values, pedestal: 0.5f);

        var result = Normalizer.Apply(image, Normalizer.ComputeStats(image), targetMedian: 0.3f);

        foreach (var v in Flatten(result))
        {
            v.ShouldBe(0f, tolerance: 1e-6f);
        }
    }

    [Fact]
    public void ApplyTile_MatchesWholeFrameApplyOverRegion()
    {
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.10f + 0.013f * i;
        var image = Mono(values, pedestal: 0.02f);
        var stats = Normalizer.ComputeStats(image);

        var whole = Normalizer.Apply(image, stats, 0.5f);

        // Pick a 3x2 tile slice (rows 0-1, cols 1-3) and verify ApplyTile
        // produces the same output as the corresponding pixels of Apply.
        const int rw = 3, rh = 2;
        var src = new float[rw * rh];
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
                src[y * rw + x] = image[0, y, 1 + x];

        var dst = new float[rw * rh];
        Normalizer.ApplyTile(src, channel: 0, stats, 0.5f, dst);

        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
                dst[y * rw + x].ShouldBe(whole[0, y, 1 + x], tolerance: 1e-5f);
    }

    [Fact]
    public void Apply_ShapeMismatch_Throws()
    {
        var image = Mono(new float[15]);
        var badStats = new NormalizationStats(new float[2], new float[2]);

        Should.Throw<ArgumentException>(() => Normalizer.Apply(image, badStats, 0.5f));
    }

    [Fact]
    public void ApplyTile_BadChannelIndex_Throws()
    {
        var stats = new NormalizationStats(new float[] { 0f }, new float[] { 0.5f });
        var src = new float[4];
        var dst = new float[4];

        Should.Throw<ArgumentOutOfRangeException>(() => Normalizer.ApplyTile(src, channel: 1, stats, 0.5f, dst));
    }

    [Fact]
    public void ApplyTile_LengthMismatch_Throws()
    {
        var stats = new NormalizationStats(new float[] { 0f }, new float[] { 0.5f });
        Should.Throw<ArgumentException>(() => Normalizer.ApplyTile(new float[4], 0, stats, 0.5f, new float[3]));
    }

    [Fact]
    public void ComputeStats_Rect_OnlyConsidersPixelsInsideBox()
    {
        // 5x3 image, values 0.00..0.14 row-major. Rect = row 0 only.
        // Inside: 0.00, 0.01, 0.02, 0.03, 0.04 -> median (n=5) = 0.02.
        // Whole-image would give median=0.07; the rect must NOT pick that up.
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.01f * i;
        var image = Mono(values);

        var stats = Normalizer.ComputeStats(image, new Rectangle(0, 0, 5, 1));

        stats.PerChannelFloor[0].ShouldBe(0.00f, tolerance: 1e-5f);
        stats.PerChannelMedian[0].ShouldBe(0.02f, tolerance: 1e-5f);
    }

    [Fact]
    public void ComputeStats_Rect_SkipsNaNInsideBox()
    {
        // Rect contains row 1 (values 0.05..0.09). Drop pixel (1,2) -> NaN.
        // Remaining: 0.05, 0.06, 0.08, 0.09 -> median = (0.06+0.08)/2 = 0.07.
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.01f * i;
        values[7] = float.NaN;
        var image = Mono(values);

        var stats = Normalizer.ComputeStats(image, new Rectangle(0, 1, 5, 1));

        stats.PerChannelMedian[0].ShouldBe(0.07f, tolerance: 1e-5f);
    }

    [Fact]
    public void ComputeStats_Rect_EmptyBoxFallsBackToWholeImage()
    {
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.01f * i;
        var image = Mono(values);

        var statsWhole = Normalizer.ComputeStats(image);
        var statsEmpty = Normalizer.ComputeStats(image, new Rectangle(10, 10, 0, 0));

        statsEmpty.PerChannelFloor[0].ShouldBe(statsWhole.PerChannelFloor[0], tolerance: 1e-5f);
        statsEmpty.PerChannelMedian[0].ShouldBe(statsWhole.PerChannelMedian[0], tolerance: 1e-5f);
    }

    [Fact]
    public void ComputeStats_Rect_ClampsToImageBounds()
    {
        // Rect overruns image to the right (width=10 vs image width=5).
        // Should clamp to the image width (covers full row 0) and produce
        // the same stats as Rectangle(0, 0, 5, 1).
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.01f * i;
        var image = Mono(values);

        var clamped = Normalizer.ComputeStats(image, new Rectangle(0, 0, 10, 1));

        clamped.PerChannelMedian[0].ShouldBe(0.02f, tolerance: 1e-5f);
    }

    [Fact]
    public void ComputeStats_IgnoresNaN()
    {
        // 14 valid pixels (0.10..0.24 less the NaN) + one NaN: the median is finite and NaN never
        // reaches either statistic.
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.10f + 0.01f * i;
        values[7] = float.NaN; // mid pixel NaN
        var image = Mono(values);

        var stats = Normalizer.ComputeStats(image);

        float.IsNaN(stats.PerChannelMedian[0]).ShouldBeFalse();
        float.IsNaN(stats.PerChannelFloor[0]).ShouldBeFalse();
        // Finite values are 0.10..0.16 and 0.18..0.24 (14 of them): median = (0.16 + 0.18) / 2.
        stats.PerChannelMedian[0].ShouldBe(0.17f, tolerance: 1e-5f);
    }
}
