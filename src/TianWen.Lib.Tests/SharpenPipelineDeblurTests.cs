using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pipeline mechanics for the program's shapes: the BlurX-first (RC-Astro) one, the
/// <see cref="DeblurStep"/> + <see cref="SharpenRequest.DeblurFirst"/> canonical, and the WHOLE-FRAME one
/// the pipeline runs when no star remover serves (<see cref="DenoiseFrameStep"/>, and the promotion of
/// the most-processed frame to the result), with the capability question that picks between them.
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

    /// <summary>A denoiser that serves colour only, as the in-house N2N model does: it declines a
    /// 1-channel input through <see cref="IEnhancerAvailability"/> and halves every pixel otherwise.</summary>
    private sealed class ColourOnlyDenoiser : IDenoiseEnhancer, IEnhancerAvailability
    {
        public string Name => "Test/ColourOnlyDenoiser";
        public bool CanServe(int channelCount, EnhanceOptions options) => channelCount == 3;
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => new ScaleAll(0.5f).EnhanceAsync(input, cancellationToken);
    }

    /// <summary>Returns the input unchanged -- the unlicensed-bxt no-op the
    /// pipeline must detect and skip.</summary>
    private sealed class PassthroughDeblur : IImageDeblurrer
    {
        public string Name => "Test/PassthroughDeblur";
        public Task<Image> EnhanceAsync(Image input, CancellationToken cancellationToken = default)
            => Task.FromResult(input);
    }

    private static Image Mono(int w, int h, SensorType sensor, float fill = 0.1f)
    {
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                plane[y, x] = fill;
            }
        }
        return new Image([plane], BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta { SensorType = sensor });
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

    /// <summary>
    /// A registered role serves unless it declines: an absent one never does, one that cannot say
    /// always does, and one that implements <see cref="IEnhancerAvailability"/> answers for itself
    /// (the colour-only denoiser declines mono). A 1-channel CFA mosaic counts as three, because the
    /// pipeline debayers it before any step sees it.
    /// </summary>
    [Fact]
    public void CapabilitiesFor_AskEachRoleAboutTheInputItWillSee()
    {
        new SharpenPipeline().CapabilitiesFor(Rgb(8, 8, 0.1f), EnhanceOptions.Default).ShouldBe(default(EnhanceCapabilities));
        new SharpenPipeline(deblurrer: new PassthroughDeblur()).CapabilitiesFor(Rgb(8, 8, 0.1f), EnhanceOptions.Default).Deblur.ShouldBeTrue();

        var pipe = new SharpenPipeline(denoiser: new ColourOnlyDenoiser());
        pipe.CapabilitiesFor(Rgb(8, 8, 0.1f), EnhanceOptions.Default).Denoise.ShouldBeTrue();
        pipe.CapabilitiesFor(Mono(8, 8, SensorType.Monochrome), EnhanceOptions.Default).Denoise.ShouldBeFalse();
        pipe.CapabilitiesFor(Mono(8, 8, SensorType.RGGB), EnhanceOptions.Default).Denoise.ShouldBeTrue();
    }

    /// <summary>
    /// No star remover: the canonical program is whole-frame, and its denoise's output IS the result
    /// (no split, no recombine). The gradient-corrected plate stays an intermediate the caller asked
    /// for, a distinct image, never the same instance as the result.
    /// </summary>
    [Fact]
    public async Task WithoutAStarRemover_TheWholeFrameDenoiseIsTheResult()
    {
        var pipe = new SharpenPipeline(gradientCorrector: new ScaleAll(1f), denoiser: new ColourOnlyDenoiser());
        var source = Rgb(8, 8, 0.4f);

        var program = pipe.CanonicalProgram(source, EnhanceOptions.Default);
        program.ToSteps().ShouldBe([new GradientCorrectionStep(), new DenoiseFrameStep()]);
        var result = await pipe.ProcessAsync(new SharpenRequest(source, program.ToSteps()), TestContext.Current.CancellationToken);

        var final = result.Final.ShouldNotBeNull();
        final.GetChannelSpan(0)[0].ShouldBe(0.2f, 1e-6f);
        result.Starless.ShouldBeNull();
        result.StarsOnly.ShouldBeNull();
        ReferenceEquals(result.GradientCorrected, final).ShouldBeFalse();
    }

    /// <summary>
    /// A mono frame with no RC-Astro: the colour-only denoiser declines it, so the program is gradient
    /// correction alone, and that frame is PROMOTED to the result: returned as Final and nowhere else,
    /// so the caller cannot release it twice.
    /// </summary>
    [Fact]
    public async Task WithNothingButAGradientCorrector_TheCorrectedFrameIsPromotedToTheResult()
    {
        var pipe = new SharpenPipeline(gradientCorrector: new ScaleAll(2f), denoiser: new ColourOnlyDenoiser());
        var source = Mono(8, 8, SensorType.Monochrome, fill: 0.1f);

        var program = pipe.CanonicalProgram(source, EnhanceOptions.Default);
        program.ToSteps().ShouldBe([new GradientCorrectionStep()]);
        var result = await pipe.ProcessAsync(new SharpenRequest(source, program.ToSteps()), TestContext.Current.CancellationToken);

        result.Final.ShouldNotBeNull().GetChannelSpan(0)[0].ShouldBe(0.2f, 1e-6f);
        result.GradientCorrected.ShouldBeNull();
    }

    /// <summary>A program either splits the stars or works on the whole frame, never both.</summary>
    [Fact]
    public async Task TheSplitAndTheWholeFrameProgramsDoNotMix()
    {
        var pipe = new SharpenPipeline(starRemover: new ScaleAll(1f), denoiser: new ScaleAll(1f));
        var source = Rgb(8, 8, 0.1f);

        await Should.ThrowAsync<ArgumentException>(async () => await pipe.ProcessAsync(
            new SharpenRequest(source, [new RemoveStarsStep(), new DenoiseFrameStep()]), TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentException>(async () => await pipe.ProcessAsync(
            new SharpenRequest(source, [new DenoiseFrameStep(), new RemoveStarsStep()]), TestContext.Current.CancellationToken));
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

        var result = await pipe.ProcessAsync(SharpenRequest.DeblurFirst(Rgb(8, 8, 0.1f)), TestContext.Current.CancellationToken);

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

        var result = await pipe.ProcessAsync(SharpenRequest.DeblurFirst(Rgb(8, 8, 0.1f)), TestContext.Current.CancellationToken);

        result.Final.ShouldNotBeNull();
        result.Final.GetChannelSpan(0)[0].ShouldBe(0.1f, 1e-4f); // source unchanged
    }
}
