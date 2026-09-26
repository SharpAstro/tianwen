using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Enhancement;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="LinearEnhanceProgram"/> is the ONE place the linear enhance step order is written,
/// and these pin it as an ORDER rather than as a set. Three callers read it -- the stacking
/// pipeline's <c>--enhance</c>, the hosted enhance endpoint and the CLI's <c>image sharpen</c> --
/// and before it existed each carried its own hand-written copy. They had already drifted: the
/// CLI's began at <see cref="RemoveStarsStep"/>, so the command line ran neither the whole-frame
/// deblur nor the gradient correction the other two always ran, and the only symptom was that the
/// same master came out looking different depending on which one you asked.
/// </summary>
public class LinearEnhanceProgramTests
{
    /// <summary>
    /// The split program with every role and no deblurrer: the stars plate is sharpened and the
    /// starless plate deconvolved, and there is no green fringe for SCNR to neutralise.
    /// </summary>
    [Fact]
    public void WithEveryRoleAndNoDeblurrerTheSplitProgramSharpensAndDeconvolves()
    {
        LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: false)).ToSteps()
            .Select(static s => s.GetType()).ShouldBe(
            [
                typeof(GradientCorrectionStep),
                typeof(RemoveStarsStep),
                typeof(SharpenStarsStep),
                typeof(DeconvolveStarlessStep),
                typeof(DenoiseStarlessStep),
                typeof(RecombineStep),
            ]);
    }

    /// <summary>
    /// The BlurX-first (PixInsight OSC) program: the whole frame is deblurred before the stars come
    /// out, which is why there is no stellar sharpen and no starless deconvolution -- both would be
    /// a second pass over detail BlurX has already recovered -- and why the stars plate gets SCNR,
    /// since tightened faint stars carry a green fringe.
    /// </summary>
    [Fact]
    public void WithADeblurrerTheProgramIsTheBlurXFirstOne()
    {
        LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: true)).ToSteps()
            .Select(static s => s.GetType()).ShouldBe(
            [
                typeof(DeblurStep),
                typeof(GradientCorrectionStep),
                typeof(RemoveStarsStep),
                typeof(DenoiseStarlessStep),
                typeof(ScnrStarsStep),
                typeof(RecombineStep),
            ]);
    }

    /// <summary>
    /// No star remover serves (no RC-Astro StarXTerminator): the WHOLE-FRAME program. Gradient
    /// correction, then the denoise on the frame, whose output is the result, so there is no split,
    /// no plate-only step and no recombine; a deblurrer, where one serves, still goes first.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutAStarRemoverTheProgramIsWholeFrame(bool deblur)
    {
        var capabilities = new EnhanceCapabilities(
            Deblur: deblur, GradientCorrection: true, StarRemoval: false, StellarSharpen: true, Deconvolve: true, Denoise: true);
        var steps = LinearEnhanceProgram.For(capabilities).ToSteps().Select(static s => s.GetType()).ToArray();

        steps.ShouldBe(deblur
            ? [typeof(DeblurStep), typeof(GradientCorrectionStep), typeof(DenoiseFrameStep)]
            : [typeof(GradientCorrectionStep), typeof(DenoiseFrameStep)]);
    }

    /// <summary>
    /// A mono frame with no RC-Astro: nothing denoises it (the in-house model is colour-only), so the
    /// program is gradient correction alone, and the pipeline promotes that frame to the result.
    /// </summary>
    [Fact]
    public void WithOnlyAGradientCorrectorTheProgramIsThatAlone()
    {
        var capabilities = new EnhanceCapabilities(
            Deblur: false, GradientCorrection: true, StarRemoval: false, StellarSharpen: false, Deconvolve: false, Denoise: false);

        LinearEnhanceProgram.For(capabilities).ToSteps().Select(static s => s.GetType())
            .ShouldBe([typeof(GradientCorrectionStep)]);
    }

    /// <summary>
    /// A role nothing serves never gets a step. StarXTerminator and NoiseXTerminator licensed, no
    /// BlurX, no stellar sharpener: the split program without the sharpen and the deconvolution.
    /// </summary>
    [Fact]
    public void ARoleNothingServesNeverGetsAStep()
    {
        var capabilities = new EnhanceCapabilities(
            Deblur: false, GradientCorrection: true, StarRemoval: true, StellarSharpen: false, Deconvolve: false, Denoise: true);

        LinearEnhanceProgram.For(capabilities).ToSteps().Select(static s => s.GetType()).ShouldBe(
        [
            typeof(GradientCorrectionStep),
            typeof(RemoveStarsStep),
            typeof(DenoiseStarlessStep),
            typeof(RecombineStep),
        ]);
    }

    /// <summary>
    /// The two public factories are the program and nothing else. They used to BE two more copies
    /// of the order, which is what made four copies in total.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCanonicalRequestsAreTheProgram(bool deblur)
    {
        var source = TestImage();
        var request = deblur ? SharpenRequest.DeblurFirst(source) : SharpenRequest.Canonical(source);
        request.Steps.ShouldBe(LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur)).ToSteps());
    }

    /// <summary>
    /// The CLI splits the program so it can put its own per-plate stretch between the plate work and
    /// the composite (SCNR after the stretch, PixInsight convention). Reassembling the two halves in
    /// order has to give the whole program back, or that split would silently drop or reorder a step.
    /// </summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void ThePlateAndCompositeHalvesReassembleIntoTheWholeProgram(bool deblur, bool starRemoval)
    {
        var program = LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur) with { StarRemoval = starRemoval });
        program.PlateSteps.AddRange(program.CompositeSteps).ShouldBe(program.ToSteps());
    }

    /// <summary>
    /// Every toggle removes its own step and disturbs nothing else. This is what lets a caller vary
    /// membership without restating the order: `image sharpen`'s flags are exactly such toggles.
    /// </summary>
    [Fact]
    public void ClearingAToggleRemovesOnlyThatStep()
    {
        var program = LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: true));

        (program with { GradientCorrection = false }).ToSteps()
            .ShouldBe(program.ToSteps().Where(static s => s is not GradientCorrectionStep));
        (program with { Denoise = false }).ToSteps()
            .ShouldBe(program.ToSteps().Where(static s => s is not DenoiseStarlessStep));
        (program with { Scnr = ScnrMode.None }).ToSteps()
            .ShouldBe(program.ToSteps().Where(static s => s is not ScnrStarsStep));
        // --no-recombine writes each plate as its own FITS, so the composite never runs.
        (program with { Recombine = false }).ToSteps()
            .ShouldBe(program.ToSteps().Where(static s => s is not RecombineStep));
    }

    /// <summary>
    /// A blend reaches the step that carries it. <c>stack --enhance-blend</c> sets all four at once
    /// and the CLI sets them separately, so they have to be independent.
    /// </summary>
    [Fact]
    public void ABlendReachesItsOwnStep()
    {
        var steps = (LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: true)) with
        {
            DeblurBlend = 0.25f,
            DenoiseBlend = 0.75f,
            ScnrAmount = 0.5f,
        }).ToSteps();

        steps.OfType<DeblurStep>().Single().Blend.ShouldBe(0.25f);
        steps.OfType<DenoiseStarlessStep>().Single().Blend.ShouldBe(0.75f);
        steps.OfType<ScnrStarsStep>().Single().Amount.ShouldBe(0.5f);

        // The whole-frame program's denoise carries the same dial.
        var wholeFrame = (LinearEnhanceProgram.For(EnhanceCapabilities.AllRoles(deblur: false) with { StarRemoval = false })
            with { DenoiseBlend = 0.6f }).ToSteps();
        wholeFrame.OfType<DenoiseFrameStep>().Single().Blend.ShouldBe(0.6f);
    }

    /// <summary>A trivial RGB frame. These tests never run the pipeline -- the factories only need
    /// something to hang a <see cref="SharpenRequest"/> on.</summary>
    private static Image TestImage()
    {
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            planes[c] = new float[4, 4];
        }
        return new Image(planes, BitDepth.Float32, 1.0f, 0f, 0f, new ImageMeta());
    }
}
