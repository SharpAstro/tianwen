using Shouldly;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which integration strategies a comet layer may be built by (<see cref="StackingPipeline.CometLayerExclusions"/>,
/// #2489). Two of these failed silently on 10P on 2026-11-25, each looking like a plausible master: a star layer the
/// comet's model is subtracted from picked TilePipelined, which re-reads each frame itself and never ran the
/// subtraction ("touched NO pixels in any frame", and no composite), because the filter that keeps such strategies out
/// was written for the mask alone; and a comet layer stacked from per-frame starless plates picked Bayer drizzle, whose
/// one-pass clip kept the star remover's trail residue (-14.8 sigma along the track against -4.0 for a sigma clip),
/// which came back as a coloured ring round the coma.
/// </summary>
public sealed class CometLayerStrategyTests
{
    [Fact]
    public void ALayerTheCometIsSubtractedFromNeverTakesAStrategyThatBypassesTheSubtraction()
    {
        var excluded = StackingPipeline.CometLayerExclusions(masked: false, modelled: true, starless: false);

        excluded.ContainsKey(IntegrationStrategyKind.TilePipelined).ShouldBeTrue();
        excluded.ContainsKey(IntegrationStrategyKind.TilePipelinedDrizzle).ShouldBeTrue();
        excluded.ContainsKey(IntegrationStrategyKind.BayerDrizzle).ShouldBeFalse("Bayer drizzle reads the frame producers, so it subtracts the model");
        excluded[IntegrationStrategyKind.TilePipelined].ShouldContain("re-reads each frame itself");
    }

    [Fact]
    public void AMaskedLayerKeepsOutTheBypassingStrategiesAndBayerDrizzleAsBefore()
    {
        var excluded = StackingPipeline.CometLayerExclusions(masked: true, modelled: false, starless: false);

        excluded.Keys.ShouldBe(
            [IntegrationStrategyKind.TilePipelined, IntegrationStrategyKind.TilePipelinedDrizzle, IntegrationStrategyKind.BayerDrizzle],
            ignoreOrder: true);
    }

    [Fact]
    public void AStarlessCometLayerIsNeverDrizzled()
    {
        var excluded = StackingPipeline.CometLayerExclusions(masked: false, modelled: false, starless: true);

        excluded.ContainsKey(IntegrationStrategyKind.BayerDrizzle).ShouldBeTrue();
        excluded.ContainsKey(IntegrationStrategyKind.TilePipelinedDrizzle).ShouldBeTrue();
        excluded.ContainsKey(IntegrationStrategyKind.TilePipelined).ShouldBeFalse("TilePipelined reads the starless plates themselves, as August's comet layer did");
        excluded[IntegrationStrategyKind.BayerDrizzle].ShouldContain("trail residue");
    }

    [Fact]
    public void AnyOtherLayerKeepsEveryStrategy()
    {
        // A sidereal stack, and a comet layer with no per-frame star removal.
        StackingPipeline.CometLayerExclusions(masked: false, modelled: false, starless: false).ShouldBeEmpty();
    }
}
