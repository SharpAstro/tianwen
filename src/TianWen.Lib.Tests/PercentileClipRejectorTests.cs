using System;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class PercentileClipRejectorTests
{
    [Fact]
    public void DropsLowestAndHighestFraction()
    {
        // 10 values: 0..9. With LowFraction=0.1, HighFraction=0.1
        // -> drop 1 lowest (index 0, value 0) + 1 highest (index 9, value 9).
        var column = new float[10];
        for (var i = 0; i < column.Length; i++) column[i] = i;
        var mask = new float[column.Length];

        var kept = new PercentileClipRejector(0.1f, 0.1f).Reject(column, mask);

        kept.ShouldBe(8);
        mask[0].ShouldBe(0f);
        mask[9].ShouldBe(0f);
        for (var i = 1; i < 9; i++) mask[i].ShouldBe(1f);
    }

    [Fact]
    public void AsymmetricFractions_OnlyHighRejected()
    {
        // Drop 0% low + 30% high on 10 samples -> 3 highest get clipped.
        var column = new float[10];
        for (var i = 0; i < column.Length; i++) column[i] = i;
        var mask = new float[column.Length];

        var kept = new PercentileClipRejector(0.0f, 0.3f).Reject(column, mask);

        kept.ShouldBe(7);
        mask[9].ShouldBe(0f);
        mask[8].ShouldBe(0f);
        mask[7].ShouldBe(0f);
        for (var i = 0; i < 7; i++) mask[i].ShouldBe(1f);
    }

    [Fact]
    public void ZeroFractions_KeepsAll()
    {
        var column = new float[] { 1f, 2f, 3f, 4f, 5f };
        var mask = new float[column.Length];

        var kept = new PercentileClipRejector(0f, 0f).Reject(column, mask);

        kept.ShouldBe(5);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void SmallNRoundDownToZero_KeepsAll()
    {
        // 5 samples * 0.1 = 0.5 -> floor -> 0 drops. Match the small-N
        // safety behaviour the other rejectors use.
        var column = new float[] { 1f, 2f, 3f, 4f, 5f };
        var mask = new float[column.Length];

        var kept = new PercentileClipRejector(0.1f, 0.1f).Reject(column, mask);

        kept.ShouldBe(5);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void OutOfOrderValues_RejectsByValueNotIndex()
    {
        // Make sure we sort before clipping -- the lowest value sits at
        // index 7, the highest at index 0. The rejector must mark THOSE
        // indices, not 0 and 9.
        float[] column = [10f, 5f, 6f, 4f, 8f, 3f, 7f, 1f, 9f, 2f];
        var mask = new float[column.Length];

        var kept = new PercentileClipRejector(0.1f, 0.1f).Reject(column, mask);

        kept.ShouldBe(8);
        mask[7].ShouldBe(0f); // value 1 (lowest)
        mask[0].ShouldBe(0f); // value 10 (highest)
        for (var i = 0; i < column.Length; i++)
        {
            if (i != 7 && i != 0) mask[i].ShouldBe(1f);
        }
    }

    [Fact]
    public void TooFewSamples_ReturnsAllKept()
    {
        float[] column = [1f, 2f];
        var mask = new float[column.Length];

        var kept = new PercentileClipRejector(0.1f, 0.1f).Reject(column, mask);

        kept.ShouldBe(2);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void MaskLengthMismatch_Throws()
    {
        var rejector = new PercentileClipRejector();
        var column = new float[5];
        var mask = new float[4];

        Should.Throw<ArgumentException>(() => rejector.Reject(column, mask));
    }

    [Fact]
    public void FractionOutOfRange_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            var column = new float[10];
            var mask = new float[10];
            new PercentileClipRejector(0.5f, 0f).Reject(column, mask);
        });
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            var column = new float[10];
            var mask = new float[10];
            new PercentileClipRejector(-0.1f, 0f).Reject(column, mask);
        });
    }
}
