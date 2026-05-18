using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class ImageArithmeticTests
{
    // 5x3 buffers produce 15 floats — deliberately NOT a multiple of
    // Vector<float>.Count (4 / 8 / 16) so each test exercises both the SIMD
    // body and the scalar tail of SubtractClampVec / DivideClampVec.
    private static float[,] MakeChannel(float[] flat, int height = 3, int width = 5)
    {
        flat.Length.ShouldBe(height * width);
        var arr = new float[height, width];
        for (var h = 0; h < height; h++)
        {
            for (var w = 0; w < width; w++)
            {
                arr[h, w] = flat[h * width + w];
            }
        }
        return arr;
    }

    private static Image Mono(params float[] values) =>
        Image.FromChannel(MakeChannel(values), maxValue: 1f, minValue: 0f);

    private static float[] Flatten(Image image, int channel = 0)
    {
        var arr = new float[image.Height * image.Width];
        for (var h = 0; h < image.Height; h++)
        {
            for (var w = 0; w < image.Width; w++)
            {
                arr[h * image.Width + w] = image[channel, h, w];
            }
        }
        return arr;
    }

    [Fact]
    public void Subtract_subtractsElementWise()
    {
        var a = Mono(0.5f, 0.4f, 0.3f, 0.2f, 0.1f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 0.55f, 0.45f, 0.35f, 0.25f, 0.15f);
        var b = Mono(0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.05f, 0.05f, 0.05f, 0.05f, 0.05f);

        var result = a.Subtract(b);

        var expected = new[] { 0.4f, 0.3f, 0.2f, 0.1f, 0.0f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.5f, 0.4f, 0.3f, 0.2f, 0.1f };
        Flatten(result).ShouldBe(expected, tolerance: 1e-6);
    }

    [Fact]
    public void Subtract_clampsNegativeResultsToZero()
    {
        // Light has lower values than dark in some pixels -> would go negative without clamp
        var light = Mono(0.10f, 0.20f, 0.05f, 0.30f, 0.15f, 0.20f, 0.10f, 0.25f, 0.05f, 0.30f, 0.40f, 0.10f, 0.20f, 0.05f, 0.30f);
        var dark  = Mono(0.30f, 0.10f, 0.20f, 0.10f, 0.40f, 0.10f, 0.30f, 0.10f, 0.20f, 0.10f, 0.20f, 0.50f, 0.10f, 0.30f, 0.20f);

        var result = light.Subtract(dark);

        foreach (var v in Flatten(result))
        {
            v.ShouldBeGreaterThanOrEqualTo(0f);
        }
        // Pixels where light >= dark should pass through unchanged
        result[0, 0, 1].ShouldBe(0.10f, tolerance: 1e-6f); // 0.20 - 0.10
        // Pixels where light < dark should be exactly 0
        result[0, 0, 0].ShouldBe(0f); // 0.10 - 0.30 -> -0.20 -> 0
        result[0, 0, 4].ShouldBe(0f); // 0.15 - 0.40 -> -0.25 -> 0
    }

    [Fact]
    public void Subtract_appliesAddedPedestal()
    {
        var a = Mono(0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.10f, 0.20f, 0.30f, 0.40f, 0.50f);
        var b = Mono(0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f);

        var result = a.Subtract(b, addedPedestal: 0.25f);

        // (0.10 - 0.30) + 0.25 = 0.05 (no clamp triggered after pedestal addition)
        result[0, 0, 0].ShouldBe(0.05f, tolerance: 1e-6f);
        // (0.30 - 0.30) + 0.25 = 0.25
        result[0, 0, 2].ShouldBe(0.25f, tolerance: 1e-6f);
        // (0.50 - 0.30) + 0.25 = 0.45
        result[0, 0, 4].ShouldBe(0.45f, tolerance: 1e-6f);
    }

    [Fact]
    public void Subtract_pedestalStillRespectsClampWhenSumGoesNegative()
    {
        // Large dark, small pedestal -> result still negative even after pedestal
        var a = Mono(0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f);
        var b = Mono(0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f, 0.80f);

        var result = a.Subtract(b, addedPedestal: 0.10f);

        foreach (var v in Flatten(result))
        {
            v.ShouldBe(0f);
        }
    }

    [Fact]
    public void Subtract_returnsFloat32_throwsOnShapeMismatch()
    {
        var a = Mono(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f);
        var b = Image.FromChannel(MakeChannel([1f, 2f, 3f, 4f, 5f, 6f], 2, 3));

        Should.Throw<ArgumentException>(() => a.Subtract(b));
    }

    [Fact]
    public void Divide_dividesElementWise()
    {
        var num = Mono(1.00f, 2.00f, 4.00f, 0.50f, 8.00f, 1.50f, 3.00f, 6.00f, 0.25f, 9.00f, 1.20f, 2.40f, 4.80f, 0.10f, 7.20f);
        var den = Mono(1.00f, 2.00f, 4.00f, 0.50f, 8.00f, 1.50f, 3.00f, 6.00f, 0.25f, 9.00f, 1.20f, 2.40f, 4.80f, 0.10f, 7.20f);

        var result = num.Divide(den);

        // num == den, so result should be all 1.0
        foreach (var v in Flatten(result))
        {
            v.ShouldBe(1f, tolerance: 1e-5f);
        }
    }

    [Fact]
    public void Divide_clampsNearZeroDenominator()
    {
        var num = Mono(1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f);
        var den = Mono(0.0f, 0.5f, 1.0f, 1e-9f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f);

        var result = num.Divide(den, epsilon: 1e-3f);

        // den = 0 -> clamped to 1e-3, result = 1 / 1e-3 = 1000
        result[0, 0, 0].ShouldBe(1000f, tolerance: 1e-2f);
        // den = 0.5 -> not clamped, result = 1 / 0.5 = 2
        result[0, 0, 1].ShouldBe(2f, tolerance: 1e-5f);
        // den = 1.0 -> not clamped, result = 1
        result[0, 0, 2].ShouldBe(1f, tolerance: 1e-5f);
        // den = 1e-9 < epsilon -> clamped to 1e-3, result = 1000
        result[0, 0, 3].ShouldBe(1000f, tolerance: 1e-2f);
    }

    [Fact]
    public void Multiply_multipliesElementWise()
    {
        var a = Mono(0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.60f, 0.70f, 0.80f, 0.90f, 1.00f, 0.15f, 0.25f, 0.35f, 0.45f, 0.55f);
        var b = Mono(2.00f, 2.00f, 2.00f, 2.00f, 2.00f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 4.00f, 4.00f, 4.00f, 4.00f, 4.00f);

        var result = a.Multiply(b);

        result[0, 0, 0].ShouldBe(0.20f, tolerance: 1e-6f);
        result[0, 1, 0].ShouldBe(0.30f, tolerance: 1e-6f);
        result[0, 2, 0].ShouldBe(0.60f, tolerance: 1e-6f);
    }

    [Fact]
    public void AddInPlace_mutatesLeftOperand()
    {
        var accumulator = Mono(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 0.15f, 0.25f, 0.35f, 0.45f, 0.55f);
        var frame = Mono(0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f);

        accumulator.AddInPlace(frame);

        accumulator[0, 0, 0].ShouldBe(0.6f, tolerance: 1e-6f);
        accumulator[0, 1, 0].ShouldBe(1.1f, tolerance: 1e-6f);
        accumulator[0, 2, 0].ShouldBe(0.65f, tolerance: 1e-6f);
    }

    [Fact]
    public void Operations_handleMultiChannel()
    {
        var ch0 = MakeChannel([0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.60f, 0.70f, 0.80f, 0.90f, 1.00f, 0.15f, 0.25f, 0.35f, 0.45f, 0.55f]);
        var ch1 = MakeChannel([1.00f, 0.90f, 0.80f, 0.70f, 0.60f, 0.50f, 0.40f, 0.30f, 0.20f, 0.10f, 0.85f, 0.75f, 0.65f, 0.55f, 0.45f]);
        var ch2 = MakeChannel([0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f]);
        var image = new Image([ch0, ch1, ch2], BitDepth.Float32, 1f, 0f, 0f, default);

        var darkCh0 = MakeChannel(new float[15]); // zeros
        var darkCh1 = MakeChannel(new float[15]);
        var darkCh2 = MakeChannel(new float[15]);
        var zeros = new Image([darkCh0, darkCh1, darkCh2], BitDepth.Float32, 1f, 0f, 0f, default);

        var result = image.Subtract(zeros);

        result.ChannelCount.ShouldBe(3);
        // Subtraction by zero should be identity per channel
        result[0, 0, 0].ShouldBe(0.10f, tolerance: 1e-6f);
        result[1, 0, 0].ShouldBe(1.00f, tolerance: 1e-6f);
        result[2, 0, 0].ShouldBe(0.50f, tolerance: 1e-6f);
    }

    [Fact]
    public void SimdAndScalarPathsAgree()
    {
        // Confirm the scalar tail and SIMD body produce identical results.
        // Vector<float>.Count is 4/8/16; using length 17 forces a tail in all cases.
        var width = Vector<float>.Count;
        width.ShouldBeOneOf(4, 8, 16); // sanity check the test premise
        var len = 17;
        var aFlat = new float[len];
        var bFlat = new float[len];
        for (var i = 0; i < len; i++)
        {
            aFlat[i] = 0.5f + i * 0.01f;
            bFlat[i] = 0.1f + i * 0.005f;
        }

        var a = Image.FromChannel(MakeChannel(aFlat, height: 1, width: 17), maxValue: 1f, minValue: 0f);
        var b = Image.FromChannel(MakeChannel(bFlat, height: 1, width: 17), maxValue: 1f, minValue: 0f);

        var resultSub = a.Subtract(b);
        var resultDiv = a.Divide(b);

        // Cross-check against scalar reference loop
        for (var i = 0; i < len; i++)
        {
            var expectedSub = MathF.Max(aFlat[i] - bFlat[i], 0f);
            resultSub[0, 0, i].ShouldBe(expectedSub, tolerance: 1e-6f);
            var expectedDiv = aFlat[i] / MathF.Max(bFlat[i], 1e-6f);
            resultDiv[0, 0, i].ShouldBe(expectedDiv, tolerance: 1e-4f);
        }
    }
}
