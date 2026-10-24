using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The enhance denoises the starless plate in passes of its own (<c>SharpenPipeline.DenoisePassesAsync</c>), each a denoiser call
/// on the last one's output, kept while it takes enough off the coarse sky noise (<see cref="MultiScaleNoise"/>). On the Sculptor
/// master NoiseXTerminator's pass 2 took 47 % off and was the owner's choice by eye; pass 3 took 22 % and only blotched the sky.
/// </summary>
public class DenoisePassGateTests
{
    [Fact]
    public async Task APassThatTakesEnoughOffIsKeptAndTheFirstThatDoesNotEndsTheRun()
    {
        // Pass 1 halves the noise, pass 2 halves it again (50 % off, kept), pass 3 takes only 20 % (discarded), so the result
        // is pass 2's, and pass 4 is never asked for.
        var denoiser = new ScalingDenoiser(0.5f, 0.5f, 0.8f, 0.5f);
        var result = await RunAsync(denoiser, tuning: null);

        denoiser.Calls.ShouldBe(3, "pass 3 runs, is measured and is discarded; nothing runs after it");
        Deviation(result).ShouldBe(Deviation(Plate()) * 0.25f, Deviation(Plate()) * 0.01f, "the plate is pass 2's");
    }

    [Fact]
    public async Task EveryPassThatTakesEnoughOffIsKeptUpToTheCap()
    {
        var denoiser = new ScalingDenoiser(0.5f, 0.5f, 0.5f, 0.5f);
        var result = await RunAsync(denoiser, tuning: null);

        denoiser.Calls.ShouldBe(SharpenPipeline.MaxDenoisePasses);
        Deviation(result).ShouldBe(Deviation(Plate()) * 0.125f, Deviation(Plate()) * 0.01f);
    }

    [Fact]
    public async Task AStatedPassCountRunsThatManyUngated()
    {
        // Two passes asked for: both run, whatever the second takes off.
        var denoiser = new ScalingDenoiser(0.5f, 0.95f, 0.5f);
        await RunAsync(denoiser, tuning: new EnhanceTuning(DenoiseIterations: 2));

        denoiser.Calls.ShouldBe(2);
    }

    [Fact]
    public void TheMeasureReadsWhiteNoiseFallingWithScale()
    {
        // White noise: each a trous band holds less than the one finer, and the same sky is read again when asked.
        var plate = Plate();
        var noise = MultiScaleNoise.Measure(plate).ShouldNotBeNull();
        for (var j = 1; j < noise.Bands.Length; j++)
        {
            noise.Bands[j].ShouldBeLessThan(noise.Bands[j - 1]);
        }
        MultiScaleNoise.Measure(plate.Affine(0.5, 0.05), onSkyOf: noise).ShouldNotBeNull().Sky.ShouldBeSameAs(noise.Sky);
    }

    private static async Task<Image> RunAsync(ScalingDenoiser denoiser, EnhanceTuning? tuning)
    {
        var pipeline = new SharpenPipeline(starRemover: new DarkStarRemover(), denoiser: denoiser);
        var result = await pipeline.ProcessAsync(
            new SharpenRequest(Plate(), [new RemoveStarsStep(), new DenoiseStarlessStep()]),
            new EnhanceOptions(Tuning: tuning), cancellationToken: TestContext.Current.CancellationToken);
        return result.DenoisedStarless.ShouldNotBeNull();
    }

    // A flat sky at 0.1 with white noise, large enough for the 32 px band and its sky.
    private static Image Plate()
    {
        const int size = 320;
        var plane = new float[size, size];
        var random = new Random(11);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                plane[y, x] = 0.1f + (0.004f * (float)(random.NextDouble() - 0.5));
            }
        }
        return new Image([plane], BitDepth.Float32, 1f, 0f, 0f, new ImageMeta { Instrument = "synth" });
    }

    private static float Deviation(Image image)
    {
        var span = image.GetChannelSpan(0);
        var sum = 0.0;
        foreach (var v in span)
        {
            sum += v;
        }
        var mean = sum / span.Length;
        var sq = 0.0;
        foreach (var v in span)
        {
            sq += (v - mean) * (v - mean);
        }
        return (float)Math.Sqrt(sq / span.Length);
    }

    /// <summary>Scales each sample's distance from 0.1 by the next factor in turn, which scales every band's noise by it.</summary>
    private sealed class ScalingDenoiser(params float[] factors) : IDenoiseEnhancer
    {
        private readonly Queue<float> _factors = new(factors);

        public string Name => "Test/Scaling";

        public int Calls { get; private set; }

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            Calls++;
            var k = _factors.Dequeue();
            return Task.FromResult(input.Affine(k, 0.1 * (1 - k)));
        }
    }

    /// <summary>A star remover that removes nothing: the starless plate is the frame.</summary>
    private sealed class DarkStarRemover : IStarRemover
    {
        public string Name => "Test/NoStars";

        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input.Affine(1.0, 0.0));
    }
}
