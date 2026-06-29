using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pipeline mechanics for the BlurX-first (RC-Astro) shape: the
/// <see cref="DeblurStep"/> + <see cref="SharpenRequest.DeblurFirst"/> canonical.
/// Uses fakes so it runs without RC-Astro installed.
/// </summary>
public class SharpenPipelineDeblurTests
{
    /// <summary>Scales every pixel by <paramref name="scale"/>, returning a NEW
    /// image. Stands in for any role (incl. the full-image deblurrer).</summary>
    private sealed class ScaleAll(float scale)
        : IImageDeblurrer, IStarRemover, IGradientCorrector, IDenoiseEnhancer
    {
        public string Name => $"Test/ScaleAll({scale})";
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
        {
            var (channels, w, h) = input.Shape;
            var data = new float[channels][,];
            for (var c = 0; c < channels; c++)
            {
                var plane = new float[h, w];
                var src = input.GetChannelSpan(c);
                for (var i = 0; i < src.Length; i++)
                {
                    plane[i / w, i % w] = src[i] * scale;
                }
                data[c] = plane;
            }
            return Task.FromResult(new Image(data, BitDepth.Float32, 1.0f, 0f, 0f, input.ImageMeta));
        }
    }

    /// <summary>Returns the input unchanged -- the unlicensed-bxt no-op the
    /// pipeline must detect and skip.</summary>
    private sealed class PassthroughDeblur : IImageDeblurrer
    {
        public string Name => "Test/PassthroughDeblur";
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }

    private static Image Rgb(int w, int h, float fill)
    {
        static float[,] Plane(int w, int h, float fill)
        {
            var a = new float[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    a[y, x] = fill;
                }
            }
            return a;
        }
        return new Image([Plane(w, h, fill), Plane(w, h, fill), Plane(w, h, fill)],
            BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = SensorType.Color });
    }

    [Fact]
    public void SupportsDeblur_TrueOnlyWithDeblurrer()
    {
        new SharpenPipeline().SupportsDeblur.ShouldBeFalse();
        new SharpenPipeline(deblurrer: new PassthroughDeblur()).SupportsDeblur.ShouldBeTrue();
    }

    [Fact]
    public async Task DeblurStep_MustBeFirst()
    {
        var pipe = new SharpenPipeline(
            deblurrer: new PassthroughDeblur(), gradientCorrector: new ScaleAll(1f), starRemover: new ScaleAll(1f));
        var req = new SharpenRequest(Rgb(8, 8, 0.1f),
            [new GradientCorrectionStep(), new DeblurStep(), new RemoveStarsStep(), new RecombineStep()]);

        var ex = await Should.ThrowAsync<ArgumentException>(async () => await pipe.ProcessAsync(req));
        ex.Message.ShouldContain("DeblurStep must be the FIRST step");
    }

    [Fact]
    public async Task DeblurStep_WithoutDeblurrer_Throws()
    {
        var pipe = new SharpenPipeline(starRemover: new ScaleAll(1f), gradientCorrector: new ScaleAll(1f));
        var req = new SharpenRequest(Rgb(8, 8, 0.1f),
            [new DeblurStep(), new RemoveStarsStep(), new RecombineStep()]);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () => await pipe.ProcessAsync(req));
        ex.Message.ShouldContain("IImageDeblurrer");
    }

    [Fact]
    public async Task DeblurFirst_RunsDeblurAndFeedsDownstream()
    {
        var identity = new ScaleAll(1f); // star / gradient / denoise pass through
        var pipe = new SharpenPipeline(
            deblurrer: new ScaleAll(2f),
            gradientCorrector: identity,
            starRemover: identity,
            denoiser: identity);

        var result = await pipe.ProcessAsync(SharpenRequest.DeblurFirst(Rgb(8, 8, 0.1f)));

        result.Final.ShouldNotBeNull();
        // deblur x2 -> 0.2; identity star split leaves stars=0, starless=0.2;
        // additive recombine -> 0.2, proving the deblurred plate fed downstream.
        result.Final.GetChannelSpan(0)[0].ShouldBe(0.2f, 1e-4f);
    }

    [Fact]
    public async Task DeblurFirst_NoOpPassthrough_StillProducesFinal()
    {
        var identity = new ScaleAll(1f);
        var pipe = new SharpenPipeline(
            deblurrer: new PassthroughDeblur(), // returns input -> pipeline skips deblur
            gradientCorrector: identity,
            starRemover: identity,
            denoiser: identity);

        var result = await pipe.ProcessAsync(SharpenRequest.DeblurFirst(Rgb(8, 8, 0.1f)));

        result.Final.ShouldNotBeNull();
        result.Final.GetChannelSpan(0)[0].ShouldBe(0.1f, 1e-4f); // source unchanged
    }
}
