using System;
using System.Drawing;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.Stacking;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Characterisation tests for <see cref="Normalizer.ComputeStats(Image)"/> and its box overload.
/// </summary>
/// <remarks>
/// <para>They sit on the stacking hot path (once per warped frame, per channel, over 244 frames on
/// the Vela project). The per-frame floor and median drive <c>(x - floor) * target / (median - floor)</c>,
/// so a quiet change here rescales every sub and shows up as a stacking artefact rather than as a
/// failure.</para>
/// <para>The floor is the frame's pedestal and never a pixel statistic: the minimum it replaced let
/// one outlier pixel set the gain of a whole frame and channel (see
/// <see cref="NormalizationStats.PerChannelFloor"/>). NaN handling is the other substance: warped
/// frames carry large NaN edge regions by construction, and NaN is excluded from the median because
/// quickselect's <c>&lt;</c>/<c>&gt;</c> comparisons are false against NaN and would land it in
/// unpredictable partition positions.</para>
/// </remarks>
public class NormalizerStatsTests
{
    [Fact]
    public void TheMedianComesFromTheActualPixelsAndTheFloorFromThePedestal()
    {
        // 3x3, values 1..9, pedestal 3: the minimum pixel is 1 and must not appear anywhere.
        var image = Mono([
            [1f, 2f, 3f],
            [4f, 5f, 6f],
            [7f, 8f, 9f],
        ], pedestal: 3f);

        var stats = Normalizer.ComputeStats(image);

        stats.PerChannelFloor[0].ShouldBe(3f);
        stats.PerChannelMedian[0].ShouldBe(5f);
    }

    /// <summary>
    /// An even count averages the two middle values (<c>MedianFast</c>'s convention). Pinned because
    /// the star-masked path in <c>Image</c> deliberately uses the OTHER convention, and a reader who
    /// has seen that one should be able to confirm this one differs on purpose.
    /// </summary>
    [Fact]
    public void AnEvenPixelCountAveragesTheTwoMiddleValues()
    {
        var image = Mono([
            [1f, 2f],
            [3f, 4f],
        ]);

        Normalizer.ComputeStats(image).PerChannelMedian[0].ShouldBe(2.5f);
    }

    [Fact]
    public void NaNIsExcludedFromTheMedian()
    {
        // Without the NaN skip the median would depend on where the partition happened to leave
        // the NaN.
        var image = Mono([
            [float.NaN, 2f, 3f],
            [4f, 5f, float.NaN],
            [6f, 7f, float.NaN],
        ]);

        var stats = Normalizer.ComputeStats(image);

        // Finite values are 2,3,4,5,6,7 -- an even count, so (4 + 5) / 2.
        stats.PerChannelMedian[0].ShouldBe(4.5f);
    }

    [Fact]
    public void AnAllNaNChannelReportsTheFloorAsItsMedian()
    {
        // No sky to read: the median falls to the floor, and ComputeScale then answers identity.
        var image = Mono([
            [float.NaN, float.NaN],
            [float.NaN, float.NaN],
        ], pedestal: 2f);

        var stats = Normalizer.ComputeStats(image);

        stats.PerChannelFloor[0].ShouldBe(2f);
        stats.PerChannelMedian[0].ShouldBe(2f);
        Normalizer.ComputeScale(stats.PerChannelMedian[0], stats.PerChannelFloor[0], 0.5f).ShouldBe(1f);
    }

    [Fact]
    public void EachChannelIsComputedIndependently()
    {
        var image = new Image(
            [
                To2D([[1f, 2f], [3f, 4f]]),
                To2D([[10f, 20f], [30f, 40f]]),
                To2D([[100f, float.NaN], [300f, 400f]]),
            ],
            BitDepth.Float32, maxValue: 400f, minValue: 1f, pedestal: 0f, imageMeta: Meta(SensorType.Color));

        var stats = Normalizer.ComputeStats(image);

        stats.PerChannelFloor.ShouldBe([0f, 0f, 0f]);
        stats.PerChannelMedian.ShouldBe([2.5f, 25f, 300f]);
    }

    [Fact]
    public void TheBoxOverloadOnlyLooksInsideTheBox()
    {
        // The extremes live outside the box, so a leak would be obvious in the median.
        var image = Mono([
            [-999f, -999f, -999f, -999f],
            [-999f, 10f, 20f, -999f],
            [-999f, 30f, 40f, -999f],
            [-999f, 999f, 999f, -999f],
        ]);

        var stats = Normalizer.ComputeStats(image, new Rectangle(1, 1, 2, 2));

        stats.PerChannelMedian[0].ShouldBe(25f);
    }

    [Fact]
    public void ABoxThatOverhangsTheImageIsClampedToIt()
    {
        var image = Mono([
            [1f, 2f],
            [3f, 4f],
        ]);

        // Asking for 10x10 at (1,1) can only yield the single pixel at (1,1).
        var stats = Normalizer.ComputeStats(image, new Rectangle(1, 1, 10, 10));

        stats.PerChannelMedian[0].ShouldBe(4f);
    }

    /// <summary>
    /// A disjoint intersection means the caller's footprint arithmetic produced nothing usable;
    /// falling back to whole-image stats is deliberate, because a median read from nowhere would
    /// leave the frame on the identity scale while its neighbours are normalised.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(5, 5, 2, 2)]
    [InlineData(-4, -4, 2, 2)]
    public void AnEmptyOrDisjointBoxFallsBackToTheWholeImage(int x, int y, int w, int h)
    {
        var image = Mono([
            [1f, 2f, 3f],
            [4f, 5f, 6f],
            [7f, 8f, 9f],
        ]);

        var stats = Normalizer.ComputeStats(image, new Rectangle(x, y, w, h));
        var whole = Normalizer.ComputeStats(image);

        stats.PerChannelFloor[0].ShouldBe(whole.PerChannelFloor[0]);
        stats.PerChannelMedian[0].ShouldBe(whole.PerChannelMedian[0]);
    }

    [Fact]
    public void ABoxOfAllNaNReportsTheFloorRatherThanPoisoning()
    {
        var image = Mono([
            [1f, 2f, 3f],
            [4f, float.NaN, float.NaN],
            [7f, float.NaN, float.NaN],
        ]);

        var stats = Normalizer.ComputeStats(image, new Rectangle(1, 1, 2, 2));

        stats.PerChannelFloor[0].ShouldBe(0f);
        stats.PerChannelMedian[0].ShouldBe(0f);
    }

    /// <summary>
    /// A frame big enough that the compaction pass crosses vector widths and buffer-slicing
    /// boundaries, with a NaN border of the kind a warped frame actually has.
    /// </summary>
    [Fact]
    public void ALargerFrameWithANaNBorderAgreesWithADirectComputation()
    {
        const int W = 133;
        const int H = 97;
        var plane = new float[H, W];
        var rng = new Random(31);
        var finite = new System.Collections.Generic.List<float>();
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var border = x < 7 || y < 5 || x >= W - 3 || y >= H - 4;
                if (border)
                {
                    plane[y, x] = float.NaN;
                }
                else
                {
                    var v = (float)(rng.NextDouble() * 100.0 + 5.0);
                    plane[y, x] = v;
                    finite.Add(v);
                }
            }
        }

        var stats = Normalizer.ComputeStats(new Image([plane], BitDepth.Float32, 105f, 5f, 5f,
            Meta(SensorType.Monochrome)));

        var sorted = finite.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        var expectedMedian = (sorted.Length & 1) == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) * 0.5f;

        stats.PerChannelFloor[0].ShouldBe(5f);
        stats.PerChannelMedian[0].ShouldBe(expectedMedian);
    }

    private static Image Mono(float[][] rows, float pedestal = 0f)
        => new([To2D(rows)], BitDepth.Float32, maxValue: 1f, minValue: 0f, pedestal: pedestal,
            imageMeta: Meta(SensorType.Monochrome));

    private static float[,] To2D(float[][] rows)
    {
        var h = rows.Length;
        var w = rows[0].Length;
        var plane = new float[h, w];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++) { plane[y, x] = rows[y][x]; }
        }
        return plane;
    }

    private static ImageMeta Meta(SensorType sensorType)
        => new("synth", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, sensorType, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);
}
