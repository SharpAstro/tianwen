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
    /// The SAS-shaped program: no deblurrer, so the stars plate is sharpened and the starless plate
    /// deconvolved, and there is no green fringe for SCNR to neutralise.
    /// </summary>
    [Fact]
    public void WithoutADeblurrerTheProgramIsTheSasShapedOne()
    {
        LinearEnhanceProgram.For(supportsDeblur: false).ToSteps()
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
        LinearEnhanceProgram.For(supportsDeblur: true).ToSteps()
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
    /// The two public factories are the program and nothing else. They used to BE two more copies
    /// of the order, which is what made four copies in total.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCanonicalRequestsAreTheProgram(bool supportsDeblur)
    {
        var source = TestImage();
        var request = supportsDeblur ? SharpenRequest.DeblurFirst(source) : SharpenRequest.Canonical(source);
        request.Steps.ShouldBe(LinearEnhanceProgram.For(supportsDeblur).ToSteps());
    }

    /// <summary>
    /// The CLI splits the program so it can put its own per-plate stretch between the plate work and
    /// the composite (SCNR after the stretch, PixInsight convention). Reassembling the two halves in
    /// order has to give the whole program back, or that split would silently drop or reorder a step.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePlateAndCompositeHalvesReassembleIntoTheWholeProgram(bool supportsDeblur)
    {
        var program = LinearEnhanceProgram.For(supportsDeblur);
        program.PlateSteps.AddRange(program.CompositeSteps).ShouldBe(program.ToSteps());
    }

    /// <summary>
    /// Every toggle removes its own step and disturbs nothing else. This is what lets a caller vary
    /// membership without restating the order: `image sharpen`'s flags are exactly such toggles.
    /// </summary>
    [Fact]
    public void ClearingAToggleRemovesOnlyThatStep()
    {
        var program = LinearEnhanceProgram.For(supportsDeblur: true);

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
        var steps = (LinearEnhanceProgram.For(supportsDeblur: true) with
        {
            DeblurBlend = 0.25f,
            DenoiseBlend = 0.75f,
            ScnrAmount = 0.5f,
        }).ToSteps();

        steps.OfType<DeblurStep>().Single().Blend.ShouldBe(0.25f);
        steps.OfType<DenoiseStarlessStep>().Single().Blend.ShouldBe(0.75f);
        steps.OfType<ScnrStarsStep>().Single().Amount.ShouldBe(0.5f);
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
