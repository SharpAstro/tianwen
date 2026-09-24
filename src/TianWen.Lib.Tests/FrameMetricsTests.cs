using Shouldly;
using System;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="FrameMetrics.IsComparableTo"/> decides which frames the focus-drift check may compare:
/// equal exposure, gain AND filter position, since the chromatic focus shift between two filters
/// changes HFD at equal exposure without the focus having drifted at all.
/// </summary>
public class FrameMetricsTests
{
    private static FrameMetrics Metrics(int filterPosition, float exposureSeconds = 30f, short gain = 100)
        => new FrameMetrics(100, 2.0f, 2.4f, TimeSpan.FromSeconds(exposureSeconds), gain, filterPosition);

    [Fact]
    public void FramesDifferingOnlyInFilterAreNotComparable()
        => Metrics(filterPosition: 0).IsComparableTo(Metrics(filterPosition: 1)).ShouldBeFalse();

    [Fact]
    public void FramesThroughTheSameFilterAreComparable()
        => Metrics(filterPosition: 2).IsComparableTo(Metrics(filterPosition: 2)).ShouldBeTrue();

    [Fact]
    public void TwoFramesWithNoWheelAreComparable()
        => Metrics(filterPosition: -1).IsComparableTo(Metrics(filterPosition: -1)).ShouldBeTrue();

    [Fact]
    public void AnUnknownSlotDoesNotMatchAKnownOne()
        => Metrics(filterPosition: -1).IsComparableTo(Metrics(filterPosition: 0)).ShouldBeFalse();

    [Fact]
    public void ExposureAndGainStillCount()
    {
        Metrics(filterPosition: 0).IsComparableTo(Metrics(filterPosition: 0, exposureSeconds: 2f)).ShouldBeFalse();
        Metrics(filterPosition: 0).IsComparableTo(Metrics(filterPosition: 0, gain: 300)).ShouldBeFalse();
    }
}
