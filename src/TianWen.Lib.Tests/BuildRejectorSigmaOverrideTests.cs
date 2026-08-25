using Shouldly;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Coverage for the <c>--reject-low-sigma</c> / <c>--reject-high-sigma</c> overrides on
/// <see cref="StackingPipeline.BuildRejector"/>.
///
/// <para>The two halves of that method answer different questions and must stay separable: the
/// frame count picks the rejector KIND (how many samples the estimator has), the sigma pair sets
/// its THRESHOLDS (what the caller is trying to throw away). The comet layer is the case that
/// forced the split -- its defaults are asymmetric the star-KEEPING way, and a comet-aligned stack
/// wants a star clipped rather than preserved.</para>
/// </summary>
[Collection("Imaging")]
public class BuildRejectorSigmaOverrideTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    public void TooFewFramesStillRejectsNothing(int frameCount)
    {
        // The overrides must not conjure a rejector where the count says there is nothing to
        // estimate from; below 5 samples a median + MAD is not a statistic.
        StackingPipeline.BuildRejector(frameCount, lowSigma: 2f, highSigma: 2f).ShouldBeNull();
    }

    [Theory]
    [InlineData(5, 3f, 3f)]
    [InlineData(29, 3f, 3f)]
    [InlineData(30, 3f, 5f)]
    [InlineData(59, 3f, 5f)]
    [InlineData(60, 3f, 5f)]
    [InlineData(135, 3f, 5f)]
    public void OmittingBothOverridesKeepsThePerKindDefaults(int frameCount, float low, float high)
    {
        // Byte-identical to the pre-override behaviour: every existing caller passes neither.
        var (actualLow, actualHigh) = Sigmas(StackingPipeline.BuildRejector(frameCount));
        actualLow.ShouldBe(low);
        actualHigh.ShouldBe(high);
    }

    [Theory]
    [InlineData(10, typeof(LinearFitClipRejector))]
    [InlineData(45, typeof(WinsorizedSigmaClipRejector))]
    [InlineData(135, typeof(SigmaClipRejector))]
    public void AnOverrideMovesTheThresholdsAndLeavesTheKindToTheFrameCount(int frameCount, System.Type expectedKind)
    {
        var rejector = StackingPipeline.BuildRejector(frameCount, lowSigma: 2.25f, highSigma: 2.75f);

        rejector.ShouldBeOfType(expectedKind,
            "the sigma pair must not select a rejector kind; only the frame count does");
        var (low, high) = Sigmas(rejector);
        low.ShouldBe(2.25f);
        high.ShouldBe(2.75f);
    }

    [Fact]
    public void EachSideOverridesIndependently()
    {
        // The comet-layer case: pull the HIGH side in to clip trailed star residuals, while
        // leaving the low side alone so genuine dark pixels are treated exactly as before.
        var (low, high) = Sigmas(StackingPipeline.BuildRejector(135, highSigma: 2.5f));
        low.ShouldBe(3f, "the low side was not overridden and must keep its default");
        high.ShouldBe(2.5f);

        var (low2, high2) = Sigmas(StackingPipeline.BuildRejector(135, lowSigma: 1.5f));
        low2.ShouldBe(1.5f);
        high2.ShouldBe(5f, "the high side was not overridden and must keep its default");
    }

    private static (float Low, float High) Sigmas(IPixelRejector? rejector) => rejector switch
    {
        SigmaClipRejector s => (s.LowSigma, s.HighSigma),
        WinsorizedSigmaClipRejector w => (w.LowSigma, w.HighSigma),
        LinearFitClipRejector l => (l.LowSigma, l.HighSigma),
        _ => throw new Xunit.Sdk.XunitException($"unexpected rejector {rejector?.GetType().Name ?? "<null>"}"),
    };
}
