using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The luminance plane a colour document's luma statistic is taken on (<c>Image.BuildLumaPlane</c>): four
/// pixels a lane, in parallel chunks, held bit for bit to the one-pixel-at-a-time loop it replaced. That means
/// every sample of the plane, and the minimum, whose one order-dependent part is the sign of a zero.
/// </summary>
[Collection("Imaging")]
public class LumaPlaneParityTests
{
    [Theory]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, int.MaxValue)]
    [InlineData(false, 997)]
    [InlineData(true, 997)]
    public void ThePlaneAndItsMinimumAreTheLoops(bool adu, int minPixelsPerChunk)
    {
        var image = Build(adu, seed: adu ? 3 : 5);
        ShouldMatchTheLoop(image, minPixelsPerChunk);
    }

    /// <summary>
    /// A zero minimum is the FIRST zero in walk order, whatever chunk, lane or tail the other one sits in: the
    /// loop kept the first of equal values, and -0 equals +0.
    /// </summary>
    [Theory]
    [InlineData(5, true, 20000, false, int.MaxValue)]
    [InlineData(5, false, 20000, true, int.MaxValue)]
    [InlineData(5, true, 20000, false, 997)]
    [InlineData(20000, true, 30001, false, 997)]
    [InlineData(6, false, 7, true, 997)]
    [InlineData(7, true, 6, false, 997)]
    [InlineData(33666, true, 100, false, 997)]
    [InlineData(100, true, 33666, false, 997)]
    public void AZeroMinimumKeepsTheSignOfTheFirstZero(int a, bool aNegative, int b, bool bNegative, int minPixelsPerChunk)
    {
        var image = Build(adu: false, seed: 7, positiveOnly: true);
        SetZero(image, a, aNegative);
        SetZero(image, b, bNegative);

        var min = ShouldMatchTheLoop(image, minPixelsPerChunk);

        var firstIsNegative = a < b ? aNegative : bNegative;
        Bits(min).ShouldBe(firstIsNegative ? Bits(-0f) : Bits(0f), "the minimum is the first zero, sign and all");
    }

    [Fact]
    public async Task TheLumaStatisticIsTheOneTheLoopsPlaneGives()
    {
        // 1600 x 1400 = 2.24 M pixels: at the default sizes the plane is built in two chunks or more and its
        // bins are walked in two bands or more.
        const int width = 1600;
        const int height = 1400;
        var image = Build(adu: true, seed: 11, width: width, height: height);
        var ct = TestContext.Current.CancellationToken;

        var actual = await image.GetLumaStretchStatsAsync(ct);

        var plane = new float[height, width];
        var min = ScalarLoop(image, plane, needsNorm: !image.HasUnitScalePeak, normFactor: 1f / image.MaxValue);
        if (min == float.MaxValue) min = 0f;
        var lumaImage = new Image([plane], BitDepth.Float32, 1f, min, 0f, image.ImageMeta with { SensorType = SensorType.Monochrome });
        var (pedestal, median, mad) = lumaImage.GetPedestralMedianAndMADScaledToUnit(0);

        Bits(actual.Pedestal).ShouldBe(Bits(pedestal));
        Bits(actual.Median).ShouldBe(Bits(median));
        Bits(actual.MAD).ShouldBe(Bits(mad));
    }

    private static float ShouldMatchTheLoop(Image image, int minPixelsPerChunk)
    {
        var (_, width, height) = image.Shape;
        var needsNorm = !image.HasUnitScalePeak;
        var normFactor = 1f / image.MaxValue;

        var expected = new float[height, width];
        var expectedMin = ScalarLoop(image, expected, needsNorm, normFactor);
        var actual = new float[height, width];
        var actualMin = image.BuildLumaPlane(actual, needsNorm, normFactor, minPixelsPerChunk);

        Bits(actualMin).ShouldBe(Bits(expectedMin), "the minimum");
        var e = MemoryMarshal.CreateReadOnlySpan(ref expected[0, 0], expected.Length);
        var a = MemoryMarshal.CreateReadOnlySpan(ref actual[0, 0], actual.Length);
        for (var i = 0; i < e.Length; i++)
        {
            if (Bits(a[i]) != Bits(e[i]))
            {
                Bits(a[i]).ShouldBe(Bits(e[i]), $"luminance at {i} ({i % width}, {i / width})");
            }
        }

        return actualMin;
    }

    // The loop as it was, word for word, writing into the plane it is handed.
    private static float ScalarLoop(Image image, float[,] lumaChannel, bool needsNorm, float normFactor)
    {
        var lumaMin = float.MaxValue;
        var n = lumaChannel.Length;
        if (n > 0)
        {
            var r = image.GetChannelSpan(0);
            var g = image.GetChannelSpan(1);
            var b = image.GetChannelSpan(2);
            var luma = MemoryMarshal.CreateSpan(ref lumaChannel[0, 0], n);
            for (var i = 0; i < n; i++)
            {
                var rv = r[i];
                var gv = g[i];
                var bv = b[i];
                if (float.IsNaN(rv) || float.IsNaN(gv) || float.IsNaN(bv))
                {
                    luma[i] = float.NaN;
                }
                else
                {
                    if (needsNorm) { rv *= normFactor; gv *= normFactor; bv *= normFactor; }
                    var l = LumaWeighting.Rec709.ToLuma(rv, gv, bv);
                    luma[i] = l;
                    if (l < lumaMin) lumaMin = l;
                }
            }
        }

        return lumaMin;
    }

    private static void SetZero(Image image, int index, bool negative)
    {
        var (_, width, _) = image.Shape;
        var zero = negative ? -0f : 0f;
        for (var c = 0; c < 3; c++)
        {
            image.GetChannelArray(c)[index / width, index % width] = zero;
        }
    }

    // Odd sizes, so a chunk ends mid-vector and the plane ends in a tail. NaN in one channel only, both signs
    // of zero, calibrated negatives and an infinity, unless only positive values are wanted.
    private static Image Build(bool adu, int seed, int width = 257, int height = 131, bool positiveOnly = false)
    {
        var full = adu ? 4095f : 1f;
        var rng = new Random(seed);
        var planes = new float[3][,];
        var min = float.MaxValue;
        for (var c = 0; c < 3; c++)
        {
            var plane = planes[c] = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var roll = rng.NextDouble();
                    float v;
                    if (positiveOnly) { v = full * (0.02f + 0.9f * (float)rng.NextDouble()); }
                    else if (roll < 0.01) { v = float.NaN; }
                    else if (roll < 0.02) { v = -0f; }
                    else if (roll < 0.03) { v = 0f; }
                    else if (roll < 0.05) { v = -full * (float)(rng.NextDouble() * 0.002); }
                    else if (roll < 0.052) { v = float.PositiveInfinity; }
                    else if (roll < 0.10) { v = full * (float)rng.NextDouble(); }
                    else { v = full * (0.02f + 0.004f * (float)rng.NextDouble()); }
                    plane[y, x] = v;
                    if (!float.IsNaN(v) && v < min) { min = v; }
                }
            }
        }

        var meta = new ImageMeta("luma", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);
        return new Image(planes, adu ? BitDepth.Int16 : BitDepth.Float32, full, min, 0f, meta);
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}
