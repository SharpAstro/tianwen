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

    /// <summary>
    /// The session keys its drift baselines on <see cref="AcquisitionSetting"/>, so two frames must share a key
    /// exactly when they are comparable; star metrics must play no part in either.
    /// </summary>
    [Theory]
    [InlineData(0, 30f, 100, true)]
    [InlineData(1, 30f, 100, false)]
    [InlineData(0, 2f, 100, false)]
    [InlineData(0, 30f, 300, false)]
    [InlineData(-1, 30f, 100, false)]
    public void TheBaselineKeyAgreesWithComparability(int filterPosition, float exposureSeconds, short gain, bool comparable)
    {
        var a = Metrics(filterPosition: 0);
        var b = Metrics(filterPosition, exposureSeconds, gain) with { StarCount = 7, MedianHfd = 5.5f, MedianFwhm = 6f };

        a.IsComparableTo(b).ShouldBe(comparable);
        (AcquisitionSetting.Of(a) == AcquisitionSetting.Of(b)).ShouldBe(comparable);
    }
}
