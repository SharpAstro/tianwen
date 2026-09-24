using System;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="Image.SplitBayerChannels"/> / <see cref="Image.MergeBayerChannels"/> over ALL FOUR
/// Bayer patterns.
///
/// <para>The gate reads <c>SensorType is not SensorType.RGGB</c>, which looks like a restriction to
/// one pattern and is not: the enum value names "this is a Bayer CFA" and the rotation rides on
/// <see cref="ImageMeta.BayerOffsetX"/> / <see cref="ImageMeta.BayerOffsetY"/>, which
/// <c>SensorTypeEx.FromFITSValue</c> derives from the file's own BAYERPAT. The wording cost a real
/// wrong conclusion about a GRBG camera, so the behaviour is pinned rather than described.</para>
///
/// <para>Each case builds a mosaic whose four photosites carry four DISTINCT values, so a split that
/// mixed up the offsets could not accidentally pass -- and the round trip has to return the exact
/// original mosaic.</para>
/// </summary>
[Collection("Imaging")]
public class BayerSplitPatternTests
{
    private const float RedValue = 10f, Green1Value = 20f, Green2Value = 30f, BlueValue = 40f;

    /// <summary>Sensor offsets each BAYERPAT maps to, mirroring <c>SensorTypeEx.FromFITSValue</c>.</summary>
    public static TheoryData<string, int, int> Patterns => new()
    {
        { "RGGB", 0, 0 },
        { "GRBG", 1, 0 },
        { "GBRG", 0, 1 },
        { "BGGR", 1, 1 },
    };

    [Theory]
    [MemberData(nameof(Patterns))]
    public void EveryBayerPatternSplitsToTheRightSubPlanes(string pattern, int offsetX, int offsetY)
    {
        const int w = 8, h = 6;
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                // Which colour sits here is decided by parity RELATIVE to the pattern's offset --
                // the same arithmetic the split uses, stated independently so the test is not just
                // the implementation repeated back.
                var onRedRow = (y & 1) == (offsetY & 1);
                var onRedCol = (x & 1) == (offsetX & 1);
                plane[y, x] = (onRedRow, onRedCol) switch
                {
                    (true, true) => RedValue,
                    (true, false) => Green1Value,
                    (false, true) => Green2Value,
                    (false, false) => BlueValue,
                };
            }
        }

        var meta = new ImageMeta() with
        {
            SensorType = SensorType.RGGB,
            BayerOffsetX = offsetX,
            BayerOffsetY = offsetY,
        };
        var mosaic = new Image([plane], BitDepth.Float32, BlueValue, RedValue, 0f, meta);

        var split = mosaic.SplitBayerChannels();
        split.ChannelCount.ShouldBe(4, $"{pattern}: split must yield [R, G1, G2, B]");
        split.Width.ShouldBe(w / 2);
        split.Height.ShouldBe(h / 2);

        // Each sub-plane must be CONSTANT at its own colour's value. A wrong offset mixes two
        // colours into one plane, which shows up here immediately.
        var expected = new[] { RedValue, Green1Value, Green2Value, BlueValue };
        var names = new[] { "R", "G1", "G2", "B" };
        for (var c = 0; c < 4; c++)
        {
            var span = split.GetChannelSpan(c);
            foreach (var v in span)
            {
                v.ShouldBe(expected[c], $"{pattern}: sub-plane {names[c]} picked up the wrong photosite");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void MergingIntoAPlaneLandsOnExactlyWhatMergeAllocates(string pattern, int offsetX, int offsetY)
    {
        // The rented form a live master and the batch drizzle merge into: the same mosaic, and every sample
        // of the target overwritten (it arrives full of NaN, as a recycled plane may be full of anything).
        const int w = 8, h = 6;
        var plane = new float[h, w];
        var next = 1f;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                plane[y, x] = next++;
            }
        }

        var meta = new ImageMeta() with { SensorType = SensorType.RGGB, BayerOffsetX = offsetX, BayerOffsetY = offsetY };
        var split = new Image([plane], BitDepth.Float32, next, 1f, 0f, meta).SplitBayerChannels();
        var target = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                target[y, x] = float.NaN;
            }
        }

        var into = split.MergeBayerChannelsInto(target);
        var allocated = split.MergeBayerChannels();

        into.GetChannelArray(0).ShouldBeSameAs(target, $"{pattern}: merged into the caller's plane");
        into.GetChannelSpan(0).SequenceEqual(allocated.GetChannelSpan(0)).ShouldBeTrue($"{pattern}: the same mosaic");
        (into.BitDepth, into.MaxValue, into.MinValue, into.Pedestal, into.ImageMeta)
            .ShouldBe((allocated.BitDepth, allocated.MaxValue, allocated.MinValue, allocated.Pedestal, allocated.ImageMeta));
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void SplitThenMergeReturnsTheOriginalMosaic(string pattern, int offsetX, int offsetY)
    {
        const int w = 8, h = 6;
        var plane = new float[h, w];
        var next = 1f;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                plane[y, x] = next++;   // every pixel distinct: any transposition is visible
            }
        }

        var meta = new ImageMeta() with
        {
            SensorType = SensorType.RGGB,
            BayerOffsetX = offsetX,
            BayerOffsetY = offsetY,
        };
        var mosaic = new Image([plane], BitDepth.Float32, next, 1f, 0f, meta);

        var roundTripped = mosaic.SplitBayerChannels().MergeBayerChannels();

        roundTripped.Width.ShouldBe(w, $"{pattern}: merge must restore the full raster");
        roundTripped.Height.ShouldBe(h);
        var original = mosaic.GetChannelSpan(0);
        var result = roundTripped.GetChannelSpan(0);
        for (var i = 0; i < original.Length; i++)
        {
            result[i].ShouldBe(original[i], $"{pattern}: round trip moved pixel {i}");
        }
    }
}
