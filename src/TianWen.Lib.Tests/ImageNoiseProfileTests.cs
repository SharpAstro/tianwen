using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for <see cref="Image.EstimateNoiseProfile"/> -- the per-channel
/// robust noise σ estimator (MAD × 1.4826) used by the AI sharpen pipeline
/// to log per-stage noise reduction. Verifies the estimator recovers
/// injected Gaussian σ within tolerance, handles mono / RGB, and produces
/// the expected per-channel ordering when channels have different noise.
/// </summary>
[Collection("Imaging")]
public class ImageNoiseProfileTests
{
    // 5% relative tolerance is comfortable for the MAD->σ bias at N = 256x256
    // = 65k samples. Theoretical bias < 1% but rounded medians from the
    // 65k-bin histogram add a small bin-quantisation error.
    private const float Tolerance = 0.05f;

    private static Image MakeNoisyImage(int width, int height, float bgLevel, double[] channelSigmas, int seed = 42)
    {
        var channels = channelSigmas.Length;
        var data = new float[channels][,];
        var rng = new Random(seed);
        for (var c = 0; c < channels; c++)
        {
            var arr = new float[height, width];
            var sigma = channelSigmas[c];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    // Box-Muller: two uniforms -> one standard Normal sample.
                    // The second sample is discarded (we never need pairs).
                    var u1 = 1.0 - rng.NextDouble();
                    var u2 = 1.0 - rng.NextDouble();
                    var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                    arr[y, x] = bgLevel + (float)(z * sigma);
                }
            }
            data[c] = arr;
        }
        return new Image(data, BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, default);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(0.005)]
    [InlineData(0.01)]
    [InlineData(0.05)]
    public void EstimateNoiseProfile_Mono_RecoversInjectedSigma(double injectedSigma)
    {
        var image = MakeNoisyImage(width: 256, height: 256, bgLevel: 0.5f,
            channelSigmas: [injectedSigma]);

        var profile = image.EstimateNoiseProfile();

        profile.Length.ShouldBe(1);
        // Recovered σ should match injected within 5% (MAD->σ bias is much
        // smaller than this; tolerance is dominated by histogram binning).
        profile[0].ShouldBe((float)injectedSigma, tolerance: (float)(injectedSigma * Tolerance));
    }

    [Fact]
    public void EstimateNoiseProfile_Rgb_RecoversPerChannelOrdering()
    {
        // Inject distinct noise levels in each channel; verify the recovered
        // ordering matches AND each channel is within tolerance.
        var sigmas = new[] { 0.002, 0.008, 0.020 };
        var image = MakeNoisyImage(width: 256, height: 256, bgLevel: 0.5f, channelSigmas: sigmas);

        var profile = image.EstimateNoiseProfile();

        profile.Length.ShouldBe(3);
        for (var c = 0; c < 3; c++)
        {
            profile[c].ShouldBe((float)sigmas[c], tolerance: (float)(sigmas[c] * Tolerance));
        }
        // Ordering preserved (smallest -> largest by construction).
        profile[0].ShouldBeLessThan(profile[1]);
        profile[1].ShouldBeLessThan(profile[2]);
    }

    [Fact]
    public void EstimateNoiseProfile_ConstantImage_ReturnsBelowQuantisationLimit()
    {
        // No noise at all -> σ should collapse to the MAD floor
        // (half a histogram bin width per GetPedestralMedianAndMADScaledToUnit
        // guard). At 65535-step quantisation that's ~7.6e-6, then x 1.4826
        // -> ~1.13e-5. We don't assert the exact floor since it depends on
        // RescaledMaxValue resolution; just that the noise is below the
        // smallest sigma the per-channel estimator can actually resolve.
        const float MadFloorUpperBound = 1e-4f;
        var data = new float[1][,];
        var arr = new float[64, 64];
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                arr[y, x] = 0.5f;
        data[0] = arr;
        var image = new Image(data, BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, default);

        var profile = image.EstimateNoiseProfile();

        profile.Length.ShouldBe(1);
        profile[0].ShouldBeLessThan(MadFloorUpperBound);
    }

    [Fact]
    public void EstimateNoiseProfile_RejectsBrightOutliersViaMad()
    {
        // Build a noisy channel with bright "star" outliers sprinkled on top.
        // MAD around the median is robust to these outliers, so the recovered
        // σ should still match the injected background σ -- proving the
        // estimator can be used for noise tracking without needing explicit
        // star masking.
        const double injectedSigma = 0.005;
        const int Size = 256;
        const float BgLevel = 0.5f;
        var data = new float[1][,];
        var arr = new float[Size, Size];
        var rng = new Random(42);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var u1 = 1.0 - rng.NextDouble();
                var u2 = 1.0 - rng.NextDouble();
                var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
                arr[y, x] = BgLevel + (float)(z * injectedSigma);
            }
        }
        // Add ~1% bright outliers (saturated "stars") on top of the noisy bg.
        var starRng = new Random(7);
        for (var i = 0; i < Size * Size / 100; i++)
        {
            var x = starRng.Next(Size);
            var y = starRng.Next(Size);
            arr[y, x] = 0.99f;
        }
        data[0] = arr;
        var image = new Image(data, BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: 0f, default);

        var profile = image.EstimateNoiseProfile();
        // Recovered σ should still match the background σ within tolerance,
        // not be inflated by the bright outliers.
        profile[0].ShouldBe((float)injectedSigma, tolerance: (float)(injectedSigma * Tolerance));
    }
}
