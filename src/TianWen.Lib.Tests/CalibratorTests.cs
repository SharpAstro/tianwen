using System;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class CalibratorTests
{
    // 5x3 mono test images (15 floats) — deliberately not a multiple of
    // Vector<float>.Count so the SIMD tail in Image.Subtract / Image.Divide
    // also gets exercised transitively.
    private static Image Mono(float[] values, int width = 5, int height = 3)
    {
        values.Length.ShouldBe(width * height);
        var arr = new float[height, width];
        for (var i = 0; i < values.Length; i++)
        {
            arr[i / width, i % width] = values[i];
        }
        return Image.FromChannel(arr, maxValue: 1f, minValue: 0f);
    }

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
    public void Apply_NoMasters_ReturnsInputUnchanged()
    {
        var values = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 0.15f, 0.25f, 0.35f, 0.45f, 0.55f };
        var light = Mono(values);

        var result = new Calibrator().Apply(light);

        Flatten(result).ShouldBe(values, tolerance: 1e-6);
    }

    [Fact]
    public void Apply_BiasOnly_SubtractsBias()
    {
        var light = Mono(new[] { 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f });
        var bias = Mono(new[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f });

        var result = new Calibrator(Bias: bias).Apply(light);

        result[0, 0, 0].ShouldBe(0.4f, tolerance: 1e-6f);
        result[0, 2, 4].ShouldBe(0.8f, tolerance: 1e-6f);
    }

    [Fact]
    public void Apply_DarkOnly_SubtractsWithPedestalAndClamp()
    {
        // Light bg: 0.10, dark mean: 0.30 -> would clamp to 0 without pedestal.
        // With pedestal 0.25: (0.10 - 0.30) + 0.25 = 0.05 (no clamp triggered).
        var light = Mono(new[] { 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f });
        var dark = Mono(new[]  { 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f, 0.30f });

        var calNoPedestal = new Calibrator(Dark: dark, Pedestal: 0f).Apply(light);
        var calWithPedestal = new Calibrator(Dark: dark, Pedestal: 0.25f).Apply(light);

        calNoPedestal[0, 0, 0].ShouldBe(0f); // clamped
        calWithPedestal[0, 0, 0].ShouldBe(0.05f, tolerance: 1e-6f); // pedestal-rescued
    }

    [Fact]
    public void Apply_FlatOnly_DividesByFlat()
    {
        var light = Mono(new[] { 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f, 0.50f });
        // Flat with vignetting profile: corners 0.5, center 1.0
        var flat = Mono(new[] { 0.5f, 0.6f, 0.7f, 0.6f, 0.5f, 0.8f, 1.0f, 1.0f, 1.0f, 0.8f, 0.5f, 0.6f, 0.7f, 0.6f, 0.5f });

        var result = new Calibrator(Flat: flat).Apply(light);

        // Corner pixels (flat 0.5) get boosted; center (flat 1.0) stays at 0.5.
        result[0, 0, 0].ShouldBe(0.50f / 0.5f, tolerance: 1e-5f); // 1.0
        result[0, 1, 1].ShouldBe(0.50f / 1.0f, tolerance: 1e-5f); // 0.5
    }

    [Fact]
    public void Apply_AllThreeMasters_AppliesInOrder()
    {
        // light - bias - dark + pedestal = (0.6 - 0.1 - 0.2 + 0.05) = 0.35
        // then / flat (1.0) = 0.35
        var light = Mono(new[] { 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f, 0.6f });
        var bias  = Mono(new[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f });
        var dark  = Mono(new[] { 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f });
        var flat  = Mono(new[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f });

        var result = new Calibrator(Bias: bias, Dark: dark, Flat: flat, Pedestal: 0.05f).Apply(light);

        result[0, 0, 0].ShouldBe(0.35f, tolerance: 1e-5f);
    }

    [Fact]
    public void Apply_FlatNearZero_ClampsToEpsilonNotInf()
    {
        var light = Mono(new[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f });
        var flat  = Mono(new[] { 0.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f });

        var result = new Calibrator(Flat: flat, FlatEpsilon: 1e-3f).Apply(light);

        // Dead pixel at (0,0) — 1.0 / 1e-3 = 1000, NOT inf.
        result[0, 0, 0].ShouldBe(1000f, tolerance: 1e-2f);
        result[0, 0, 1].ShouldBe(1.0f, tolerance: 1e-5f); // normal pixel
    }

    [Fact]
    public void ApplyTile_AllMasters_MatchesWholeFrameApplyOverSameRegion()
    {
        // Pick a 2x2 tile out of the 5x3 frame and verify ApplyTile produces
        // the same values as the corresponding pixels of Apply.
        var light = Mono(new[] { 0.50f, 0.60f, 0.70f, 0.80f, 0.90f, 0.55f, 0.65f, 0.75f, 0.85f, 0.95f, 0.45f, 0.55f, 0.65f, 0.75f, 0.85f });
        var bias  = Mono(new[] { 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f, 0.10f });
        var dark  = Mono(new[] { 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f, 0.20f });
        var flat  = Mono(new[] { 0.95f, 0.95f, 1.00f, 1.00f, 1.00f, 0.95f, 0.95f, 1.00f, 1.00f, 1.00f, 0.95f, 0.95f, 1.00f, 1.00f, 1.00f });

        var cal = new Calibrator(Bias: bias, Dark: dark, Flat: flat, Pedestal: 0.05f);
        var whole = cal.Apply(light);

        // Region: x=1, y=0, 3x2 (columns 1-3, rows 0-1)
        const int rx = 1, ry = 0, rw = 3, rh = 2;
        var srcTile = new float[rw * rh];
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
                srcTile[y * rw + x] = light[0, ry + y, rx + x];

        var dstTile = new float[rw * rh];
        cal.ApplyTile(srcTile, channel: 0, regionX: rx, regionY: ry, regionWidth: rw, regionHeight: rh, dstTile);

        for (var y = 0; y < rh; y++)
        {
            for (var x = 0; x < rw; x++)
            {
                dstTile[y * rw + x].ShouldBe(whole[0, ry + y, rx + x], tolerance: 1e-5f);
            }
        }
    }

    [Fact]
    public void ApplyTile_BadLengths_Throws()
    {
        var cal = new Calibrator();
        var src = new float[6];
        var dst = new float[5]; // wrong size

        Should.Throw<ArgumentException>(() =>
            cal.ApplyTile(src, 0, 0, 0, 3, 2, dst));
    }

    [Fact]
    public void ApplyTile_RegionOutOfBounds_Throws()
    {
        var bias = Mono(new[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f });
        var cal = new Calibrator(Bias: bias);
        var src = new float[4];
        var dst = new float[4];

        // Region (4, 1) 2x2 hits master bounds (5x3) at x=5,6 which is out
        Should.Throw<ArgumentException>(() =>
            cal.ApplyTile(src, 0, 4, 1, 2, 2, dst));
    }
}
