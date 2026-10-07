using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Imaging.Planetary;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The grade's whole-frame passes (#1310 part 3): the range and the row peaks are read in vector lanes, and the mean, the spread and the
/// histogram in one scalar loop, and each must answer as the scalar walk it replaced, to the bit, since every frame of every stack and
/// every frame a live capture sends is graded through them. Widths that are no multiple of the lanes, signed zeros and NaN are the
/// cases a vector pass gets wrong; the 4,800 frames of eight captures the change was measured on agreed field for field.
/// </summary>
public class GradePassesTests
{
    private static float[] Frame(Random random, int width, int height)
    {
        var luma = new float[width * height];
        for (var i = 0; i < luma.Length; i++)
        {
            luma[i] = (float)(random.NextDouble() * 1.5 - 0.25);
        }
        // Signed zeros and a repeated extreme, where a lane's choice between equal values could differ from a scalar comparison's.
        luma[0] = -0f;
        luma[luma.Length / 2] = 0f;
        luma[^1] = luma.Max();
        return luma;
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 5)]
    [InlineData(37, 11)]
    [InlineData(300, 300)]
    [InlineData(801, 3)]
    public void TheVectorPassReadsTheRangeAndEveryRowsPeakAsAScalarWalkDoes(int width, int height)
    {
        var luma = Frame(new Random(width * 31 + height), width, height);
        var peaks = new float[height];

        PlanetaryDisk.RangeAndRowPeaks(luma, width, height, peaks, out var min, out var max).ShouldBeTrue();

        var (_, _, scalarMin, scalarMax, _) = PlanetaryDisk.Moments(luma);
        min.ShouldBe(scalarMin);
        max.ShouldBe(scalarMax);
        for (var y = 0; y < height; y++)
        {
            peaks[y].ShouldBe(luma.Skip(y * width).Take(width).Max(), $"row {y}");
        }
    }

    [Theory]
    [InlineData(0)]      // in the first lane
    [InlineData(300)]    // in a full vector of a later row
    [InlineData(800)]    // in a row's tail, past the last full vector
    public void AFrameHoldingANaNIsLeftToTheScalarWalk(int at)
    {
        const int width = 801;
        var luma = Frame(new Random(at), width, 3);
        luma[at] = float.NaN;

        PlanetaryDisk.RangeAndRowPeaks(luma, width, 3, new float[3], out _, out _).ShouldBeFalse(
            "a lane's minimum or maximum does not skip a NaN as the scalar comparisons do");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(37, 11)]
    [InlineData(800, 600)]
    public void TheFusedSumsAreMomentsSumsBitForBitAndTheHistogramIsTallysOwn(int width, int height)
    {
        var luma = Frame(new Random(width + height), width, height);
        var (mean, deviation, min, max, _) = PlanetaryDisk.Moments(luma);
        var scale = (PlanetaryDisk.HistogramBins - 1) / (double)(max - min);
        var fused = new int[4 * PlanetaryDisk.HistogramBins];
        var walked = new int[4 * PlanetaryDisk.HistogramBins];

        var (fusedMean, fusedDeviation) = PlanetaryDisk.SumsAndTally(luma, min, scale, fused);
        for (var y = 0; y < height; y++)
        {
            PlanetaryDisk.Tally(luma.AsSpan(y * width, width), min, scale, walked);
        }

        BitConverter.DoubleToInt64Bits(fusedMean).ShouldBe(BitConverter.DoubleToInt64Bits(mean));
        BitConverter.DoubleToInt64Bits(fusedDeviation).ShouldBe(BitConverter.DoubleToInt64Bits(deviation));
        for (var bin = 0; bin < PlanetaryDisk.HistogramBins; bin++)
        {
            // The four interleaved tallies are summed before any bin is read, so only their sum must agree.
            var a = fused[bin] + fused[PlanetaryDisk.HistogramBins + bin] + fused[(2 * PlanetaryDisk.HistogramBins) + bin] + fused[(3 * PlanetaryDisk.HistogramBins) + bin];
            var b = walked[bin] + walked[PlanetaryDisk.HistogramBins + bin] + walked[(2 * PlanetaryDisk.HistogramBins) + bin] + walked[(3 * PlanetaryDisk.HistogramBins) + bin];
            a.ShouldBe(b, $"bin {bin}");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(33)]
    [InlineData(800)]
    public void TheVectorCountAtFullScaleIsTheScalarCount(int length)
    {
        var random = new Random(length);
        var row = Enumerable.Range(0, length).Select(_ => random.Next(4) switch { 0 => 1f, 1 => 0.999f, 2 => float.NaN, _ => (float)random.NextDouble() }).ToArray();

        FrameGrader.CountAtLeast(row, 0.999f).ShouldBe(row.Count(v => v >= 0.999f));
    }
}
