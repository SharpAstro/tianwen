using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

public static class BackgroundNeutralizationTests
{
    [Fact]
    public static void ComputeGains_UniformBackground_ReturnsIdentity()
    {
        // When all channels have equal background, no neutralization needed
        Span<float> bg = [0.1f, 0.1f, 0.1f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        gains.R.ShouldBe(1f, 0.001f);
        gains.G.ShouldBe(1f, 0.001f);
        gains.B.ShouldBe(1f, 0.001f);
    }

    [Fact]
    public static void ComputeGains_RedCast_ReducesRedChannel()
    {
        // Red background is higher (red cast) — red should be darkened (g > 1)
        Span<float> bg = [0.2f, 0.1f, 0.1f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        // t = (0.2+0.1+0.1)/3 = 0.133
        // gR = (1-0.133)/(1-0.2) = 0.867/0.8 = 1.084
        // gG = (1-0.133)/(1-0.1) = 0.867/0.9 = 0.963
        gains.R.ShouldBeGreaterThan(1f); // darken red (g>1)
        gains.G.ShouldBeLessThan(1f);    // brighten green (g<1)
        gains.B.ShouldBeLessThan(1f);    // brighten blue (g<1)
    }

    [Fact]
    public static void ComputeGains_BlueCast_ReducesBlueChannel()
    {
        // Blue background is higher (blue cast) — blue should be darkened (g > 1)
        Span<float> bg = [0.05f, 0.05f, 0.15f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        gains.B.ShouldBeGreaterThan(1f);  // darken blue (g>1)
        gains.R.ShouldBeLessThan(1f);     // brighten red (g<1)
    }

    [Fact]
    public static void ComputeGains_GreenCast_ReducesGreenChannel()
    {
        // Green background is higher (green cast) — green should be darkened (g > 1)
        Span<float> bg = [0.05f, 0.15f, 0.05f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        gains.G.ShouldBeGreaterThan(1f);  // darken green (g>1)
        gains.R.ShouldBeLessThan(1f);    // brighten red (g<1)
    }

    [Fact]
    public static void ComputeGains_ClampsWithinReasonableRange()
    {
        // Extreme case: one channel very bright, others very dark
        Span<float> bg = [0.01f, 0.5f, 0.01f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        gains.R.ShouldBeInRange(0f, 10f);
        gains.G.ShouldBeInRange(0f, 10f);
        gains.B.ShouldBeInRange(0f, 10f);
    }

    [Fact]
    public static void ComputeGains_FewerThan3Channels_ReturnsIdentity()
    {
        Span<float> bg2 = [0.1f, 0.2f];
        var gains = BackgroundNeutralization.ComputeGains(bg2);
        gains.R.ShouldBe(1f);
        gains.G.ShouldBe(1f);
        gains.B.ShouldBe(1f);
    }

    [Fact]
    public static void Apply_NeutralizesBackgroundInImageData()
    {
        // Create a 3-channel image with a red-biased background
        float[][,] data =
        [
            new float[2, 2], // R channel — all 0.2
            new float[2, 2], // G channel — all 0.1
            new float[2, 2], // B channel — all 0.1
        ];
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++)
            {
                data[0][y, x] = 0.2f;
                data[1][y, x] = 0.1f;
                data[2][y, x] = 0.1f;
            }

        Span<float> bg = [0.2f, 0.1f, 0.1f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        BackgroundNeutralization.Apply(data, gains);

        // After neutralization, all channels should be closer to the mean (0.133)
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++)
            {
                // Red was 0.2, should decrease. gR > 1, offset < 0
                data[0][y, x].ShouldBeLessThan(0.2f);
                // Green was 0.1, should increase. gG < 1, offset > 0
                data[1][y, x].ShouldBeGreaterThan(0.09f);
                data[2][y, x].ShouldBeGreaterThan(0.09f);
                // No channel should go negative
                data[0][y, x].ShouldBeGreaterThanOrEqualTo(0f);
                data[1][y, x].ShouldBeGreaterThanOrEqualTo(0f);
                data[2][y, x].ShouldBeGreaterThanOrEqualTo(0f);
            }
    }

    [Fact]
    public static void Apply_DoesNotModifyNaN()
    {
        float[][,] data =
        [
            new float[1, 1],
            new float[1, 1],
            new float[1, 1],
        ];
        data[0][0, 0] = float.NaN;
        data[1][0, 0] = 0.1f;
        data[2][0, 0] = 0.1f;

        var gains = (1f, 1f, 1f);
        BackgroundNeutralization.Apply(data, gains);

        float.IsNaN(data[0][0, 0]).ShouldBeTrue();
        data[1][0, 0].ShouldBe(0.1f);
    }

    [Fact]
    public static void ApplyToChannel_PedestalSubtractsThenNeutralizes()
    {
        // Formula: out = max((val - ped) * g + (1-g), 0)
        // With pedestal=0.1, val=0.3, g=0.8:
        // out = max((0.3-0.1)*0.8 + 0.2, 0) = max(0.2*0.8+0.2, 0) = max(0.36, 0) = 0.36
        var result = BackgroundNeutralization.ApplyToChannel(0.3f, 0.8f, 0.1f);
        result.ShouldBe(0.36f, 0.001f);
    }

    [Fact]
    public static void ApplyToChannel_ClampsNegativeToZero()
    {
        // When val < ped and g is large, result could go negative
        var result = BackgroundNeutralization.ApplyToChannel(0.05f, 10f, 0.1f);
        result.ShouldBe(0f);
    }
}
