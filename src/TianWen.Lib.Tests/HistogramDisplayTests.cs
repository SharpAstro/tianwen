using System;
using System.Collections.Immutable;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A live feed hands the display new statistics on every exposure, and a raw frame's bin count is its
/// peak plus one, so it moves with almost every exposure. <see cref="HistogramDisplay.Refresh"/> takes
/// them in place: the result must be exactly what a display built fresh from them draws, and within the
/// widest bin count seen so far it must allocate nothing.
/// </summary>
public class HistogramDisplayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(StretchMode.None)]
    [InlineData(StretchMode.Unlinked)]
    public void ARefreshDrawsExactlyWhatAFreshDisplayOfTheSameStatisticsDraws(StretchMode mode)
    {
        var display = new HistogramDisplay(Statistics(channels: 3, bins: 60001, seed: 1));

        // Fewer bins than the buffer holds, then more: both must land where a fresh display puts them.
        foreach (var bins in new[] { 4096, 65536 })
        {
            var next = Statistics(channels: 3, bins, seed: bins);
            display.Refresh(next);
            var fresh = new HistogramDisplay(next);

            Recompute(display, mode);
            Recompute(fresh, mode);

            display.RawBinCount.ShouldBe(fresh.RawBinCount);
            (display.LogPeak, display.LinearPeak).ShouldBe((fresh.LogPeak, fresh.LinearPeak));
            for (var c = 0; c < 3; c++)
            {
                display.GetDisplayBins(c).ToArray().ShouldBe(fresh.GetDisplayBins(c).ToArray());
            }
        }
    }

    // Bounded against the bins it must not reallocate, as the Allocations collection's tests are, never against zero: the
    // runtime's own tiering work lands on whichever thread crosses a call-count threshold, and once put 1,448 bytes on
    // this one (arm64, Release, in the full suite) while the refresh itself allocated nothing.
    [Fact]
    public void ARefreshWithinTheWidestBinCountSoFarAllocatesNothing()
    {
        var display = new HistogramDisplay(Statistics(channels: 3, bins: 65536, seed: 1));
        var next = Statistics(channels: 3, bins: 60001, seed: 2);
        const long bins = 3L * 60001 * sizeof(float);

        var before = GC.GetAllocatedBytesForCurrentThread();
        display.Refresh(next);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"refresh of 3 x 60001 bins: {allocated:N0} bytes");
        allocated.ShouldBeLessThan(bins / 2, $"the refresh reuses the widest bins so far: {allocated:N0} bytes against {bins:N0} of bins");
    }

    [Fact]
    public void ADifferentChannelCountNeedsANewDisplay()
    {
        var display = new HistogramDisplay(Statistics(channels: 3, bins: 1024, seed: 1));

        Should.Throw<ArgumentException>(() => display.Refresh(Statistics(channels: 1, bins: 1024, seed: 2)));
    }

    private static void Recompute(HistogramDisplay display, StretchMode mode)
        => display.Recompute(mode, normFactor: 1f,
            pedestals: (0.01f, 0.02f, 0.03f), shadows: (0.05f, 0.06f, 0.07f),
            midtones: (0.2f, 0.25f, 0.3f), rescales: (1.1f, 1.2f, 1.3f));

    // Per-channel histograms of `bins` bins, filled with a pattern that differs per channel and seed.
    private static ImageHistogram[] Statistics(int channels, int bins, int seed)
    {
        var rng = new Random(seed);
        var result = new ImageHistogram[channels];
        for (var c = 0; c < channels; c++)
        {
            var counts = new uint[bins];
            for (var i = 0; i < bins; i++)
            {
                counts[i] = (uint)rng.Next(0, 1000);
            }

            result[c] = new ImageHistogram(c, ImmutableArray.Create(counts), Mean: 0f, Total: 0L, Threshold: bins,
                ThresholdPct: 100, RescaledMaxValue: null, Median: null, MAD: null, IgnoreBlack: false);
        }
        return result;
    }
}
