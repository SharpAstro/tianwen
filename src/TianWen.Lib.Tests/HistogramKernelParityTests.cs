using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The histogram kernel (<c>Image.Traverse</c>) against the walk it replaced, and <see cref="Image.GetStats"/>
/// / <see cref="StretchSolver.CollectStats"/> against the calls they fuse (#631). Everything is compared BIT
/// for bit: the kernel takes four samples at a time and a document open now walks its channels in parallel,
/// and neither may move a single bin, count or mean.
/// </summary>
/// <remarks>
/// <para>The images are built to break a kernel: NaNs, negatives (a calibrated frame has them) and negative
/// zero, values past the 91 and 100 percent thresholds, and widths that leave a vector tail on every step.</para>
/// <para><b>The running sum is checked as the DOUBLE it is</b>, through the internal <c>Traverse</c>, not only
/// as the float mean the public methods return. Summed in another order a double differs from the walk's
/// only in its last bits, and only when a rounding tie or a change of exponent lands in the right place, so
/// on ordinary sky the float mean hides the difference completely: a kernel with two of its lanes swapped
/// passed every check here that looked at the mean alone. <see cref="TheRunningSumIsTakenInWalkOrder"/> builds
/// the tie on purpose.</para>
/// </remarks>
[Collection("Imaging")]
public class HistogramKernelParityTests
{
    public static TheoryData<string> Kinds() => ["mono-unit", "mono-adu", "mono-adu-integer", "colour-unit", "mosaic-00", "mosaic-11", "mosaic-10"];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EveryHistogramIsTheOneTheScalarWalkBuilds(string kind)
    {
        var image = Build(kind);
        var checkedCount = 0;
        var fromBands = new List<bool>();
        foreach (var (channel, cfa) in Walks(image))
        {
            foreach (var stride in new[] { 1, 2, 3 })
            {
                foreach (var pct in new byte[] { 91, 100 })
                {
                    foreach (var ignoreBlack in new[] { false, true })
                    {
                        foreach (var removePedestal in new[] { false, true })
                        {
                            var at = $"{kind} channel {channel} cfa {cfa?.ToString() ?? "none"} stride {stride} pct {pct} ignoreBlack {ignoreBlack} removePedestal {removePedestal}";
                            var h = image.Histogram(channel, pct, ignoreBlack, calcStats: true, removePedestal, stride, cfa);
                            var scale = h.RescaledMaxValue ?? 1f;
                            var pedestal = removePedestal ? image.MinValue * scale : 0f;
                            var (bins, sum, total) = ScalarWalk(image, channel, cfa, stride, scale, (uint)h.Threshold, pedestal, ignoreBlack);

                            h.Histogram.AsSpan().SequenceEqual(bins).ShouldBeTrue($"bins differ: {at}");
                            h.Total.ShouldBe(total, at);
                            Bits(h.Mean).ShouldBe(Bits((float)(sum / (total + 1))), $"mean differs: {at}");

                            // The kernel itself: the double sum, and a second histogram in the same walk (the
                            // stretch statistics' pedestal), each against a walk of its own.
                            var threshold = (uint)h.Threshold;
                            var secondPedestal = image.MinValue * scale;
                            var (secondBins, _, secondTotal) = ScalarWalk(image, channel, cfa, stride, scale, threshold, secondPedestal, ignoreBlack);
                            var first = new uint[threshold];
                            var second = new uint[threshold];
                            var (kernelSum, kernelTotal, kernelSecondTotal) = image.Traverse(channel, ignoreBlack, stride, cfa, scale, threshold,
                                first, pedestal, sumFirst: true, second, secondPedestal);
                            BitConverter.DoubleToInt64Bits(kernelSum).ShouldBe(BitConverter.DoubleToInt64Bits(sum), $"sum differs: {at}");
                            kernelTotal.ShouldBe(total, at);
                            first.AsSpan().SequenceEqual(bins).ShouldBeTrue($"first bins differ: {at}");
                            kernelSecondTotal.ShouldBe(secondTotal, at);
                            second.AsSpan().SequenceEqual(secondBins).ShouldBeTrue($"second bins differ: {at}");

                            // The same in parallel bands, forced small so an image this size bands: the bins,
                            // the counts and the sum, whichever way the sum was got.
                            var bandFirst = new uint[threshold];
                            var bandSecond = new uint[threshold];
                            var banded = image.TraverseInBands(channel, ignoreBlack, stride, cfa, scale, threshold,
                                bandFirst, pedestal, sumFirst: true, bandSecond, secondPedestal, minSamplesPerBand: 997);
                            BitConverter.DoubleToInt64Bits(banded.Sum).ShouldBe(BitConverter.DoubleToInt64Bits(sum),
                                $"banded sum differs (taken {(banded.SumFromBands ? "from the bands" : "in walk order")}): {at}");
                            banded.Total.ShouldBe(total, at);
                            bandFirst.AsSpan().SequenceEqual(bins).ShouldBeTrue($"banded first bins differ: {at}");
                            banded.SecondTotal.ShouldBe(secondTotal, at);
                            bandSecond.AsSpan().SequenceEqual(secondBins).ShouldBeTrue($"banded second bins differ: {at}");
                            fromBands.Add(banded.SumFromBands);
                            checkedCount++;
                        }
                    }
                }
            }
        }
        checkedCount.ShouldBeGreaterThan(0);

        // Both ways of getting the sum are exercised: whole numbers always pass the bound, and tiny values
        // beside a large total (every kind but the integer one carries them) fail it while they are counted.
        if (Environment.ProcessorCount > 1)
        {
            if (kind == "mono-adu-integer")
            {
                fromBands.ShouldContain(true, "whole numbers far below 2^53 cannot round");
            }
            else if (kind == "mono-unit")
            {
                fromBands.ShouldContain(false, "a tiny value beside a large total must send the sum back to walk order");
            }
        }
    }

    [Fact]
    public void TheRunningSumIsTakenInWalkOrder()
    {
        // 65536 samples of 2^15 bring the sum to exactly 2^31, where a double's last bit is worth 2^-21. The
        // next vector starts with 2^-22 and 2^-21. In walk order the first is exactly half a last bit, a tie,
        // which rounds to even and is lost; the second then lands exactly. The other way round the second
        // lands first, the sum's last bit is odd, and the tie rounds UP.
        const int width = 256;
        var plane = new float[257, width];
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < width; x++) { plane[y, x] = 32768f; }
        }
        var tie = MathF.ScaleB(1f, -22);
        var lastBit = MathF.ScaleB(1f, -21);
        plane[256, 0] = tie;
        plane[256, 1] = lastBit;

        var inOrder = Math.ScaleB(1.0, 31) + tie + lastBit;
        var swapped = Math.ScaleB(1.0, 31) + lastBit + tie;
        inOrder.ShouldNotBe(swapped, "the premise: these two orders must give different doubles");

        var meta = new ImageMeta("order", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);
        var image = new Image([plane], BitDepth.Int16, 65535f, 0f, 0f, meta);
        const uint threshold = 65536;
        var bins = new uint[threshold];
        var (sum, _, _) = image.Traverse(0, ignoreBlack: false, pixelStride: 1, cfa: null, scaleFactor: 1f, threshold,
            bins, pedestal: 0f, sumFirst: true, secondHistogram: default, secondPedestal: 0f);

        BitConverter.DoubleToInt64Bits(sum).ShouldBe(BitConverter.DoubleToInt64Bits(inOrder));

        // In bands this sum cannot be proven order-free (the tie is a rounding), so it is taken in walk order.
        var bandBins = new uint[threshold];
        var banded = image.TraverseInBands(0, ignoreBlack: false, pixelStride: 1, cfa: null, scaleFactor: 1f, threshold,
            bandBins, pedestal: 0f, sumFirst: true, secondHistogram: default, secondPedestal: 0f, minSamplesPerBand: 997);
        if (Environment.ProcessorCount > 1)
        {
            banded.SumFromBands.ShouldBeFalse("2^-22 beside 2^31 is exactly the case the bound refuses");
        }
        BitConverter.DoubleToInt64Bits(banded.Sum).ShouldBe(BitConverter.DoubleToInt64Bits(inOrder));
        bandBins.AsSpan().SequenceEqual(bins).ShouldBeTrue();
    }

    /// <summary>
    /// A frame whose statistics cannot be taken fails with the reason the one-walk-at-a-time code gave, not with
    /// the wrapper the parallel walk would put round it. The viewer shows the message as the reason a file did
    /// not open; wrapped, it read "One or more errors occurred", once per channel. The real case: a drizzle
    /// weight sidecar whose samples all lie past the histogram (10P/Tempel, <c>_autocrop.rejection.fits</c>).
    /// </summary>
    [Fact]
    public async Task AFrameWithNoStatisticsFailsWithTheReasonNotAWrapper()
    {
        const int width = 64;
        const int height = 48;
        var planes = new float[3][,];
        for (var c = 0; c < 3; c++)
        {
            var plane = planes[c] = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++) { plane[y, x] = 33.3f; }
            }
        }
        var meta = new ImageMeta("weights", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Color, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);

        // Labelled [0, 1] while every sample is 33.3: nothing lands in a bin, so there is no MAD.
        var expected = Should.Throw<InvalidOperationException>(
            () => new Image(planes, BitDepth.Float32, 1f, 0f, 0f, meta).GetPedestralMedianAndMADScaledToUnit(0));

        var collect = Should.Throw<InvalidOperationException>(
            () => StretchSolver.CollectStats(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, meta)));
        collect.Message.ShouldBe(expected.Message);

        var open = await Should.ThrowAsync<InvalidOperationException>(
            () => AstroImageDocument.AdoptImageAsync(new Image(planes, BitDepth.Float32, 1f, 0f, 0f, meta), cancellationToken: TestContext.Current.CancellationToken));
        open.Message.ShouldBe(expected.Message);
    }

    [Fact]
    public void GetStatsBandsAFullSizeFrameAndStillMatchesTheTwoCalls()
    {
        // 2048 x 1100: 2.25 M samples, so GetStats walks two bands or more at its own default size, and whole
        // numbers, so the bound holds and the mean comes from the bands.
        const int width = 2048;
        const int height = 1100;
        var plane = new float[height, width];
        var rng = new Random(31);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                plane[y, x] = rng.Next(900, 4096);
            }
        }
        var meta = new ImageMeta("bands", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, SensorType.Monochrome, 0, 0,
            RowOrder.TopDown, float.NaN, float.NaN);
        var image = new Image([plane], BitDepth.Int16, 4095f, 900f, 0f, meta);

        var (histogram, stretch) = image.GetStats(0);

        ShouldBeTheSame(histogram, image.Statistics(0), "full-size frame");
        var (pedestal, median, mad) = image.GetPedestralMedianAndMADScaledToUnit(0);
        Bits(stretch.Pedestal).ShouldBe(Bits(pedestal));
        Bits(stretch.Median).ShouldBe(Bits(median));
        Bits(stretch.Mad).ShouldBe(Bits(mad));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void GetStatsIsStatisticsAndTheStretchStatsFromOneWalk(string kind)
    {
        var image = Build(kind);
        foreach (var (channel, cfa) in Walks(image))
        {
            foreach (var stride in new[] { 1, 2, 3 })
            {
                var at = $"{kind} channel {channel} cfa {cfa?.ToString() ?? "none"} stride {stride}";
                var (histogram, stretch) = image.GetStats(channel, stride, cfa);

                ShouldBeTheSame(histogram, image.Statistics(channel, removePedestral: false, stride, cfa), at);

                var (pedestal, median, mad) = image.GetPedestralMedianAndMADScaledToUnit(channel, stride, cfa);
                Bits(stretch.Pedestal).ShouldBe(Bits(pedestal), at);
                Bits(stretch.Median).ShouldBe(Bits(median), at);
                Bits(stretch.Mad).ShouldBe(Bits(mad), at);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void CollectStatsIsTheTwoCollectorsEntryForEntry(string kind)
    {
        var image = Build(kind);
        foreach (var stride in new[] { 1, 3 })
        {
            var (stretch, histograms) = StretchSolver.CollectStats(image, stride);
            var expectedStretch = StretchSolver.CollectPerChannelStats(image, image.ChannelCount, stride);
            var expectedHistograms = StretchSolver.CollectChannelHistograms(image, stride);

            stretch.Length.ShouldBe(expectedStretch.Length);
            histograms.Length.ShouldBe(expectedHistograms.Length);
            for (var i = 0; i < stretch.Length; i++)
            {
                var at = $"{kind} entry {i} stride {stride}";
                Bits(stretch[i].Pedestal).ShouldBe(Bits(expectedStretch[i].Pedestal), at);
                Bits(stretch[i].Median).ShouldBe(Bits(expectedStretch[i].Median), at);
                Bits(stretch[i].Mad).ShouldBe(Bits(expectedStretch[i].Mad), at);
                ShouldBeTheSame(histograms[i], expectedHistograms[i], at);
            }
        }
    }

    [Theory]
    [InlineData("mono-unit")]
    [InlineData("mono-adu")]
    [InlineData("colour-unit")]
    [InlineData("mosaic-00")]
    [InlineData("mosaic-11")]
    public async Task ADocumentTakesTheStatisticsTheSeparateCallsWould(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await AstroImageDocument.AdoptImageAsync(Build(kind), cancellationToken: ct);
        var image = document.UnstretchedImage;

        var expectedHistograms = StretchSolver.CollectChannelHistograms(image);
        document.ChannelStatistics.Length.ShouldBe(expectedHistograms.Length);
        for (var i = 0; i < expectedHistograms.Length; i++)
        {
            ShouldBeTheSame(document.ChannelStatistics[i], expectedHistograms[i], $"{kind} histogram {i}");
        }

        var expectedStretch = StretchSolver.CollectPerChannelStats(image, image.ChannelCount);
        document.PerChannelStats.Length.ShouldBe(expectedStretch.Length);
        for (var i = 0; i < expectedStretch.Length; i++)
        {
            Bits(document.PerChannelStats[i].Pedestal).ShouldBe(Bits(expectedStretch[i].Pedestal), $"{kind} stretch {i}");
            Bits(document.PerChannelStats[i].Median).ShouldBe(Bits(expectedStretch[i].Median), $"{kind} stretch {i}");
            Bits(document.PerChannelStats[i].Mad).ShouldBe(Bits(expectedStretch[i].Mad), $"{kind} stretch {i}");
        }

        if (image.IsCfaMosaic)
        {
            // The whole mosaic's one walk feeds both of these.
            ShouldBeTheSame(document.MosaicHistogram.ShouldNotBeNull(), image.Statistics(0), $"{kind} mosaic histogram");
            document.LumaStats.HasValue.ShouldBeTrue();
            var luma = document.LumaStats.Value;
            var (pedestal, median, mad) = image.GetPedestralMedianAndMADScaledToUnit(0);
            Bits(luma.Pedestal).ShouldBe(Bits(pedestal));
            Bits(luma.Median).ShouldBe(Bits(median));
            Bits(luma.Mad).ShouldBe(Bits(mad));
        }
        else
        {
            document.MosaicHistogram.ShouldBeNull();
        }
    }

    // The walk as it was before the vector kernel, written out plainly: every sample in walk order (phase,
    // then row, then column), binned by the same three comparisons and summed in that order.
    private static (uint[] Bins, double Sum, long Total) ScalarWalk(
        Image image, int channel, CfaChannel? cfa, int pixelStride, float scale, uint threshold, float pedestal, bool ignoreBlack)
    {
        var (_, width, height) = image.Shape;
        var data = image.GetChannelSpan(channel);
        var bins = new uint[threshold];
        var sum = 0.0;
        var total = 0L;
        var maxBinIndex = (int)threshold - 1;
        var maxBinF = (float)maxBinIndex;

        Span<(int Row, int Col)> starts = stackalloc (int Row, int Col)[2];
        var phases = image.CfaPhaseStarts(cfa, starts);
        var step = Image.CfaStep(cfa, Math.Max(1, pixelStride));
        for (var phase = 0; phase < phases; phase++)
        {
            var (rowStart, colStart) = starts[phase];
            for (var h = rowStart; h <= height - 1; h += step)
            {
                for (var w = colStart; w <= width - 1; w += step)
                {
                    var raw = data[h * width + w];
                    if (float.IsNaN(raw))
                    {
                        continue;
                    }
                    var value = raw * scale;
                    var valueMinusPedestal = value - pedestal;
                    if ((!ignoreBlack || valueMinusPedestal >= 1) && valueMinusPedestal < threshold)
                    {
                        var rounded = MathF.Round(valueMinusPedestal);
                        var bin = rounded <= 0f ? 0 : (rounded >= maxBinF ? maxBinIndex : (int)rounded);
                        bins[bin]++;
                        total++;
                        sum += valueMinusPedestal;
                    }
                }
            }
        }

        return (bins, sum, total);
    }

    private static void ShouldBeTheSame(ImageHistogram actual, ImageHistogram expected, string at)
    {
        actual.Channel.ShouldBe(expected.Channel, at);
        actual.Histogram.AsSpan().SequenceEqual(expected.Histogram.AsSpan()).ShouldBeTrue($"bins differ: {at}");
        Bits(actual.Mean).ShouldBe(Bits(expected.Mean), $"mean differs: {at}");
        actual.Total.ShouldBe(expected.Total, at);
        Bits(actual.Threshold).ShouldBe(Bits(expected.Threshold), at);
        actual.ThresholdPct.ShouldBe(expected.ThresholdPct, at);
        actual.RescaledMaxValue.ShouldBe(expected.RescaledMaxValue, at);
        actual.Median.ShouldBe(expected.Median, at);
        (actual.MAD is { } a ? Bits(a) : (int?)null).ShouldBe(expected.MAD is { } e ? Bits(e) : null, $"MAD differs: {at}");
        actual.IgnoreBlack.ShouldBe(expected.IgnoreBlack, at);
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    // Every channel of a plain image; the whole plane and each colour of a mosaic.
    private static IEnumerable<(int Channel, CfaChannel? Cfa)> Walks(Image image)
    {
        if (image.IsCfaMosaic)
        {
            yield return (0, null);
            yield return (0, CfaChannel.Red);
            yield return (0, CfaChannel.Green);
            yield return (0, CfaChannel.Blue);
            yield break;
        }
        for (var c = 0; c < image.ChannelCount; c++)
        {
            yield return (c, null);
        }
    }

    private static Image Build(string kind)
    {
        // Odd sizes: a step-1 row leaves a vector tail, and the two step-2 phases of a mosaic leave different
        // ones (129 samples from column 0, 128 from column 1).
        const int width = 257;
        const int height = 131;
        var unit = !kind.StartsWith("mono-adu", StringComparison.Ordinal);
        var channels = kind == "colour-unit" ? 3 : 1;
        var (sensor, bayerX, bayerY) = kind switch
        {
            "mosaic-00" => (SensorType.RGGB, 0, 0),
            "mosaic-11" => (SensorType.RGGB, 1, 1),
            "mosaic-10" => (SensorType.RGGB, 1, 0),
            "colour-unit" => (SensorType.Color, 0, 0),
            _ => (SensorType.Monochrome, 0, 0),
        };

        var full = unit ? 1f : 4095f;
        // A seed that is the same in every process (string.GetHashCode is randomised per process).
        var seed = 17;
        foreach (var ch in kind) { seed = unchecked(seed * 31 + ch); }
        var rng = new Random(seed);
        var planes = new float[channels][,];
        var min = float.MaxValue;
        for (var c = 0; c < channels; c++)
        {
            var plane = planes[c] = new float[height, width];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var roll = rng.NextDouble();
                    float v;
                    if (roll < 0.01) { v = float.NaN; }
                    else if (roll < 0.03) { v = full * (float)(rng.NextDouble() * 1e-9); }        // tiny: the ordered sum turns inexact
                    else if (roll < 0.04) { v = -full * (float)(rng.NextDouble() * 0.002); }      // calibrated below zero
                    else if (roll < 0.045) { v = -0f; }
                    else if (roll < 0.10) { v = full * (float)rng.NextDouble(); }                // anywhere, both thresholds included
                    else { v = full * (0.02f + 0.004f * (float)rng.NextDouble()); }              // the sky
                    if (kind == "mono-adu-integer" && !float.IsNaN(v))
                    {
                        v = MathF.Round(v);
                    }
                    plane[y, x] = v;
                    if (!float.IsNaN(v) && v < min) { min = v; }
                }
            }
            plane[height / 2, width / 2] = full;
        }

        var meta = new ImageMeta("parity", DateTimeOffset.UnixEpoch, TimeSpan.Zero, FrameType.Light, "",
            0f, 0f, -1, -1, Filter.None, 1, 1, float.NaN, sensor, bayerX, bayerY,
            RowOrder.TopDown, float.NaN, float.NaN);
        return new Image(planes, unit ? BitDepth.Float32 : BitDepth.Int16, full, min, 0f, meta);
    }
}
