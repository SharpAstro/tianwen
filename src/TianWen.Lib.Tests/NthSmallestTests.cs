using System;
using Shouldly;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="StatisticsHelper.NthSmallest"/> replaced an <see cref="Array.Sort(Array)"/>-then-index
/// pair on the document-open path, so what needs pinning is that it is the SAME order statistic --
/// not merely a similar average.
/// </summary>
/// <remarks>
/// <para>These are synthetic and use DISTINCT values on purpose. The real-fixture parity test
/// (<see cref="HistogramSelectionParityTests"/>) cannot make this distinction: in quantised astro
/// data thousands of samples share the median ADU, so the two middle values tie and
/// <see cref="StatisticsHelper.MedianFast"/> is indistinguishable from the upper median. That was
/// measured, not assumed -- swapping the production call to <c>MedianFast</c> left every fixture
/// assertion green. So the convention has to be pinned where it is observable, or it is not pinned at
/// all.</para>
/// </remarks>
public class NthSmallestTests
{
    [Fact]
    public void ItReturnsTheSameElementAsSortingAndIndexing()
    {
        var rng = new Random(1234);
        for (var trial = 0; trial < 200; trial++)
        {
            var n = 1 + rng.Next(300);
            var values = new float[n];
            for (var i = 0; i < n; i++)
            {
                values[i] = (float)(rng.NextDouble() * 2000.0 - 1000.0);
            }

            var sorted = values[..];
            Array.Sort(sorted);

            for (var k = 0; k < n; k += Math.Max(1, n / 7))
            {
                var scratch = values[..];
                StatisticsHelper.NthSmallest(scratch, k).ShouldBe(sorted[k],
                    $"trial {trial}, n={n}, k={k}");
            }
        }
    }

    /// <summary>
    /// The distinction that matters at the call site: for an even count, <c>sorted[n / 2]</c> is the
    /// UPPER of the two middle values, while <c>MedianFast</c> averages them. Both are defensible
    /// definitions of a median; they are not interchangeable, and the star-masked path's result feeds
    /// the stretch, so it keeps the one it always had.
    /// </summary>
    [Fact]
    public void ForAnEvenCountItIsTheUpperMiddleValueWhereMedianFastAverages()
    {
        float[] Source() => [10f, 20f, 30f, 40f];

        StatisticsHelper.NthSmallest(Source(), 4 / 2).ShouldBe(30f);
        StatisticsHelper.MedianFast(Source()).ShouldBe(25f);
    }

    [Fact]
    public void AnOddCountAgreesWithMedianFast()
    {
        float[] Source() => [10f, 20f, 30f, 40f, 50f];

        StatisticsHelper.NthSmallest(Source(), 5 / 2).ShouldBe(30f);
        StatisticsHelper.MedianFast(Source()).ShouldBe(30f);
    }

    /// <summary>
    /// Heavy ties are the real-data case (a quantised background), and they are also the input that
    /// breaks a naive quickselect partition, so the same array is worth a direct test.
    /// </summary>
    [Fact]
    public void ItSurvivesAnArrayThatIsAlmostAllTies()
    {
        var values = new float[10_001];
        Array.Fill(values, 7f);
        values[0] = 1f;
        values[^1] = 9f;

        var sorted = values[..];
        Array.Sort(sorted);

        StatisticsHelper.NthSmallest(values[..], 0).ShouldBe(sorted[0]);
        StatisticsHelper.NthSmallest(values[..], values.Length / 2).ShouldBe(sorted[values.Length / 2]);
        StatisticsHelper.NthSmallest(values[..], values.Length - 1).ShouldBe(sorted[^1]);
    }

    [Fact]
    public void AnEmptySpanIsNotANumberAndTheIndexIsClamped()
    {
        StatisticsHelper.NthSmallest([], 0).ShouldBe(float.NaN);
        StatisticsHelper.NthSmallest([5f, 1f, 3f], -4).ShouldBe(1f);
        StatisticsHelper.NthSmallest([5f, 1f, 3f], 99).ShouldBe(5f);
    }
}
