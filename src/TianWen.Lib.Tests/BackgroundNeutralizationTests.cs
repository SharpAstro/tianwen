using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public class BackgroundNeutralizationTests(ITestOutputHelper output)
{
    [Fact]
    public void ComputeGains_UniformBackground_ReturnsIdentity()
    {
        Span<float> bg = [0.1f, 0.1f, 0.1f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        output.WriteLine($"Uniform bg=[0.1,0.1,0.1] → g=({gains.R:F4},{gains.G:F4},{gains.B:F4})");
        gains.R.ShouldBe(1f, 0.001f);
        gains.G.ShouldBe(1f, 0.001f);
        gains.B.ShouldBe(1f, 0.001f);
    }

    [Fact]
    public void ComputeGains_RedCast_ReducesRedChannel()
    {
        Span<float> bg = [0.2f, 0.1f, 0.1f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        output.WriteLine($"Red cast bg=[0.2,0.1,0.1] → g=({gains.R:F4},{gains.G:F4},{gains.B:F4})");
        gains.R.ShouldBeGreaterThan(1f); // darken red (g>1)
        gains.G.ShouldBeLessThan(1f);    // brighten green (g<1)
        gains.B.ShouldBeLessThan(1f);    // brighten blue (g<1)
    }

    [Fact]
    public void ComputeGains_BlueCast_ReducesBlueChannel()
    {
        Span<float> bg = [0.05f, 0.05f, 0.15f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        output.WriteLine($"Blue cast bg=[0.05,0.05,0.15] → g=({gains.R:F4},{gains.G:F4},{gains.B:F4})");
        gains.B.ShouldBeGreaterThan(1f);  // darken blue (g>1)
        gains.R.ShouldBeLessThan(1f);     // brighten red (g<1)
    }

    [Fact]
    public void ComputeGains_GreenCast_ReducesGreenChannel()
    {
        Span<float> bg = [0.05f, 0.15f, 0.05f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        output.WriteLine($"Green cast bg=[0.05,0.15,0.05] → g=({gains.R:F4},{gains.G:F4},{gains.B:F4})");
        gains.G.ShouldBeGreaterThan(1f);  // darken green (g>1)
        gains.R.ShouldBeLessThan(1f);    // brighten red (g<1)
    }

    [Fact]
    public void ComputeGains_ClampsWithinReasonableRange()
    {
        Span<float> bg = [0.01f, 0.5f, 0.01f];
        var gains = BackgroundNeutralization.ComputeGains(bg);
        output.WriteLine($"Extreme bg=[0.01,0.5,0.01] → g=({gains.R:F4},{gains.G:F4},{gains.B:F4})");
        gains.R.ShouldBeInRange(0f, 10f);
        gains.G.ShouldBeInRange(0f, 10f);
        gains.B.ShouldBeInRange(0f, 10f);
    }

    [Fact]
    public void ComputeGains_FewerThan3Channels_ReturnsIdentity()
    {
        Span<float> bg2 = [0.1f, 0.2f];
        var gains = BackgroundNeutralization.ComputeGains(bg2);
        output.WriteLine($"2-chan bg=[0.1,0.2] → g=({gains.R:F4},{gains.G:F4},{gains.B:F4})");
        gains.R.ShouldBe(1f);
        gains.G.ShouldBe(1f);
        gains.B.ShouldBe(1f);
    }

    [Fact]
    public void Apply_NeutralizesBackgroundInImageData()
    {
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
        output.WriteLine($"Before: R={data[0][0,0]:F4} G={data[1][0,0]:F4} B={data[2][0,0]:F4}");
        BackgroundNeutralization.Apply(data, gains);
        output.WriteLine($"After:  R={data[0][0,0]:F4} G={data[1][0,0]:F4} B={data[2][0,0]:F4}");

        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 2; x++)
            {
                data[0][y, x].ShouldBeLessThan(0.2f);
                data[1][y, x].ShouldBeGreaterThan(0.09f);
                data[2][y, x].ShouldBeGreaterThan(0.09f);
                data[0][y, x].ShouldBeGreaterThanOrEqualTo(0f);
                data[1][y, x].ShouldBeGreaterThanOrEqualTo(0f);
                data[2][y, x].ShouldBeGreaterThanOrEqualTo(0f);
            }
    }

    [Fact]
    public void Apply_DoesNotModifyNaN()
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
    public void ApplyToChannel_PedestalSubtractsThenNeutralizes()
    {
        var result = BackgroundNeutralization.ApplyToChannel(0.3f, 0.8f, 0.1f);
        output.WriteLine($"val=0.3 ped=0.1 g=0.8 → out={result:F4}");
        result.ShouldBe(0.36f, 0.001f);
    }

    [Fact]
    public void ApplyToChannel_ClampsNegativeToZero()
    {
        var result = BackgroundNeutralization.ApplyToChannel(0.05f, 10f, 0.1f);
        output.WriteLine($"val=0.05 ped=0.1 g=10 → out={result:F4}");
        result.ShouldBe(0f);
    }

    [Fact]
    public async Task RealFile_ScanBackgroundAndComputeGains()
    {
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(
            "Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop",
            cancellationToken: TestContext.Current.CancellationToken);

        output.WriteLine($"Loaded: {image.Width}×{image.Height}×{image.ChannelCount}");
        output.WriteLine($"SensorType: {image.ImageMeta.SensorType}");
        output.WriteLine($"MaxValue: {image.MaxValue:F2}  MinValue: {image.MinValue:F4}");

        // Compute pedestals (same as viewer pipeline)
        Span<float> pedestals = stackalloc float[image.ChannelCount];
        for (var c = 0; c < image.ChannelCount; c++)
            pedestals[c] = image.GetPedestralMedianAndMADScaledToUnit(c).Pedestral;

        var bgResult = image.ScanBackgroundRegion(pedestals, squareSize: 48);
        var perChannelBg = bgResult.PerChannel;
        var lumaBg = bgResult.Luma;

        output.WriteLine($"Pedestals: [{pedestals[0]:F4}, {pedestals[1]:F4}, {pedestals[2]:F4}]");
        output.WriteLine($"Background (ped-sub): R={perChannelBg[0]:F6} G={perChannelBg[1]:F6} B={perChannelBg[2]:F6}");
        output.WriteLine($"Background luma: {lumaBg:F6}");

        var gains = BackgroundNeutralization.ComputeGains(perChannelBg);
        output.WriteLine($"Neutralization gains: R={gains.R:F4} G={gains.G:F4} B={gains.B:F4}");

        gains.R.ShouldBeInRange(0f, 10f);
        gains.G.ShouldBeInRange(0f, 10f);
        gains.B.ShouldBeInRange(0f, 10f);
    }
}
