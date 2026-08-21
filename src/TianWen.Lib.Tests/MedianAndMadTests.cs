using System;
using System.Numerics;
using Shouldly;
using TianWen.Lib.Stat;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="StatisticsHelper.MedianAndMad(Span{float})"/> and
/// <see cref="StatisticsHelper.UpperMedianAndMad"/> replaced the same six lines written out
/// longhand in four places, and their deviation pass is VECTORISED where each of those was scalar.
/// So the oracle here is the longhand scalar form, and the lengths deliberately sweep across
/// <c>Vector&lt;float&gt;.Count</c> boundaries -- a vectorised loop with a remainder tail is exactly
/// the shape that is correct for every length except the ones nobody tested.
/// </summary>
public class MedianAndMadTests
{
    [Fact]
    public void ItAgreesWithTheLonghandScalarFormAtEveryVectorWidthBoundary()
    {
        var width = Vector<float>.Count;
        var rng = new Random(4242);

        // 1 through 3 full vectors plus a remainder, so every tail length occurs, and a size well
        // past any unrolling.
        for (var n = 1; n <= 3 * width + 2; n++)
        {
            Check(n);
        }
        Check(10_000);
        Check(10_001);

        void Check(int n)
        {
            var source = new float[n];
            for (var i = 0; i < n; i++)
            {
                source[i] = (float)(rng.NextDouble() * 500.0 - 250.0);
            }

            var (median, mad) = StatisticsHelper.MedianAndMad(source[..]);
            var (refMedian, refMad) = LonghandScalar(source[..]);

            median.ShouldBe(refMedian, $"median, n={n}");
            mad.ShouldBe(refMad, $"mad, n={n}");
        }

        static (float Median, float Mad) LonghandScalar(Span<float> values)
        {
            var median = StatisticsHelper.MedianFast(values);
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = MathF.Abs(values[i] - median);
            }
            return (median, StatisticsHelper.MedianFast(values));
        }
    }

    [Fact]
    public void TheUpperMedianVariantAgreesWithItsOwnLonghandForm()
    {
        var width = Vector<float>.Count;
        var rng = new Random(99);

        for (var n = 1; n <= 2 * width + 3; n++)
        {
            var source = new float[n];
            for (var i = 0; i < n; i++)
            {
                source[i] = (float)(rng.NextDouble() * 80.0);
            }

            var (median, mad) = StatisticsHelper.UpperMedianAndMad(source[..]);

            var work = source[..];
            var k = n / 2;
            var refMedian = StatisticsHelper.NthSmallest(work, k);
            for (var i = 0; i < n; i++)
            {
                work[i] = MathF.Abs(work[i] - refMedian);
            }
            var refMad = StatisticsHelper.NthSmallest(work, k);

            median.ShouldBe(refMedian, $"median, n={n}");
            mad.ShouldBe(refMad, $"mad, n={n}");
        }
    }

    /// <summary>
    /// The two conventions are a named pair, not a flag, because they genuinely differ -- and only
    /// for an even count with distinct middle values. Pinned here as well as in
    /// <see cref="NthSmallestTests"/> so the difference is visible from whichever entry point a
    /// reader arrives at.
    /// </summary>
    [Fact]
    public void TheTwoConventionsDifferOnlyInTheirMedianDefinition()
    {
        float[] Source() => [1f, 2f, 8f, 9f];

        StatisticsHelper.MedianAndMad(Source()).Median.ShouldBe(5f);
        StatisticsHelper.UpperMedianAndMad(Source()).Median.ShouldBe(8f);
    }

    [Fact]
    public void TheDoubleOverloadAgreesWithItsLonghandForm()
    {
        var width = Vector<double>.Count;
        var rng = new Random(7);

        for (var n = 1; n <= 3 * width + 2; n++)
        {
            var source = new double[n];
            for (var i = 0; i < n; i++)
            {
                source[i] = rng.NextDouble() * 1000.0 - 500.0;
            }

            var (median, mad) = StatisticsHelper.MedianAndMad(source[..]);

            var work = source[..];
            var refMedian = StatisticsHelper.MedianFast(work);
            for (var i = 0; i < n; i++)
            {
                work[i] = Math.Abs(work[i] - refMedian);
            }
            var refMad = StatisticsHelper.MedianFast(work);

            median.ShouldBe(refMedian, $"median, n={n}");
            mad.ShouldBe(refMad, $"mad, n={n}");
        }
    }

    /// <summary>
    /// The documented contract: the buffer comes back holding deviations, not the input values. Two
    /// of the four migrated call sites relied on that (their buffer is scratch), so it is behaviour
    /// rather than an implementation detail.
    /// </summary>
    [Fact]
    public void TheBufferIsLeftHoldingDeviations()
    {
        var values = new float[] { 10f, 2f, 6f, 30f, 4f };
        var (median, _) = StatisticsHelper.MedianAndMad(values);

        median.ShouldBe(6f);
        Array.Sort(values);
        values.ShouldBe([0f, 2f, 4f, 4f, 24f]);
    }

    [Fact]
    public void AnEmptySpanYieldsNotANumberForBoth()
    {
        var (median, mad) = StatisticsHelper.MedianAndMad(Array.Empty<float>());
        median.ShouldBe(float.NaN);
        mad.ShouldBe(float.NaN);
    }
}
