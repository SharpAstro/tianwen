using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The histogram and star-masked-median paths were made faster three ways -- selection instead of a
/// full sort, a flat span instead of <c>float[,]</c> indexing, and a float-domain clamp instead of
/// <see cref="Math.Clamp(double, double, double)"/>. Every one was chosen because it is
/// BIT-IDENTICAL, so this file reimplements the PRE-CHANGE behaviour as the oracle and demands exact
/// equality on real fixtures.
/// </summary>
/// <remarks>
/// <para>An oracle rather than a golden value on purpose: a checked-in expected number cannot say
/// whether a later divergence came from the optimisation or from the fixture, and it silently
/// re-baselines if someone regenerates it. Reimplementing the old algorithm keeps the claim
/// falsifiable.</para>
/// <para>These feed the stretch, so a last-bit difference is not cosmetic: median and MAD set
/// shadows and rescale for every rendered pixel, and <c>Histogram.Mean</c> drives
/// <c>Background()</c>'s mode search, which sets the star-detection threshold.</para>
/// </remarks>
[Collection("Imaging")]
public class HistogramSelectionParityTests
{
    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16")]
    [InlineData("RGGB_frame_bx0_by0_top_down")]
    [InlineData("PlateSolveTestFile")]
    public async Task TheHistogramMatchesTheOldTwoDimensionalDoubleClampLoopExactly(string fixture)
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(fixture, cancellationToken: ct);

        for (var c = 0; c < image.ChannelCount; c++)
        {
            // Both the pedestal-subtracting and plain forms: removePedestral changes the value that
            // reaches the clamp, and negative values are exactly where a float-domain clamp could
            // diverge from the double one.
            foreach (var removePedestral in new[] { false, true })
            {
                var actual = image.Statistics(c, removePedestral: removePedestral);
                var expected = ReferenceHistogram(image, c, thresholdPct: 100, ignoreBlack: false,
                    removePedestral: removePedestral);

                actual.Total.ShouldBe(expected.Total);
                actual.Mean.ShouldBe(expected.Mean);
                actual.Median!.Value.ShouldBe(expected.Median);
                actual.MAD!.Value.ShouldBe(expected.Mad);
                actual.Histogram.Length.ShouldBe(expected.Bins.Length);
                for (var b = 0; b < expected.Bins.Length; b++)
                {
                    if (actual.Histogram[b] != expected.Bins[b])
                    {
                        Assert.Fail($"{fixture} ch{c} removePedestral={removePedestral}: bin {b} " +
                            $"was {actual.Histogram[b]}, reference {expected.Bins[b]}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("image_file-snr-20_stars-28_1280x960x16")]
    [InlineData("RGGB_frame_bx0_by0_top_down")]
    public async Task TheStarMaskedMedianMatchesTheOldSortBasedPathExactly(string fixture)
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await SharedTestData.ExtractGZippedFitsImageAsync(fixture, cancellationToken: ct);

        var stars = await image.FindStarsAsync(image.ReferenceStarChannel, snrMin: 10f,
            cancellationToken: ct);
        // ShouldNotBeNull returns the narrowed value, so no null-forgiving operator is needed.
        var mask = stars.StarMask.ShouldNotBeNull(
            $"{fixture} produced no star mask, so this test would assert nothing");

        for (var c = 0; c < image.ChannelCount; c++)
        {
            var actual = image.GetStarMaskedMedianAndMADScaledToUnit(c, mask);
            var expected = ReferenceStarMaskedMedianAndMad(image, c, mask);

            actual.Pedestral.ShouldBe(expected.Pedestral);
            actual.Median.ShouldBe(expected.Median);
            actual.MAD.ShouldBe(expected.Mad);
        }
    }

    /// <summary>
    /// The histogram loop as it stood before the change: <c>float[,]</c> element access and
    /// <c>Math.Clamp</c> resolving to its <c>double</c> overload.
    /// </summary>
    private static (ImmutableArray<uint> Bins, float Mean, long Total, float Median, float Mad) ReferenceHistogram(
        Image image, int channel, byte thresholdPct, bool ignoreBlack, bool removePedestral)
    {
        var (_, width, height) = image.Shape;

        // Mirrors Image.Histogram's unit-scaled-float branch selection.
        var unitScaled = image.MaxValue <= 1f
            && (image.BitDepth is BitDepth.Float32 || image.SamplesAreUnitReferred);
        var scaleFactor = unitScaled ? (float)ushort.MaxValue : 1f;
        var effectiveMaxValue = unitScaled ? ushort.MaxValue : image.MaxValue;

        var threshold = (uint)Math.Round(effectiveMaxValue * (0.01d * thresholdPct),
            MidpointRounding.ToPositiveInfinity) + 1;
        var bins = new uint[threshold];
        var histTotal = 0u;
        var count = 1;
        var totalValue = 0.0;
        var pedestralAdjustValue = removePedestral ? image.MinValue * scaleFactor : 0f;

        for (var h = 0; h <= height - 1; h++)
        {
            for (var w = 0; w <= width - 1; w++)
            {
                var rawValue = image[channel, h, w];
                if (!float.IsNaN(rawValue))
                {
                    var value = rawValue * scaleFactor;
                    var valueMinusPedestral = value - pedestralAdjustValue;
                    if ((!ignoreBlack || valueMinusPedestral >= 1) && valueMinusPedestral < threshold)
                    {
                        var valueAsInt = (int)Math.Clamp(MathF.Round(valueMinusPedestral), 0, threshold - 1);
                        bins[valueAsInt]++;
                        histTotal++;
                        totalValue += valueMinusPedestral;
                        count++;
                    }
                }
            }
        }

        // Median and MAD walk the bins, which the change did not touch -- recomputed here only so the
        // comparison covers the whole returned record rather than part of it.
        var medianLength = histTotal / 2.0;
        uint occurances = 0;
        int median1 = 0, median2 = 0;
        for (var i = 0; i < threshold; i++)
        {
            occurances += bins[i];
            if (occurances > medianLength) { median1 = i; median2 = i; break; }
            if (occurances == medianLength)
            {
                median1 = i;
                for (var j = i + 1; j < threshold; j++)
                {
                    if (bins[j] > 0) { median2 = j; break; }
                }
                break;
            }
        }
        var median = median1 * 0.5f + median2 * 0.5f;

        occurances = 0;
        var idxDown = median1;
        var idxUp = median2;
        var mad = 0f;
        while (true)
        {
            var currCount = idxDown >= 0 && idxDown != idxUp
                ? bins[idxDown] + bins[idxUp]
                : bins[idxUp];
            var prevOccurances = occurances;
            occurances += currCount;
            if (occurances > medianLength)
            {
                var k = (double)idxUp - median;
                var frac = currCount > 0 ? (medianLength - prevOccurances) / currCount : 0.5;
                mad = (float)(k == 0 ? frac * 0.5 : Math.Max(0, k - 0.5 + frac));
                break;
            }
            idxUp++;
            idxDown--;
            if (idxUp >= threshold) { break; }
        }

        return (ImmutableCollectionsMarshalShim(bins), (float)(totalValue / count), (long)histTotal, median, mad);
    }

    private static ImmutableArray<uint> ImmutableCollectionsMarshalShim(uint[] bins)
        => System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(bins);

    /// <summary>
    /// The star-masked path as it stood before the change: collect, then two full
    /// <see cref="Array.Sort(Array)"/> calls over separate buffers.
    /// </summary>
    private static (float Pedestral, float Median, float Mad) ReferenceStarMaskedMedianAndMad(
        Image image, int channel, BitMatrix starMask, int pixelStride = 4)
    {
        var (_, width, height) = image.Shape;
        var unitDivisor = image.MaxValue <= 1f ? 1f : image.MaxValue;
        var pedestal = image.MinValue / unitDivisor;
        var maxSamples = ((width / pixelStride) + 1) * ((height / pixelStride) + 1);
        var samples = new float[maxSamples];
        var count = 0;

        for (var y = 0; y < height; y += pixelStride)
        {
            for (var x = 0; x < width; x += pixelStride)
            {
                var v = image[channel, y, x];
                if (float.IsNaN(v)) continue;
                if (starMask[y, x]) continue;
                if (v <= image.MinValue) continue;
                samples[count++] = v;
            }
        }

        if (count < 100)
        {
            var fallback = image.GetPedestralMedianAndMADScaledToUnit(channel);
            return (fallback.Pedestral, fallback.Median, fallback.MAD);
        }

        Array.Sort(samples, 0, count);
        var median = samples[count / 2];

        var madSamples = new float[count];
        for (var i = 0; i < count; i++)
        {
            madSamples[i] = MathF.Abs(samples[i] - median);
        }
        Array.Sort(madSamples);
        var mad = madSamples[count / 2];

        var invMax = 1f / unitDivisor;
        var unitMedian = (median - image.MinValue) * invMax;
        var unitMad = mad * invMax;
        const float MinUnitMad = 0.5f / 65535f;
        if (unitMad < MinUnitMad) { unitMad = MinUnitMad; }

        return (pedestal, unitMedian, unitMad);
    }
}
