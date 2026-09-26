using System;
using Shouldly;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.Onnx;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The NAFNet pre-stretch measures COVERED pixels only. A master's canvas ring is exact zero where no
/// frame reached, and counted it was every channel's floor: three E13 masters with a bright sky read
/// 0.20 to 0.26 against the 0.125 linearity threshold (their ringless neighbours read 0.06), so the
/// exporter refused them and a runner would have handed them to the net unstretched, while a ringed
/// master that passed was stretched from 0 instead of from its own darkest sky.
/// </summary>
public class NafnetInputStretchRingTests
{
    private const int Size = 200;
    private const int RingRows = 12;
    private const int RingColumns = 16;

    /// <summary>
    /// A linear frame with a bright sky (0.25 with a +-0.01 ripple) and a few stars up to 0.9: the
    /// shape of an unfiltered broadband master under a bright sky, whose sky sits well above the
    /// threshold when measured from zero and well below it when measured from its own floor.
    /// </summary>
    private static float[][,] BrightSky(int channels = 3)
    {
        var planes = new float[channels][,];
        for (var c = 0; c < channels; c++)
        {
            var plane = new float[Size, Size];
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    plane[y, x] = 0.25f + 0.01f * MathF.Sin(0.37f * x + 0.61f * y + c);
                }
            }
            foreach (var (sx, sy) in new[] { (60, 70), (140, 50), (120, 150), (90, 120) })
            {
                for (var y = sy - 4; y <= sy + 4; y++)
                {
                    for (var x = sx - 4; x <= sx + 4; x++)
                    {
                        var d2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                        plane[y, x] += 0.65f * MathF.Exp(-d2 / 4f);
                    }
                }
            }
            planes[c] = plane;
        }
        return planes;
    }

    /// <summary>Zeroes the top <see cref="RingRows"/> rows and the left <see cref="RingColumns"/>
    /// columns in every channel: a canvas ring, which reaches the border by construction.</summary>
    private static void CutRing(float[][,] planes)
    {
        foreach (var plane in planes)
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    if (y < RingRows || x < RingColumns) plane[y, x] = 0f;
                }
            }
        }
    }

    private static bool InRing(int y, int x) => y < RingRows || x < RingColumns;

    private static Image ToImage(float[][,] planes) => new Image(planes, BitDepth.Float32, 1f, 0f, 0f, default);

    [Fact]
    public void AbsentPixelsIsTheBorderRingAndNotAnInteriorIsland()
    {
        var planes = BrightSky();
        CutRing(planes);
        // An interior island of exact zeros in every channel: a clipped measurement, not absence.
        foreach (var plane in planes)
        {
            for (var y = 100; y < 104; y++)
            {
                for (var x = 100; x < 104; x++) plane[y, x] = 0f;
            }
        }

        var absent = ToImage(planes).AbsentPixels().ShouldNotBeNull();

        absent.PopCount().ShouldBe(RingRows * Size + RingColumns * (Size - RingRows));
        absent[0, Size - 1].ShouldBeTrue();
        absent[Size - 1, 0].ShouldBeTrue();
        absent[101, 101].ShouldBeFalse("an interior island does not reach the border and is not the ring");
        absent[RingRows, RingColumns].ShouldBeFalse();
    }

    [Fact]
    public void AFrameWithNoRingHasNoAbsentPixels()
    {
        ToImage(BrightSky()).AbsentPixels().ShouldBeNull();
    }

    /// <summary>
    /// With nothing excluded the measurement is the one the stretch and the linearity test always
    /// took, bit for bit: every frame without a ring, i.e. almost every frame, is untouched by the fix.
    /// </summary>
    [Fact]
    public void WithNothingExcludedTheMeasurementIsTheOriginalBitForBit()
    {
        var planes = BrightSky();
        CutRing(planes);
        planes[1][50, 60] = float.NaN;
        var image = ToImage(planes);

        for (var c = 0; c < image.ChannelCount; c++)
        {
            var span = image.GetChannelSpan(c);
            var min = float.PositiveInfinity;
            foreach (var v in span)
            {
                if (!float.IsNaN(v) && v < min) min = v;
            }
            var shifted = new System.Collections.Generic.List<float>();
            foreach (var v in span)
            {
                if (!float.IsNaN(v)) shifted.Add(v - min);
            }
            var median = TianWen.Lib.Stat.StatisticsHelper.MedianFast(shifted.ToArray().AsSpan());

            var (measuredMin, measuredMedian) = image.MinAndShiftedMedian(c);

            measuredMin.ShouldBe(min);
            measuredMedian.ShouldBe(median);
        }
    }

    [Fact]
    public void ARingedBrightSkyIsLinearAndIsStretchedFromItsCoveredFloor()
    {
        var planes = BrightSky();
        CutRing(planes);
        var image = ToImage(planes);

        // Counted, the ring is the floor and the whole sky reads as "above it": the refusal E13 met.
        image.MinAndShiftedMedian(0).ShiftedMedian.ShouldBeGreaterThan(AiNafnetInputs.StretchAutoDetectMedianThreshold);

        var (stretched, applied, origMin, balances) = ChunkedNafnetRunner.ApplyInputStretch(image);

        applied.ShouldBeTrue("a linear frame must take the stretch whatever its ring");
        origMin.ShouldNotBeNull();
        balances.ShouldNotBeNull();
        for (var c = 0; c < image.ChannelCount; c++)
        {
            var coveredMin = float.PositiveInfinity;
            var covered = new System.Collections.Generic.List<float>();
            var src = image.GetChannelSpan(c);
            var dst = stretched.GetChannelSpan(c);
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var i = y * Size + x;
                    if (InRing(y, x))
                    {
                        dst[i].ShouldBe(0f, "a ring pixel sits below the covered floor and clamps to zero");
                        continue;
                    }
                    coveredMin = MathF.Min(coveredMin, src[i]);
                    covered.Add(dst[i]);
                }
            }
            origMin[c].ShouldBe(coveredMin, $"channel {c}'s floor is its darkest covered pixel, not the ring's zero");
            covered.Sort();
            covered[covered.Count / 2].ShouldBe((float)AiNafnetInputs.TargetMedian, 0.005f,
                $"channel {c}'s covered median lands on the target the net was trained at");
        }
    }

    /// <summary>
    /// Only the ring is left out. An interior island of zeros is a measurement (a clipped pixel), so it
    /// still sets the floor, and a bright frame with one still reads as not linear: the fix is about
    /// what no frame reached, not about zeros.
    /// </summary>
    [Fact]
    public void AnInteriorIslandOfZerosStillCounts()
    {
        var planes = BrightSky();
        foreach (var plane in planes)
        {
            for (var y = 100; y < 104; y++)
            {
                for (var x = 100; x < 104; x++) plane[y, x] = 0f;
            }
        }
        var image = ToImage(planes);

        var (_, applied, _, _) = ChunkedNafnetRunner.ApplyInputStretch(image);

        applied.ShouldBeFalse();
        image.MinAndShiftedMedian(0, image.AbsentPixels()).Min.ShouldBe(0f);
    }

    [Fact]
    public void CopyAbsentCopiesTheRingAndNothingElse()
    {
        var planes = BrightSky(channels: 1);
        CutRing(planes);
        var image = ToImage(planes);
        var absent = image.AbsentPixels().ShouldNotBeNull();
        var to = new float[Size * Size];
        to.AsSpan().Fill(7f);

        ChunkedNafnetRunner.CopyAbsent(image.GetChannelSpan(0), to, Size, absent);

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                to[y * Size + x].ShouldBe(InRing(y, x) ? 0f : 7f);
            }
        }
    }

    [Fact]
    public void AnExclusionOfTheWrongShapeIsRefused()
    {
        var image = ToImage(BrightSky());

        Should.Throw<ArgumentException>(() => image.MinAndShiftedMedian(0, new BitMatrix(Size, Size - 1)));
    }
}
