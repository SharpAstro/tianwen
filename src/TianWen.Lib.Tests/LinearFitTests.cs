using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary><see cref="LeastAbsoluteDeviation"/>, <see cref="LinearFit"/> (PixInsight's, before mono channels are
/// combined) and the block noise the synthetic luminance is weighted by.</summary>
[Collection("Imaging")]
public class LinearFitTests(ITestOutputHelper output)
{
    private const int Size = 256;

    [Fact]
    public void TheLeastAbsoluteDeviationLineIgnoresAFifthOfGrossOutliers()
    {
        var rng = new Random(3);
        const int n = 5000;
        var x = new float[n];
        var y = new float[n];
        for (var i = 0; i < n; i++)
        {
            x[i] = (float)(rng.NextDouble() * 100);
            y[i] = (float)(2.5 + (0.8 * x[i]) + (0.1 * Gaussian(rng)));
            if (i % 5 == 0)
            {
                y[i] += (float)(50 + (100 * rng.NextDouble()));
            }
        }

        var line = LeastAbsoluteDeviation.Fit(x, y, startSlope: 1.0);

        output.WriteLine($"slope {line.Slope:F5}, offset {line.Offset:F4}, mean absolute deviation {line.MeanAbsoluteDeviation:F3}");
        line.Slope.ShouldBe(0.8, 0.002);
        line.Offset.ShouldBe(2.5, 0.1);
    }

    /// <summary>
    /// A channel made from the reference by a known line and its own noise, with its brightest stars clipped flat, comes
    /// back with that line: the clipped cores are outside the 0.92 window and carry no weight.
    /// </summary>
    [Fact]
    public void AChannelIsPutOnTheReferencesScaleAndItsClippedCoresCarryNoWeight()
    {
        const double offset = 120;
        const double slope = 1.7;
        var truth = Field(seed: 5);
        var rng = new Random(6);
        var reference = new float[Size, Size];
        var target = new float[Size, Size];
        var clip = 0f;
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                clip = Math.Max(clip, truth[y, x]);
            }
        }
        clip = (float)((0.5 * clip - offset) / slope);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                reference[y, x] = (float)(truth[y, x] + (5 * Gaussian(rng)));
                target[y, x] = Math.Min(clip, (float)(((truth[y, x] - offset) / slope) + (3 * Gaussian(rng))));
            }
        }
        target[7, 9] = float.NaN;
        var referenceImage = Plane(reference);
        var targetImage = Plane(target);

        var fit = LinearFit.Measure(targetImage, referenceImage);
        var fitted = LinearFit.Apply(targetImage, fit);

        output.WriteLine($"offset {fit.Offset:F3} (true {offset}), slope {fit.Slope:F5} (true {slope}), {fit.Pixels} of {Size * Size} pixels");
        fit.Slope.ShouldBe(slope, slope * 0.005);
        fit.Offset.ShouldBe(offset, 3.0);
        fit.Pixels.ShouldBeLessThan(Size * Size - 1);
        float.IsNaN(fitted.GetChannelSpan(0)[(7 * Size) + 9]).ShouldBeTrue();
        fitted.GetChannelSpan(0)[(20 * Size) + 30].ShouldBe((float)(fit.Offset + (fit.Slope * target[20, 30])), 1e-3f);
    }

    /// <summary>
    /// The reason the luminance is weighted by noise over blocks: a sub-pixel blur halves the noise neighbouring pixels
    /// read, and leaves the noise of 4 px blocks nearly alone. White noise reads the same either way.
    /// </summary>
    [Fact]
    public void BlockNoiseReadsABlurredChannelNearlyAsItsTrueNoise()
    {
        var rng = new Random(11);
        var white = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                white[y, x] = (float)(100 + Gaussian(rng));
            }
        }
        var whiteImage = Plane(white);
        var blurred = whiteImage.GaussianBlur(0.55f);

        var whitePixels = SyntheticLuminance.Noise(whiteImage);
        var whiteBlocks = SyntheticLuminance.BlockNoise(whiteImage);
        var blurredPixels = SyntheticLuminance.Noise(blurred);
        var blurredBlocks = SyntheticLuminance.BlockNoise(blurred);

        output.WriteLine($"white: pixels {whitePixels:F3}, blocks {whiteBlocks:F3}; blurred 0.55 px: pixels {blurredPixels:F3}, blocks {blurredBlocks:F3}");
        whitePixels.ShouldBe(1.0, 0.03);
        whiteBlocks.ShouldBe(1.0, 0.08);
        blurredPixels.ShouldBeLessThan(0.6);
        blurredBlocks.ShouldBeGreaterThan(0.78);
    }

    // A sky, a nebula across the frame and stars: a spread of levels the line is fitted over.
    private static float[,] Field(int seed)
    {
        var rng = new Random(seed);
        var plane = new float[Size, Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                plane[y, x] = (float)(300 + (2000.0 * x / Size) + (800 * Math.Exp(-(((x - 160) * (x - 160)) + ((y - 90) * (y - 90))) / 3000.0)));
            }
        }
        for (var s = 0; s < 60; s++)
        {
            var cx = rng.NextDouble() * Size;
            var cy = rng.NextDouble() * Size;
            var peak = 2000 + (rng.NextDouble() * 30000);
            for (var y = Math.Max(0, (int)cy - 6); y < Math.Min(Size, (int)cy + 7); y++)
            {
                for (var x = Math.Max(0, (int)cx - 6); x < Math.Min(Size, (int)cx + 7); x++)
                {
                    plane[y, x] += (float)(peak * Math.Exp(-(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) / 2.0));
                }
            }
        }
        return plane;
    }

    private static Image Plane(float[,] plane)
    {
        var max = float.NegativeInfinity;
        var min = float.PositiveInfinity;
        foreach (var v in plane)
        {
            if (float.IsFinite(v))
            {
                max = Math.Max(max, v);
                min = Math.Min(min, v);
            }
        }
        return new Image([plane], BitDepth.Float32, max, min, 0f, new ImageMeta());
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
