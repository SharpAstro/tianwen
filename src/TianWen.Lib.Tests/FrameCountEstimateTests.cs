using System;
using Shouldly;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// How many frames a slot is expected to yield.
///
/// <para>The bug that produced these: the Home board reported <c>frame 5/1</c> during a real run. The
/// denominator was the sum of <see cref="FilterExposure.Count"/> over the filter plan, and
/// <c>FilterPlanBuilder.BuildSingleFilterPlan</c> omits <c>Count</c>, so it defaulted to 1. Raising the
/// default would not have fixed it: <c>Count</c> is a cycling quantum ("frames before advancing to the
/// next filter"), and the imaging loop only consults it when the plan has more than one entry -- so a
/// single-filter plan shoots until the slot's time runs out and no count could be right.</para>
///
/// <para>Meanwhile the Session Setup tab carried its own duration-derived formula and said <c>~245</c>
/// for the same observation. Two answers, 245x apart, on two screens at once. These tests pin the one
/// that replaced both.</para>
/// </summary>
public class FrameCountEstimateTests
{
    [Fact]
    public void ARigWithNoFilterWheelGetsTheSlotsWorthOfFrames_NotOne()
    {
        // The exact shape BuildSingleFilterPlan produces: one passthrough entry, Count left at its
        // default of 1. Summing counts answered 1 here, for the commonest configuration there is.
        var plan = FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(50));

        plan.Length.ShouldBe(1);
        plan[0].Count.ShouldBe(1, "the cycling quantum is genuinely 1; it is just not a frame total");

        // 50 s subs + 10 s overhead is a frame a minute, so a 100-minute slot holds 100.
        FrameCountEstimate.ForPlan(TimeSpan.FromMinutes(100), plan).ShouldBe(100);
    }

    [Fact]
    public void ALadderIsWeightedByEachEntrysFrameCount()
    {
        // One cycle: 3 x (50 + 10) + 1 x (110 + 10) = 300 s for 4 frames = 75 s a frame.
        // A 1 h slot therefore holds 48. An UNWEIGHTED mean of the two entries would be 90 s a frame
        // and answer 40, so this distinguishes the two.
        var ladder = System.Collections.Immutable.ImmutableArray.Create(
            new FilterExposure(0, TimeSpan.FromSeconds(50), 3),
            new FilterExposure(1, TimeSpan.FromSeconds(110), 1));

        FrameCountEstimate.ForPlan(TimeSpan.FromHours(1), ladder).ShouldBe(48);
    }

    [Fact]
    public void ASingleEntryLadderAgreesWithThePlainWindowFormula()
    {
        // The two entry points must not be allowed to diverge: one is what the Session Setup tab asks,
        // the other is what an observation asks, and they are the same question.
        var window = TimeSpan.FromMinutes(137);
        var sub = TimeSpan.FromSeconds(90);

        FrameCountEstimate.ForPlan(window, FilterPlanBuilder.BuildSingleFilterPlan(sub))
            .ShouldBe(FrameCountEstimate.ForWindow(window, sub));
    }

    [Theory]
    [InlineData(0)]      // no slot
    [InlineData(-30)]    // a slot that has already closed
    public void ANonPositiveWindowYieldsNothingRatherThanANegativeCount(int minutes)
    {
        var plan = FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(60));

        FrameCountEstimate.ForPlan(TimeSpan.FromMinutes(minutes), plan).ShouldBe(0);
        FrameCountEstimate.ForWindow(TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(60)).ShouldBe(0);
    }

    [Fact]
    public void AnEmptyPlanHasNoDenominatorRatherThanAFabricatedOne()
    {
        // A bare target queued through /targets carries no plan. Inventing a denominator would misreport
        // how far along the run is; 0 is what the display treats as "unknown" and drops.
        FrameCountEstimate.ForPlan(TimeSpan.FromHours(1), []).ShouldBe(0);
        FrameCountEstimate.ForPlan(TimeSpan.FromHours(1), default).ShouldBe(0);
    }

    [Theory]
    [InlineData(60, 100)]
    [InlineData(137, 48)]
    [InlineData(500, 3)]
    public void TheInverseReproducesTheEstimateExactly(int windowMinutes, int frames)
    {
        // This is what RemoteSessionMirror leans on: the state DTO carries the estimate but not the plan,
        // so the mirror rebuilds a plan from the number and must land back on the same number. Integer
        // truncation makes that non-obvious, hence pinning it.
        var window = TimeSpan.FromMinutes(windowMinutes);

        var sub = FrameCountEstimate.SubExposureForFrames(window, frames);

        FrameCountEstimate.ForWindow(window, sub).ShouldBe(frames);
    }

    [Fact]
    public void AnUnreachableFrameCountInvertsToNothingRatherThanANegativeExposure()
    {
        // More frames than the slot could hold even at zero exposure: 10 frames of pure overhead is
        // 100 s, which does not fit in a minute.
        FrameCountEstimate.SubExposureForFrames(TimeSpan.FromMinutes(1), 10).ShouldBe(TimeSpan.Zero);
        FrameCountEstimate.SubExposureForFrames(TimeSpan.FromHours(1), 0).ShouldBe(TimeSpan.Zero);
    }
}
