using System;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Scheduling")]
public class FilterPlanBuilderTests
{
    private static readonly ImmutableArray<InstalledFilter> LRGBHaFilters =
    [
        new InstalledFilter(Filter.Luminance, 0),
        new InstalledFilter(Filter.Red, 20),
        new InstalledFilter(Filter.Green, 0),
        new InstalledFilter(Filter.Blue, -15),
        new InstalledFilter(Filter.HydrogenAlpha, 21),
    ];

    private static readonly ImmutableArray<InstalledFilter> LRGBHaFiltersZeroOffsets =
    [
        new InstalledFilter(Filter.Luminance, 0),
        new InstalledFilter(Filter.Red, 0),
        new InstalledFilter(Filter.Green, 0),
        new InstalledFilter(Filter.Blue, 0),
        new InstalledFilter(Filter.HydrogenAlpha, 0),
    ];

    [Fact]
    public void BuildSingleFilterPlan_ReturnsSingleEntryWithPositionMinusOne()
    {
        var plan = FilterPlanBuilder.BuildSingleFilterPlan(TimeSpan.FromSeconds(120));

        plan.Length.ShouldBe(1);
        plan[0].FilterPosition.ShouldBe(-1);
        plan[0].SubExposure.ShouldBe(TimeSpan.FromSeconds(120));
        plan[0].Count.ShouldBe(1);
    }

    [Fact]
    public void BuildAutoFilterPlan_OrdersAsAltitudeLadder_NarrowbandThenRGBThenLuminance()
    {
        var plan = FilterPlanBuilder.BuildAutoFilterPlan(
            LRGBHaFilters,
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(300));

        // Altitude ladder: narrowband → RGB → Luminance (top)
        plan.Length.ShouldBe(5);

        // Narrowband first (bottom of ladder, low-alt tolerant)
        plan[0].FilterPosition.ShouldBe(4); // Ha
        plan[0].SubExposure.ShouldBe(TimeSpan.FromSeconds(300));

        // Then RGB
        plan[1].FilterPosition.ShouldBe(1); // Red
        plan[2].FilterPosition.ShouldBe(2); // Green
        plan[3].FilterPosition.ShouldBe(3); // Blue

        // Luminance at the top (needs best seeing, stacking foundation)
        plan[4].FilterPosition.ShouldBe(0); // Luminance

        // All broadband (RGB + L) should use broadband exposure
        for (var i = 1; i < 5; i++)
        {
            plan[i].SubExposure.ShouldBe(TimeSpan.FromSeconds(120));
        }
    }

    [Fact]
    public void BuildAutoFilterPlan_AltitudeOverload_IgnoresAltitude_SameAsWithout()
    {
        var planWithAlt = FilterPlanBuilder.BuildAutoFilterPlan(
            LRGBHaFilters,
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(300),
            targetAltitudeDeg: 60);

        var planWithoutAlt = FilterPlanBuilder.BuildAutoFilterPlan(
            LRGBHaFilters,
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(300));

        planWithAlt.Length.ShouldBe(planWithoutAlt.Length);
        for (var i = 0; i < planWithAlt.Length; i++)
        {
            planWithAlt[i].FilterPosition.ShouldBe(planWithoutAlt[i].FilterPosition);
            planWithAlt[i].SubExposure.ShouldBe(planWithoutAlt[i].SubExposure);
        }
    }

    [Fact]
    public void BuildAutoFilterPlan_DefaultFramesPerFilter_Is10()
    {
        var plan = FilterPlanBuilder.BuildAutoFilterPlan(
            LRGBHaFilters,
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(300));

        foreach (var entry in plan)
        {
            entry.Count.ShouldBe(10);
        }
    }

    [Fact]
    public void BuildAutoFilterPlan_CustomFramesPerFilter()
    {
        var plan = FilterPlanBuilder.BuildAutoFilterPlan(
            LRGBHaFilters,
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(300),
            framesPerFilter: 5);

        foreach (var entry in plan)
        {
            entry.Count.ShouldBe(5);
        }
    }

    [Fact]
    public void BuildAutoFilterPlan_EmptyFilters_FallsBackToSinglePlan()
    {
        var plan = FilterPlanBuilder.BuildAutoFilterPlan(
            ImmutableArray<InstalledFilter>.Empty,
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(300));

        plan.Length.ShouldBe(1);
        plan[0].FilterPosition.ShouldBe(-1);
    }

    [Fact]
    public void BuildAutoFilterPlan_NarrowbandOnlyWheel_AllNarrowband()
    {
        var narrowbandOnly = ImmutableArray.Create(
            new InstalledFilter(Filter.HydrogenAlpha, 0),
            new InstalledFilter(Filter.OxygenIII, 0),
            new InstalledFilter(Filter.SulphurII, 0));

        var plan = FilterPlanBuilder.BuildAutoFilterPlan(
            narrowbandOnly,
            TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(300));

        plan.Length.ShouldBe(3);
        plan[0].SubExposure.ShouldBe(TimeSpan.FromSeconds(300));
    }

    [Theory]
    [InlineData(OpticalDesign.Newtonian)]
    [InlineData(OpticalDesign.Cassegrain)]
    [InlineData(OpticalDesign.RASA)]
    [InlineData(OpticalDesign.Astrograph)]
    public void GetReferenceFilter_MirrorDesign_ReturnsLuminance(OpticalDesign design)
    {
        var result = FilterPlanBuilder.GetReferenceFilter(LRGBHaFilters, design);

        result.ShouldBe(0); // Luminance is at index 0
    }

    [Theory]
    [InlineData(OpticalDesign.Refractor)]
    [InlineData(OpticalDesign.SCT)]
    [InlineData(OpticalDesign.NewtonianCassegrain)]
    public void GetReferenceFilter_RefractiveDesign_WithOffsets_ReturnsLuminance(OpticalDesign design)
    {
        // LRGBHaFilters has non-zero offsets
        var result = FilterPlanBuilder.GetReferenceFilter(LRGBHaFilters, design);

        result.ShouldBe(0); // Luminance
    }

    [Theory]
    [InlineData(OpticalDesign.Refractor)]
    [InlineData(OpticalDesign.SCT)]
    [InlineData(OpticalDesign.NewtonianCassegrain)]
    public void GetReferenceFilter_RefractiveDesign_NoOffsets_ReturnsMinusOne(OpticalDesign design)
    {
        var result = FilterPlanBuilder.GetReferenceFilter(LRGBHaFiltersZeroOffsets, design);

        result.ShouldBe(-1); // Must focus on scheduled filter
    }

    [Fact]
    public void GetReferenceFilter_EmptyFilters_ReturnsMinusOne()
    {
        var result = FilterPlanBuilder.GetReferenceFilter(ImmutableArray<InstalledFilter>.Empty, OpticalDesign.Newtonian);

        result.ShouldBe(-1);
    }

    [Fact]
    public void IsNarrowband_BroadbandFilters_ReturnsFalse()
    {
        FilterPlanBuilder.IsNarrowband(Filter.Luminance).ShouldBeFalse();
        FilterPlanBuilder.IsNarrowband(Filter.Red).ShouldBeFalse();
        FilterPlanBuilder.IsNarrowband(Filter.Green).ShouldBeFalse();
        FilterPlanBuilder.IsNarrowband(Filter.Blue).ShouldBeFalse();
    }

    [Fact]
    public void IsNarrowband_NarrowbandFilters_ReturnsTrue()
    {
        FilterPlanBuilder.IsNarrowband(Filter.HydrogenAlpha).ShouldBeTrue();
        FilterPlanBuilder.IsNarrowband(Filter.OxygenIII).ShouldBeTrue();
        FilterPlanBuilder.IsNarrowband(Filter.SulphurII).ShouldBeTrue();
        FilterPlanBuilder.IsNarrowband(Filter.HydrogenBeta).ShouldBeTrue();
    }

    [Fact]
    public void IsNarrowband_DualBandFilter_ReturnsTrueWhenNarrowbandOnly()
    {
        // Ha+OIII is narrowband-only (no RGB bits)
        FilterPlanBuilder.IsNarrowband(Filter.HydrogenAlphaOxygenIII).ShouldBeTrue();
    }
}
