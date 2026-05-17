using TianWen.Lib.Stat;
using Shouldly;
using System;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class StatisticsHelperTests
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(42f, 42f)]
    [InlineData(5f, 1f, 4f, 5f, 7f, 9f)]
    [InlineData(5.5f, 1f, 5f, 6f, 9f)]
    [InlineData(8f, 8f, 9f, 7f, 10f, 6.5f)]
    public void GivenValuesWhenCalcMedianSortedThenItIsReturned(float expectedMedian, params float[] values)
    {
        StatisticsHelper.MedianSorted(values).ShouldBe(expectedMedian);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(42f, 42f)]
    [InlineData(5f, 1f, 4f, 5f, 7f, 9f)]
    [InlineData(5.5f, 1f, 5f, 6f, 9f)]
    [InlineData(8f, 8f, 9f, 7f, 10f, 6.5f)]
    public void GivenValuesWhenCalcMedianFastThenItIsReturned(float expectedMedian, params float[] values)
    {
        StatisticsHelper.MedianFast(values).ShouldBe(expectedMedian);
    }

    [Fact]
    public void MedianFast_MatchesMedianSorted_OverRandomInputs()
    {
        // Cross-check MedianFast against the sort-based MedianSorted over a
        // wide range of sizes (including the up-to-328 annulus buffer size
        // used by AnalyseStar) and random distributions. Both implementations
        // must produce byte-identical results.
        var rng = new Random(42);
        foreach (var n in new[] { 2, 3, 4, 5, 7, 8, 9, 15, 16, 32, 100, 327, 328, 329, 1024 })
        {
            for (var trial = 0; trial < 25; trial++)
            {
                var src = new float[n];
                for (var i = 0; i < n; i++)
                {
                    src[i] = (float)(rng.NextDouble() * 1000.0 - 500.0);
                }

                var expected = StatisticsHelper.MedianSorted(((float[])src.Clone()).AsSpan());
                var actual = StatisticsHelper.MedianFast(((float[])src.Clone()).AsSpan());
                actual.ShouldBe(expected, $"n={n}, trial={trial}");
            }
        }
    }

    [Fact]
    public void MedianFast_Double_MatchesMedianSorted_OverRandomInputs()
    {
        var rng = new Random(7);
        foreach (var n in new[] { 2, 3, 4, 5, 15, 16, 100 })
        {
            for (var trial = 0; trial < 25; trial++)
            {
                var src = new double[n];
                for (var i = 0; i < n; i++)
                {
                    src[i] = rng.NextDouble() * 1000.0 - 500.0;
                }

                var expected = StatisticsHelper.MedianSorted(((double[])src.Clone()).AsSpan());
                var actual = StatisticsHelper.MedianFast(((double[])src.Clone()).AsSpan());
                actual.ShouldBe(expected, $"n={n}, trial={trial}");
            }
        }
    }

    [Theory]
    [InlineData(20u, -120, -40, -60)]
    [InlineData(1u, 1)]
    [InlineData(0u, 0)]
    [InlineData(0u, 0, 0)]
    [InlineData(4u, 8, 12, 24)]
    [InlineData(20u, 120, 40, 60)]
    public void GivenValuesWhenCalcGCDThenItIsReturned(uint expectedGCD, int first, params int[] rest)
    {
        var firstCopy = first;
        var rest0copy = rest.Length > 0 ? rest[0] : int.MinValue;
        StatisticsHelper.GCD(first, rest).ShouldBe(expectedGCD);

        first.ShouldBe(firstCopy);
        if (rest.Length > 0)
        {
            var rest0after = rest[0];
            rest0after.ShouldBe(rest0copy);
        }
    }

    [Theory]
    [InlineData(14400uL, -120, -40, -60)]
    [InlineData(1u, 1)]
    [InlineData(0u, 0)]
    [InlineData(0u, 0, 0)]
    [InlineData(576uL, 8, 12, 24)]
    [InlineData(120uL, 120, 60)]
    [InlineData(14400uL, 120, 40, 60)]
    public void GivenValuesWhenCalcLCMThenItIsReturned(uint expectedLCM, int first, params int[] rest)
    {
        StatisticsHelper.LCM(first, rest).ShouldBe(expectedLCM);
    }
}
