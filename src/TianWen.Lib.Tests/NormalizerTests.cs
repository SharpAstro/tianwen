using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class NormalizerTests
{
    // 5x3 = 15 floats — not a Vector<float>.Count multiple (4/8/16), so the
    // scalar tail of NormalizeVec gets exercised by every test.
    private static Image Mono(float[] values)
    {
        values.Length.ShouldBe(15);
        var arr = new float[3, 5];
        for (var i = 0; i < 15; i++)
        {
            arr[i / 5, i % 5] = values[i];
        }
        return Image.FromChannel(arr, maxValue: 1f, minValue: 0f);
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
    public void ComputeStats_Mono_GivesMinAndMedianFromHistogram()
    {
        // Values 0.0..0.14 in increments of 0.01 -> min 0.0, median 0.07
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.01f * i;
        var image = Mono(values);

        var stats = Normalizer.ComputeStats(image);

        stats.PerChannelMin.Length.ShouldBe(1);
        stats.PerChannelMedian.Length.ShouldBe(1);
        stats.PerChannelMin[0].ShouldBe(0f, tolerance: 1e-5f);
        // Sort-based median is exact — pixel 7 (out of 15) = value 0.07.
        stats.PerChannelMedian[0].ShouldBe(0.07f, tolerance: 1e-5f);
    }

    [Fact]
    public void Apply_NormalizesMedianToTarget()
    {
        // Values 0.10 .. 0.24. Min = 0.10, median = 0.17.
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.10f + 0.01f * i;
        var image = Mono(values);
        var stats = Normalizer.ComputeStats(image);

        var result = Normalizer.Apply(image, stats, targetMedian: 0.5f);

        // After (v - min) * (target / (median - min)):
        //   target maps median -> target -> 0.5
        //   min   maps        -> 0
        var resultFlat = Flatten(result);
        var minOut = float.PositiveInfinity;
        foreach (var v in resultFlat) if (v < minOut) minOut = v;
        minOut.ShouldBe(0f, tolerance: 1e-5f);

        // Index 7 (value 0.17) is the median -> lands exactly on target after normalization.
        resultFlat[7].ShouldBe(0.5f, tolerance: 1e-5f);
    }

    [Fact]
    public void Apply_TwoFramesAtDifferentBrightness_ConvergeToSameMedian()
    {
        // Frame A bright, frame B dim. Both have IDENTICAL spatial pattern
        // (same relative gradient), only the absolute level differs.
        // After normalization to the same target, both should produce nearly
        // identical output — the whole point of normalization.
        var bright = new float[15];
        var dim = new float[15];
        for (var i = 0; i < 15; i++)
        {
            bright[i] = 0.5f + 0.01f * i;  // 0.50..0.64
            dim[i]    = 0.1f + 0.002f * i; // 0.100..0.128 (same shape, 5x less + offset)
        }
        var imgA = Mono(bright);
        var imgB = Mono(dim);

        var statsA = Normalizer.ComputeStats(imgA);
        var statsB = Normalizer.ComputeStats(imgB);
        var normA = Normalizer.Apply(imgA, statsA, 0.5f);
        var normB = Normalizer.Apply(imgB, statsB, 0.5f);

        var fA = Flatten(normA);
        var fB = Flatten(normB);
        for (var i = 0; i < 15; i++)
        {
            // After normalization both frames have the same shape (linear gradient
            // with min=0 and median=0.5), so per-pixel match should be tight.
            fA[i].ShouldBe(fB[i], tolerance: 0.1f);
        }
    }

    [Fact]
    public void Apply_FlatFrame_MedianEqualsMin_FallsBackToIdentityScale()
    {
        // Pathological: all pixels are the same value. min == median.
        // ComputeScale falls back to 1.0 so we don't divide by zero.
        var values = new float[15];
        Array.Fill(values, 0.5f);
        var image = Mono(values);
        var stats = Normalizer.ComputeStats(image);

        var result = Normalizer.Apply(image, stats, targetMedian: 0.3f);

        // out = (0.5 - 0.5) * 1.0 = 0.0  (since scale falls back to 1, but min == 0.5 so output is 0)
        // The result is a flat zero image — caller is expected to handle "no
        // dynamic range to normalize" by skipping the frame in the integrator
        // or interpreting the zero-flat output as a sentinel.
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
        var image = Mono(values);
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
    public void ComputeStats_IgnoresNaN()
    {
        // 14 valid pixels (0.1..0.14) + one NaN. Min should be 0.10, NOT NaN.
        var values = new float[15];
        for (var i = 0; i < 15; i++) values[i] = 0.10f + 0.01f * i;
        values[7] = float.NaN; // mid pixel NaN
        var image = Mono(values);

        var stats = Normalizer.ComputeStats(image);

        stats.PerChannelMin[0].ShouldBe(0.10f, tolerance: 1e-5f);
        float.IsNaN(stats.PerChannelMin[0]).ShouldBeFalse();
    }
}
