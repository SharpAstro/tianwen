using Shouldly;
using System;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Guider")]
public class GuideErrorTrackerTests
{
    [Fact]
    public void GivenEmptyTrackerWhenQueryThenDefaultValues()
    {
        var tracker = new GuideErrorTracker();

        tracker.TotalSamples.ShouldBe(0u);
        tracker.RaRmsAll.ShouldBe(0);
        tracker.DecRmsAll.ShouldBe(0);
        tracker.TotalRmsAll.ShouldBe(0);
        tracker.LastRaError.ShouldBeNull();
        tracker.LastDecError.ShouldBeNull();
        tracker.GetDriftRatio().ShouldBeNull();
    }

    [Fact]
    public void GivenConstantErrorWhenAddedThenRmsEqualsError()
    {
        var tracker = new GuideErrorTracker();

        // Add constant 1.0 RA error, 0.5 Dec error over 50 samples
        for (var i = 0; i < 50; i++)
        {
            tracker.Add(i * 2.0, 1.0, 0.5);
        }

        tracker.TotalSamples.ShouldBe(50u);
        // RMS of constant value = that value
        tracker.RaRmsShort.ShouldBe(1.0, 0.01);
        tracker.DecRmsShort.ShouldBe(0.5, 0.01);
        tracker.TotalRmsShort.ShouldBe(Math.Sqrt(1.0 + 0.25), 0.01);
    }

    [Fact]
    public void GivenSamplesWhenWindowExpiresThenOldSamplesDropped()
    {
        var tracker = new GuideErrorTracker();

        // Add 50 samples at 1s intervals with RA error = 2.0
        for (var i = 0; i < 50; i++)
        {
            tracker.Add(i, 2.0, 0);
        }

        // Short window (100s) should have all 50 samples
        tracker.ShortWindowCount.ShouldBe(50);

        // Add 100 more samples with RA error = 0.5 (from t=50 to t=149)
        for (var i = 50; i < 150; i++)
        {
            tracker.Add(i, 0.5, 0);
        }

        // Short window (100s) should only have recent samples
        // At t=149, cutoff = 49, so samples at t≥49 remain (101 samples)
        tracker.ShortWindowCount.ShouldBe(101);
        // RMS dominated by 0.5 samples with one 2.0 at t=49
        tracker.RaRmsShort.ShouldBeLessThan(1.0, "recent small errors should dominate");
    }

    [Fact]
    public void GivenGrowingErrorWhenCheckedThenDriftDetected()
    {
        var tracker = new GuideErrorTracker();

        // Initial good period: small errors for 200 seconds
        for (var i = 0; i < 200; i++)
        {
            tracker.Add(i, 0.1, 0.1);
        }

        // Drift begins: growing errors for 100 seconds
        for (var i = 200; i < 300; i++)
        {
            tracker.Add(i, 0.1 + (i - 200) * 0.02, 0.1);
        }

        var ratio = tracker.GetDriftRatio();
        ratio.ShouldNotBeNull();
        // Short window has the drifting samples, should have higher RMS than long window
        ratio.Value.ShouldBeGreaterThan(1.0, "drift should make short RMS > long RMS");
    }

    [Fact]
    public void GivenTrackerWhenResetThenAllCleared()
    {
        var tracker = new GuideErrorTracker();

        for (var i = 0; i < 10; i++)
        {
            tracker.Add(i, 1.0, 0.5);
        }

        tracker.Reset();

        tracker.TotalSamples.ShouldBe(0u);
        tracker.ShortWindowCount.ShouldBe(0);
        tracker.LongWindowCount.ShouldBe(0);
        tracker.LastRaError.ShouldBeNull();
    }

    [Fact]
    public void GivenTrackerWhenToGuideStatsThenPopulated()
    {
        var tracker = new GuideErrorTracker();

        tracker.Add(0, 1.5, -0.8);
        tracker.Add(1, -1.0, 0.6);
        tracker.Add(2, 0.5, -0.3);

        var stats = tracker.ToGuideStats();

        stats.TotalRMS.ShouldBeGreaterThan(0);
        stats.RaRMS.ShouldBeGreaterThan(0);
        stats.DecRMS.ShouldBeGreaterThan(0);
        stats.PeakRa.ShouldBe(1.5);
        stats.PeakDec.ShouldBe(0.8);
        stats.LastRaErr.ShouldBe(0.5);
        stats.LastDecErr.ShouldBe(-0.3);
    }

    [Fact]
    public void GivenRollingAccumWhenPeakEvictedThenPeakRecomputed()
    {
        var accum = new RollingAccum(TimeSpan.FromSeconds(10));

        accum.Add(0, 5.0);   // large value
        accum.Add(1, 1.0);
        accum.Peak.ShouldBe(5.0);

        // Evict the large value by advancing time past the window
        accum.Add(15, 1.0);
        accum.Peak.ShouldBe(1.0, "peak should be recomputed after eviction");
    }

    [Fact]
    public void GivenRollingAccumWhenSingleSampleThenCorrectStats()
    {
        var accum = new RollingAccum(TimeSpan.FromSeconds(60));

        accum.Add(0, 3.0);

        accum.Count.ShouldBe(1);
        accum.Mean.ShouldBe(3.0);
        accum.RMS.ShouldBe(3.0);
        accum.Stdev.ShouldBe(0); // single sample has 0 stdev
    }
}
