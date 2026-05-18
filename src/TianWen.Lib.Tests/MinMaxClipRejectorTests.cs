using System;
using Shouldly;
using TianWen.Lib.Imaging.Calibration;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Imaging")]
public class MinMaxClipRejectorTests
{
    [Fact]
    public void DropsExactCountFromEachTail()
    {
        // 10 values: 0..9. DropLowest=1, DropHighest=2 -> mask out index 0
        // (value 0) and indices 8,9 (values 8,9).
        var column = new float[10];
        for (var i = 0; i < column.Length; i++) column[i] = i;
        var mask = new float[column.Length];

        var kept = new MinMaxClipRejector(1, 2).Reject(column, mask);

        kept.ShouldBe(7);
        mask[0].ShouldBe(0f);
        mask[8].ShouldBe(0f);
        mask[9].ShouldBe(0f);
        for (var i = 1; i < 8; i++) mask[i].ShouldBe(1f);
    }

    [Fact]
    public void ZeroDrops_KeepsAll()
    {
        var column = new float[] { 1f, 2f, 3f, 4f, 5f };
        var mask = new float[column.Length];

        var kept = new MinMaxClipRejector(0, 0).Reject(column, mask);

        kept.ShouldBe(5);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void OutOfOrderValues_RejectsByValueNotIndex()
    {
        // Values placed in arbitrary order: the lowest value (1) is at
        // index 7, the highest (10) is at index 0. The mask must reject
        // those indices, not 0 and 9.
        float[] column = [10f, 5f, 6f, 4f, 8f, 3f, 7f, 1f, 9f, 2f];
        var mask = new float[column.Length];

        var kept = new MinMaxClipRejector(1, 1).Reject(column, mask);

        kept.ShouldBe(8);
        mask[7].ShouldBe(0f); // value 1
        mask[0].ShouldBe(0f); // value 10
    }

    [Fact]
    public void DropExceedsCount_KeepsAll()
    {
        // 3 samples but want to drop 2+2=4 -> safety: keep everything.
        float[] column = [1f, 2f, 3f];
        var mask = new float[column.Length];

        var kept = new MinMaxClipRejector(2, 2).Reject(column, mask);

        kept.ShouldBe(3);
        foreach (var m in mask) m.ShouldBe(1f);
    }

    [Fact]
    public void MaskLengthMismatch_Throws()
    {
        var rejector = new MinMaxClipRejector();
        var column = new float[5];
        var mask = new float[4];

        Should.Throw<ArgumentException>(() => rejector.Reject(column, mask));
    }

    [Fact]
    public void NegativeCounts_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            var column = new float[5];
            var mask = new float[5];
            new MinMaxClipRejector(-1, 0).Reject(column, mask);
        });
    }
}
