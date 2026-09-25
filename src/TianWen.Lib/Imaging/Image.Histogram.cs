using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using TianWen.Lib.Geometry;
using static TianWen.Lib.Stat.StatisticsHelper;
namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Generates a historgram of the image with values from 0 to 90% of the maximum value.
    /// This is used to find the background level and star level.
    /// Values above 90% of the maximum value are ignored as they are likely to be saturated stars or artifacts.
    /// NaN values are also ignored.
    /// The histogram is returned as an array of uint where the index represents the pixel value and the value at that index represents the number of pixels with that value.
    /// Additionally, the mean pixel value  and total number of pixels in the histogram are also returned.
    /// </summary>
    /// <param name="channel">Channel index for which to calculate the histogram</param>
    /// <param name="ignoreBlack">Whether to ignore black pixels (value 0) in the histogram. This is useful for images with black borders or vignetting. Default is true.</param>
    /// <param name="thresholdPct">The percentage of the maximum pixel value to use as the upper limit for the histogram. Default is 91%.</param>
    /// <param name="calcStats">If true calculate further statistics like median and MAD</param>
    /// <param name="cfa">A colour of a Bayer mosaic to take the histogram over, walking only that
    /// colour's photosites (<paramref name="channel"/> is then the mosaic's single plane). Null is the
    /// whole plane. See <see cref="CfaChannel"/>.</param>
    /// <returns>historgram values</returns>
    public ImageHistogram Histogram(int channel, byte thresholdPct = 91, bool ignoreBlack = true, bool calcStats = false, bool removePedestral = false, int pixelStride = 1, CfaChannel? cfa = null)
    {
        var (rescaledMaxValue, scaleFactor, threshold) = HistogramScale(channel, thresholdPct);

        // A plain array, wrapped without copying at the return. The builder this replaced cost
        // TWICE the memory for the same bins: its own backing array, then another one because
        // ToImmutableArray() on a Builder copies. Measured at 0.50 MB per call against 0.25 MB,
        // and a document open made 10-12 of these calls (before GetStats took two at a time). The zero-fill loop is gone too -- it was
        // 64 AddRange calls per histogram to write zeros that `new uint[]` already guarantees.
        var histogram = new uint[threshold];
        var (hist_mean, hist_total, median, mad) = FillHistogram(
            channel, ignoreBlack, calcStats, removePedestral, pixelStride, cfa, scaleFactor, threshold, histogram);

        // AsImmutableArray wraps the array rather than copying it. Safe because `histogram` is a
        // local that does not escape this method by any other route, so no caller can hold a
        // mutable alias to the bins.
        return new ImageHistogram(channel, ImmutableCollectionsMarshal.AsImmutableArray(histogram), hist_mean,
            hist_total, threshold, thresholdPct, rescaledMaxValue, median, mad, ignoreBlack);
    }

    // Checks the arguments and decides the bins: the scale a sample is binned at (a unit-scaled float image
    // is binned in [0, 65535], inline) and how many bins the threshold keeps. Shared by Histogram and the
    // rented median-and-MAD path, so the two cannot bin differently.
    private (float? RescaledMaxValue, float ScaleFactor, uint Threshold) HistogramScale(int channel, byte thresholdPct)
    {
        var channelCount = ChannelCount;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }
        if (thresholdPct > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(thresholdPct), thresholdPct, "Threshold percentage must be between 0 and 100");
        }

        // For normalized float images, histogram bins are mapped to [0, 65535] range inline
        // without allocating a full rescaled copy of the image.
        float? rescaledMaxValue;
        float scaleFactor;
        float effectiveMaxValue;
        if (IsUnitScaledFloat)
        {
            rescaledMaxValue = ushort.MaxValue;
            scaleFactor = ushort.MaxValue;
            effectiveMaxValue = ushort.MaxValue;
        }
        else
        {
            rescaledMaxValue = null;
            scaleFactor = 1f;
            effectiveMaxValue = MaxValue;
        }

        var threshold = (uint)Math.Round(effectiveMaxValue * (0.01d * thresholdPct), MidpointRounding.ToPositiveInfinity) + 1;
        return (rescaledMaxValue, scaleFactor, threshold);
    }

    // A histogram with its mean, its count and, with calcStats, its median and MAD: Traverse over `histogram`
    // (threshold bins, zeroed by the caller), then MedianAndMad over what it filled. Histogram runs it over a
    // new array it then returns.
    private (float Mean, long Total, float? Median, float? Mad) FillHistogram(
        int channel, bool ignoreBlack, bool calcStats, bool removePedestral, int pixelStride, CfaChannel? cfa,
        float scaleFactor, uint threshold, Span<uint> histogram)
    {
        var pedestralAdjustValue = removePedestral ? MinValue * scaleFactor : 0f;
        var (total_value, hist_total, _) = Traverse(channel, ignoreBlack, pixelStride, cfa, scaleFactor, threshold,
            histogram, pedestralAdjustValue, sumFirst: true, secondHistogram: default, secondPedestal: 0f);

        // The count behind the mean started at 1, to prevent a divide by zero, and grew with every binned sample.
        var hist_mean = (float)(total_value / (hist_total + 1));
        if (!calcStats)
        {
            return (hist_mean, hist_total, null, float.NaN);
        }

        var (median, mad) = MedianAndMad(histogram, hist_total, threshold);
        return (hist_mean, hist_total, median, mad);
    }

    /// <summary>
    /// THE traversal every histogram here is built by. Bins each sample of <paramref name="channel"/>, walked
    /// per <paramref name="cfa"/> and <paramref name="pixelStride"/>, into <paramref name="histogram"/> after
    /// subtracting <paramref name="pedestal"/>; and, when <paramref name="secondHistogram"/> is not empty,
    /// into that one too after subtracting <paramref name="secondPedestal"/>, in the same pass.
    /// </summary>
    /// <remarks>
    /// <para><b>Two histograms from one walk</b> is what a document open wants (<see cref="GetStats"/>): the
    /// display histogram at the frame's own levels and the stretch statistics' histogram with the pedestal
    /// removed. They cannot be one histogram, since they bin different numbers; they can be one READ of the
    /// samples, which was two before.</para>
    /// <para><b>Only the first histogram has a running sum, and only when <paramref name="sumFirst"/> asks.</b>
    /// The sum is the one ordered, floating-point part of the walk: every other result is an integer count,
    /// which no order changes. It is also the walk's longest dependency chain, so a histogram whose mean
    /// nobody reads (the stretch statistics keep only the median and the MAD) leaves it out.</para>
    /// <para><b>Four samples at a time, bit for bit.</b> Where the walk is contiguous (step 1) or takes every
    /// other photosite (step 2, a CFA colour at full resolution: two loads, the even lanes kept), a
    /// <see cref="Vector128{T}"/> does the scale, the pedestal, the range test, the rounding and the clamp for
    /// four samples at once. Each of those is an IEEE operation per lane, so each lane computes exactly what the
    /// scalar walk computes, and the sum takes the lanes in walk order. The increments stay scalar: a histogram
    /// is a scatter. Any other step, and step 2 on a machine with no native even-lane shuffle, walks scalar.</para>
    /// <para><b>Internal, not private, so a test can see the sum.</b> What leaves through the public methods is
    /// a FLOAT mean, and a double sum taken in another order differs from this one in its last bits, which
    /// the conversion to float almost always hides. HistogramKernelParityTests compares the double itself.</para>
    /// </remarks>
    internal (double Sum, long Total, long SecondTotal) Traverse(
        int channel, bool ignoreBlack, int pixelStride, CfaChannel? cfa, float scaleFactor, uint threshold,
        Span<uint> histogram, float pedestal, bool sumFirst, Span<uint> secondHistogram, float secondPedestal)
    {
        var (_, width, height) = Shape;
        // No first histogram is the walk that wants only the ordered sum (TraverseInBands, when it cannot
        // prove its band sums exact): the samples are tested and summed in walk order and binned nowhere.
        var binFirst = !histogram.IsEmpty;
        var dual = !secondHistogram.IsEmpty;
        var hist_total = 0L;
        var secondTotal = 0L;
        // Accumulate as double, not float: a 61 MP IMX455 frame with sky ~12 ADU
        // gives a true sum of ~732 M, but float32's 24-bit mantissa quantises
        // increments below 16 once the accumulator passes ~256 M, so successive
        // += 12 rounds to 0 and total_value saturates at ~268 M. The resulting
        // mean was 4.39 instead of 12, which dragged Background()'s mode search
        // range to bins 1-4 (all empty), forced the fallback-to-mean path, and
        // pushed every sky pixel above the FindStars detection threshold ->
        // 12 M AnalyseStar candidates per pass on the polar-align IMX455
        // bench. Double accumulator has 53-bit mantissa -- ULP at 1 G is 1e-7,
        // so single-ADU increments stay exact for any sane image size.
        var total_value = 0.0;
        var channelData = Planes[channel].Data;

        // pixelStride > 1 subsamples on a fixed grid (every Nth row, every Nth
        // column). Bins remain dense enough for median/MAD on a 9576x6388
        // frame even at stride=8 (~950k samples), and the histogram cost
        // drops 64x. Used by the live mini viewer where the stretch only
        // needs aggregate statistics, not exact per-pixel histograms.
        var stride = Math.Max(1, pixelStride);
        var maxBinIndex = (int)threshold - 1;
        var maxBinF = (float)maxBinIndex;

        // A sample is binned when low <= value < threshold. Ignoring black puts the floor at 1 (the black
        // overlap areas of a stack); otherwise the floor is -infinity, which every number passes. NaN passes
        // neither comparison, so the vector lanes need no test of their own for it.
        var low = ignoreBlack ? 1f : float.NegativeInfinity;

        // Walk a FLAT span, not channelData[h, w]. A float[,] element access recomputes the row
        // offset and bounds-checks both dimensions per pixel, and the JIT can hoist neither out
        // of this loop. Row-major slicing keeps the traversal ORDER byte-identical, which matters
        // because total_value is an ordered floating-point accumulation.
        //
        // Measured per 24 MP channel, RELEASE (Debug is ~7x slower and inverts the ranking of
        // everything here, so quote the configuration with any number):
        //   50 ms  as it was
        //   36 ms  with this flat span
        //   32 ms  with the float-domain clamp below
        //    8 ms  with parallel row bands -- not taken HERE: it reorders the total_value
        //          summation that feeds Background()'s mode search. TraverseInBands takes it
        //          for the document open (#490), and takes the sum from the bands only when it
        //          proves no order could change it.
        //   18 ms  with four samples at a time (the Vector128 walk below), bit for bit; the
        //          probe's copy of the scalar loop read 29 ms in the same run (2026-09-25,
        //          win-arm64, #631).
        // Reproduce: TIANWEN_HISTOGRAM_PROBE=1 dotnet test -c Release
        //            --filter HistogramCostDecompositionProbe
        // A CFA colour is the mosaic walked at that colour's photosites -- one start per phase, red
        // and blue one each, green both -- accumulated into the SAME bins. Nothing is materialised:
        // the statistic wants a traversal, not the pixels, and SplitBayerChannels would copy a
        // quarter-frame per colour to be read back once. All three colours together visit every
        // photosite exactly once, the cost of one plain scan. See CfaPhaseStarts / CfaStep.
        Span<(int Row, int Col)> phaseStarts = stackalloc (int Row, int Col)[2];
        var phaseCount = CfaPhaseStarts(cfa, phaseStarts);
        var step = CfaStep(cfa, stride);

        var vectorised = Vector128.IsHardwareAccelerated
            && (step == 1 || (step == 2 && (AdvSimd.Arm64.IsSupported || Sse.IsSupported)));
        var columnsPerVector = 4 * step;
        var vScale = Vector128.Create(scaleFactor);
        var vPedestal = Vector128.Create(pedestal);
        var vSecondPedestal = Vector128.Create(secondPedestal);
        var vLow = Vector128.Create(low);
        var vThreshold = Vector128.Create((float)threshold);
        var vMaxBin = Vector128.Create(maxBinF);

        var flat = MemoryMarshal.CreateReadOnlySpan(ref channelData[0, 0], channelData.Length);
        for (var phase = 0; phase < phaseCount; phase++)
        {
            var (rowStart, colStart) = phaseStarts[phase];
            for (var h = rowStart; h <= height - 1; h += step)
            {
                var row = flat.Slice(h * width, width);
                var w = colStart;
                if (vectorised)
                {
                    ref var rowOrigin = ref MemoryMarshal.GetReference(row);
                    for (; w + columnsPerVector <= width; w += columnsPerVector)
                    {
                        var raw = step == 1
                            ? Vector128.LoadUnsafe(ref rowOrigin, (nuint)w)
                            : EvenLanes(Vector128.LoadUnsafe(ref rowOrigin, (nuint)w), Vector128.LoadUnsafe(ref rowOrigin, (nuint)(w + 4)));
                        var value = raw * vScale;

                        var first = value - vPedestal;
                        var firstIn = (Vector128.GreaterThanOrEqual(first, vLow) & Vector128.LessThan(first, vThreshold)).ExtractMostSignificantBits();
                        if (firstIn != 0)
                        {
                            if (binFirst)
                            {
                                BinLanes(histogram, first, firstIn, vMaxBin);
                            }
                            hist_total += BitOperations.PopCount(firstIn);
                            if (sumFirst)
                            {
                                // Lane order IS walk order, so the ordered sum sees the samples in exactly
                                // the sequence the scalar walk would.
                                if (firstIn == 0b1111)
                                {
                                    total_value += first.GetElement(0);
                                    total_value += first.GetElement(1);
                                    total_value += first.GetElement(2);
                                    total_value += first.GetElement(3);
                                }
                                else
                                {
                                    for (var lane = 0; lane < 4; lane++)
                                    {
                                        if ((firstIn & (1u << lane)) != 0)
                                        {
                                            total_value += first.GetElement(lane);
                                        }
                                    }
                                }
                            }
                        }

                        if (dual)
                        {
                            var second = value - vSecondPedestal;
                            var secondIn = (Vector128.GreaterThanOrEqual(second, vLow) & Vector128.LessThan(second, vThreshold)).ExtractMostSignificantBits();
                            if (secondIn != 0)
                            {
                                BinLanes(secondHistogram, second, secondIn, vMaxBin);
                                secondTotal += BitOperations.PopCount(secondIn);
                            }
                        }
                    }
                }

                // The scalar walk: the whole row when nothing is vectorised, else what is left of it.
                for (; w <= width - 1; w += step)
                {
                    var rawValue = row[w];
                    if (!float.IsNaN(rawValue))
                    {
                        var value = rawValue * scaleFactor;
                        var valueMinusPedestral = value - pedestal;

                        // ignore black overlap areas and bright stars (if threshold percentage is below 100%)
                        if (valueMinusPedestral >= low && valueMinusPedestral < threshold)
                        {
                            if (binFirst)
                            {
                                histogram[Bin(valueMinusPedestral, maxBinIndex, maxBinF)]++; // calculate histogram
                            }
                            hist_total++;
                            if (sumFirst)
                            {
                                total_value += valueMinusPedestral;
                            }
                        }

                        if (dual)
                        {
                            var secondValue = value - secondPedestal;
                            if (secondValue >= low && secondValue < threshold)
                            {
                                secondHistogram[Bin(secondValue, maxBinIndex, maxBinF)]++;
                                secondTotal++;
                            }
                        }
                    }
                }
            }
        }

        return (total_value, hist_total, secondTotal);
    }

    // A band has to hold this many samples for another band to pay for its bins and its thread.
    private const int DefaultMinSamplesPerBand = 1 << 20;

    // Above every biased float exponent (0 to 255): no nonzero finite addend has been seen.
    private const int NoExponent = 256;

    /// <summary>
    /// <see cref="Traverse"/> over parallel bands of rows, with the same results bit for bit. The bins and the
    /// counts are integers, which no order changes. The running sum is taken from the bands only when no order
    /// could have changed it; otherwise it is taken again in walk order.
    /// </summary>
    /// <remarks>
    /// <para><b>When no order can change a floating-point sum.</b>
    /// <list type="number">
    /// <item>Every addend is a float, so an integer multiple of its own ulp, and so of g, the smallest ulp among
    /// them, a power of two.</item>
    /// <item>Every partial sum, in any order and any grouping, is then an integer multiple of g, no larger in
    /// magnitude than the sum of the magnitudes.</item>
    /// <item>If that sum is below 2^53 g, every partial sum is exactly representable as a double. No addition
    /// rounds, and every order gives the same double, the walk's included.</item>
    /// </list>
    /// The bands track the two numbers this needs, the smallest exponent and the sum of magnitudes. When the
    /// bound holds, the band sums ARE the ordered sum. When it does not (a tiny value beside a large total, an
    /// infinity), <see cref="Traverse"/> takes the sum again, in walk order and with no bins, which costs back
    /// what the bands saved and nothing more.</para>
    /// <para><b>#490 declined parallel bands</b> because a reordered sum is only almost certainly the same. This
    /// one is certainly the same, or it is not reordered.</para>
    /// <para><b>Used where a frame's statistics are taken on their own:</b> the document open
    /// (<see cref="GetStats"/>, the luminance statistic). It is not used where the caller already runs frames
    /// in parallel, as star detection inside a bake does.</para>
    /// </remarks>
    /// <param name="minSamplesPerBand">How many samples a band must hold for a second band to pay; a test sets it
    /// low to band a small image.</param>
    internal (double Sum, long Total, long SecondTotal, bool SumFromBands) TraverseInBands(
        int channel, bool ignoreBlack, int pixelStride, CfaChannel? cfa, float scaleFactor, uint threshold,
        Span<uint> histogram, float pedestal, bool sumFirst, Span<uint> secondHistogram, float secondPedestal,
        int minSamplesPerBand = DefaultMinSamplesPerBand)
    {
        var (_, width, height) = Shape;
        var stride = Math.Max(1, pixelStride);
        Span<(int Row, int Col)> phaseStarts = stackalloc (int Row, int Col)[2];
        var phaseCount = CfaPhaseStarts(cfa, phaseStarts);
        var step = CfaStep(cfa, stride);
        var walked = (long)phaseCount * ((height + step - 1) / step) * ((width + step - 1) / step);
        var bandCount = (int)Math.Clamp(walked / Math.Max(1, minSamplesPerBand), 1, Math.Min(Environment.ProcessorCount, height));
        if (bandCount < 2)
        {
            var (orderedSum, orderedTotal, orderedSecondTotal) = Traverse(channel, ignoreBlack, pixelStride, cfa, scaleFactor, threshold,
                histogram, pedestal, sumFirst, secondHistogram, secondPedestal);
            return (orderedSum, orderedTotal, orderedSecondTotal, SumFromBands: false);
        }

        var dual = !secondHistogram.IsEmpty;
        var bins = (int)threshold;
        var starts = phaseStarts[..phaseCount].ToArray();
        var channelData = Planes[channel].Data;
        var low = ignoreBlack ? 1f : float.NegativeInfinity;
        var tallies = new BandTally[bandCount];
        var firstBins = new uint[bandCount][];
        var secondBins = new uint[bandCount][];
        ParallelFor.Run(bandCount, band =>
        {
            var first = ArrayPool<uint>.Shared.Rent(bins);
            Array.Clear(first, 0, bins);
            firstBins[band] = first;
            uint[] second = [];
            if (dual)
            {
                second = ArrayPool<uint>.Shared.Rent(bins);
                Array.Clear(second, 0, bins);
            }
            secondBins[band] = second;

            var rowFrom = (int)((long)height * band / bandCount);
            var rowTo = (int)((long)height * (band + 1) / bandCount);
            tallies[band] = WalkBand(channelData, width, rowFrom, rowTo, starts, step, scaleFactor, threshold, low,
                first.AsSpan(0, bins), pedestal, sumFirst, dual ? second.AsSpan(0, bins) : default, secondPedestal);
        });

        long total = 0, secondTotal = 0;
        var bandSum = 0.0;
        var magnitude = 0.0;
        var minExponent = NoExponent;
        var nonFinite = false;
        for (var band = 0; band < bandCount; band++)
        {
            AddBins(histogram, firstBins[band].AsSpan(0, bins));
            ArrayPool<uint>.Shared.Return(firstBins[band]);
            if (dual)
            {
                AddBins(secondHistogram, secondBins[band].AsSpan(0, bins));
                ArrayPool<uint>.Shared.Return(secondBins[band]);
            }

            var tally = tallies[band];
            total += tally.Total;
            secondTotal += tally.SecondTotal;
            bandSum += tally.Sum;
            magnitude += tally.Magnitude;
            minExponent = Math.Min(minExponent, tally.MinExponent);
            nonFinite |= tally.NonFinite;
        }

        if (!sumFirst)
        {
            return (0.0, total, secondTotal, SumFromBands: false);
        }

        // The bound, with the sum of magnitudes read generously: it was itself summed in doubles, so it can sit
        // a relative 2^-22 or so below the true one, and 2^-20 more covers that. A biased exponent e makes an
        // ulp of 2^(e - 150), so 2^53 g is 2^(e - 97). No nonzero addend at all means a sum of +0 either way.
        var exact = !nonFinite
            && (minExponent == NoExponent || magnitude * (1 + 1.0 / (1 << 20)) < Math.ScaleB(1.0, minExponent - 97));
        if (exact)
        {
            return (bandSum, total, secondTotal, SumFromBands: true);
        }

        var (sum, _, _) = Traverse(channel, ignoreBlack, pixelStride, cfa, scaleFactor, threshold,
            Span<uint>.Empty, pedestal, sumFirst: true, Span<uint>.Empty, 0f);
        return (sum, total, secondTotal, SumFromBands: false);
    }

    // What one band of TraverseInBands found besides its bins.
    private readonly record struct BandTally(long Total, long SecondTotal, double Sum, double Magnitude, int MinExponent, bool NonFinite);

    // One band's rows of every phase, binned as Traverse bins them. The sum is taken lane by lane, in no
    // particular order, beside what TraverseInBands needs to know whether that order could matter.
    private static BandTally WalkBand(
        float[,] channelData, int width, int rowFrom, int rowTo, (int Row, int Col)[] starts, int step,
        float scaleFactor, uint threshold, float low,
        Span<uint> histogram, float pedestal, bool sumFirst, Span<uint> secondHistogram, float secondPedestal)
    {
        var dual = !secondHistogram.IsEmpty;
        long total = 0, secondTotal = 0;
        var sum = 0.0;
        var magnitude = 0.0;
        var minExponent = NoExponent;
        var nonFinite = false;
        var maxBinIndex = (int)threshold - 1;
        var maxBinF = (float)maxBinIndex;

        var vectorised = Vector128.IsHardwareAccelerated
            && (step == 1 || (step == 2 && (AdvSimd.Arm64.IsSupported || Sse.IsSupported)));
        var columnsPerVector = 4 * step;
        var vScale = Vector128.Create(scaleFactor);
        var vPedestal = Vector128.Create(pedestal);
        var vSecondPedestal = Vector128.Create(secondPedestal);
        var vLow = Vector128.Create(low);
        var vThreshold = Vector128.Create((float)threshold);
        var vMaxBin = Vector128.Create(maxBinF);
        var vSumLower = Vector128<double>.Zero;
        var vSumUpper = Vector128<double>.Zero;
        var vMagnitudeLower = Vector128<double>.Zero;
        var vMagnitudeUpper = Vector128<double>.Zero;
        var vExponentMask = Vector128.Create(0xFF);
        var vOne = Vector128.Create(1);
        var vNoExponent = Vector128.Create(NoExponent);
        var vMinExponent = vNoExponent;
        var vNonFinite = Vector128<int>.Zero;

        var flat = MemoryMarshal.CreateReadOnlySpan(ref channelData[0, 0], channelData.Length);
        foreach (var (rowStart, colStart) in starts)
        {
            // The phase's rows are rowStart, rowStart + step, ...: the first of them in this band.
            var h = rowStart >= rowFrom ? rowStart : rowStart + (rowFrom - rowStart + step - 1) / step * step;
            for (; h < rowTo; h += step)
            {
                var row = flat.Slice(h * width, width);
                var w = colStart;
                if (vectorised)
                {
                    ref var rowOrigin = ref MemoryMarshal.GetReference(row);
                    for (; w + columnsPerVector <= width; w += columnsPerVector)
                    {
                        var raw = step == 1
                            ? Vector128.LoadUnsafe(ref rowOrigin, (nuint)w)
                            : EvenLanes(Vector128.LoadUnsafe(ref rowOrigin, (nuint)w), Vector128.LoadUnsafe(ref rowOrigin, (nuint)(w + 4)));
                        var value = raw * vScale;

                        var first = value - vPedestal;
                        var inRange = Vector128.GreaterThanOrEqual(first, vLow) & Vector128.LessThan(first, vThreshold);
                        var firstIn = inRange.ExtractMostSignificantBits();
                        if (firstIn != 0)
                        {
                            BinLanes(histogram, first, firstIn, vMaxBin);
                            total += BitOperations.PopCount(firstIn);
                            if (sumFirst)
                            {
                                // A lane left out counts as +0, which moves neither sum nor bound.
                                var counted = Vector128.ConditionalSelect(inRange, first, Vector128<float>.Zero);
                                var lower = Vector128.WidenLower(counted);
                                var upper = Vector128.WidenUpper(counted);
                                vSumLower += lower;
                                vSumUpper += upper;
                                vMagnitudeLower += Vector128.Abs(lower);
                                vMagnitudeUpper += Vector128.Abs(upper);

                                // The biased exponent; a zero (either sign) constrains nothing, a subnormal has
                                // the smallest normal's ulp, and 255 is an infinity (a NaN never gets this far).
                                var exponent = Vector128.ShiftRightLogical(counted.AsInt32(), 23) & vExponentMask;
                                var nonZero = ~Vector128.Equals(counted, Vector128<float>.Zero).AsInt32();
                                vNonFinite |= nonZero & Vector128.Equals(exponent, vExponentMask);
                                vMinExponent = Vector128.Min(vMinExponent,
                                    Vector128.ConditionalSelect(nonZero, Vector128.Max(exponent, vOne), vNoExponent));
                            }
                        }

                        if (dual)
                        {
                            var second = value - vSecondPedestal;
                            var secondIn = (Vector128.GreaterThanOrEqual(second, vLow) & Vector128.LessThan(second, vThreshold)).ExtractMostSignificantBits();
                            if (secondIn != 0)
                            {
                                BinLanes(secondHistogram, second, secondIn, vMaxBin);
                                secondTotal += BitOperations.PopCount(secondIn);
                            }
                        }
                    }
                }

                for (; w <= width - 1; w += step)
                {
                    var rawValue = row[w];
                    if (!float.IsNaN(rawValue))
                    {
                        var value = rawValue * scaleFactor;
                        var first = value - pedestal;
                        if (first >= low && first < threshold)
                        {
                            histogram[Bin(first, maxBinIndex, maxBinF)]++;
                            total++;
                            if (sumFirst)
                            {
                                sum += first;
                                magnitude += Math.Abs((double)first);
                                if (first != 0f)
                                {
                                    var exponent = (BitConverter.SingleToInt32Bits(first) >>> 23) & 0xFF;
                                    if (exponent == 0xFF)
                                    {
                                        nonFinite = true;
                                    }
                                    else
                                    {
                                        minExponent = Math.Min(minExponent, Math.Max(exponent, 1));
                                    }
                                }
                            }
                        }

                        if (dual)
                        {
                            var second = value - secondPedestal;
                            if (second >= low && second < threshold)
                            {
                                secondHistogram[Bin(second, maxBinIndex, maxBinF)]++;
                                secondTotal++;
                            }
                        }
                    }
                }
            }
        }

        sum += vSumLower.GetElement(0) + vSumLower.GetElement(1) + vSumUpper.GetElement(0) + vSumUpper.GetElement(1);
        magnitude += vMagnitudeLower.GetElement(0) + vMagnitudeLower.GetElement(1) + vMagnitudeUpper.GetElement(0) + vMagnitudeUpper.GetElement(1);
        for (var lane = 0; lane < 4; lane++)
        {
            minExponent = Math.Min(minExponent, vMinExponent.GetElement(lane));
            nonFinite |= vNonFinite.GetElement(lane) != 0;
        }

        return new BandTally(total, secondTotal, sum, magnitude, minExponent, nonFinite);
    }

    // The bin a sample lands in.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bin(float value, int maxBinIndex, float maxBinF)
    {
        // Clamp in float, cast once. Math.Clamp(float, int, uint) binds the
        // DOUBLE overload, so the old form ran float -> double -> clamp ->
        // double -> int per pixel. Comparing before the cast also keeps the
        // cast in range, which the double clamp was doing implicitly -- a
        // calibrated frame can carry very negative pixels, and (int) on an
        // out-of-range float is platform-defined.
        var rounded = MathF.Round(value);
        return rounded <= 0f ? 0 : (rounded >= maxBinF ? maxBinIndex : (int)rounded);
    }

    // Bin for four lanes at once, and the same bins: MathF.Round and Vector128.Round both round to even, and
    // clamping the rounded value into [0, maxBin] before the conversion is Bin's two comparisons (a rounded
    // -0 or anything below it lands in bin 0, anything at or past maxBin in maxBin).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> BinIndices(Vector128<float> value, Vector128<float> maxBin)
        => Vector128.ConvertToInt32(Vector128.Min(Vector128.Max(Vector128.Round(value), Vector128<float>.Zero), maxBin));

    // Lanes 0 and 2 of each vector, in order: the samples at w, w + 2, w + 4 and w + 6 of eight contiguous
    // ones, which is a CFA colour's next four photosites along its row. UZP1 on Arm, SHUFPS on x86; Traverse
    // only calls this where one of the two exists.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> EvenLanes(Vector128<float> lower, Vector128<float> upper)
        => AdvSimd.Arm64.IsSupported
            ? AdvSimd.Arm64.UnzipEven(lower, upper)
            : Sse.Shuffle(lower, upper, 0b10_00_10_00);

    // The increments for the lanes `lanes` selects: a histogram is a scatter, so these stay scalar. The one
    // place both walks bin a vector, so Traverse and WalkBand cannot bin differently.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BinLanes(Span<uint> histogram, Vector128<float> values, uint lanes, Vector128<float> maxBin)
    {
        var bin = BinIndices(values, maxBin);
        if (lanes == 0b1111)
        {
            histogram[bin.GetElement(0)]++;
            histogram[bin.GetElement(1)]++;
            histogram[bin.GetElement(2)]++;
            histogram[bin.GetElement(3)]++;
            return;
        }

        for (var lane = 0; lane < 4; lane++)
        {
            if ((lanes & (1u << lane)) != 0)
            {
                histogram[bin.GetElement(lane)]++;
            }
        }
    }

    // A band's bins into the histogram's.
    private static void AddBins(Span<uint> into, ReadOnlySpan<uint> from)
    {
        var i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            ref var target = ref MemoryMarshal.GetReference(into);
            ref var source = ref MemoryMarshal.GetReference(from);
            for (; i + 4 <= into.Length; i += 4)
            {
                (Vector128.LoadUnsafe(ref target, (nuint)i) + Vector128.LoadUnsafe(ref source, (nuint)i)).StoreUnsafe(ref target, (nuint)i);
            }
        }

        for (; i < into.Length; i++)
        {
            into[i] += from[i];
        }
    }

    // The median and the MAD of a filled histogram, in bin units.
    private static (float Median, float? Mad) MedianAndMad(ReadOnlySpan<uint> histogram, long hist_total, uint threshold)
    {
        // Median threshold is half the PIXEL count (hist_total), not half
        // the BIN count (histogram.Count). The old `histogram.Count / 2.0`
        // typically resolved to threshold/2 ~= 32768; on images with more
        // than that many pixels the walker stopped well below the actual
        // median bin, biasing median toward 0. Latent because typical
        // astro frames have a tight background dominating early bins, so
        // both thresholds resolved to the same bin -- but a uniform
        // [0, 1] ramp on a 512^2+ image returns ~0.13 instead of ~0.5
        // under the old behaviour.
        var medianlength = hist_total / 2.0;
        uint occurances = 0;
        int median1 = 0, median2 = 0;

        /* Determine median out of histogram array */
        for (int i = 0; i < threshold; i++)
        {
            var histValue = histogram[i];

            occurances += histValue;
            if (occurances > medianlength)
            {
                median1 = i;
                median2 = i;
                break;
            }
            else if (occurances == medianlength)
            {
                median1 = i;
                // Find the next bin j with non-zero count. Previous code
                // tested `histValue > 0` (the OUTER bin's count) which is
                // always true at this point -- so j=i+1 unconditionally,
                // regardless of whether that bin had any pixels. Fix
                // mirrors the obvious intent of "next non-empty bin".
                for (int j = i + 1; j < threshold; j++)
                {
                    if (histogram[j] > 0)
                    {
                        median2 = j;
                        break;
                    }
                }
                break;
            }
        }
        var median = median1 * 0.5f + median2 * 0.5f;

        /* Determine median Absolute Deviation out of histogram array and previously determined median
         * As the histogram already has the values sorted and we know the median,
         * we can determine the mad by beginning from the median and step up and down
         * By doing so we will gain a sorted list automatically, because MAD = DetermineMedian(|xn - median|)
         * So starting from the median will be 0 (as median - median = 0), going up and down will increment by the steps
         *
         * Sub-bin linear interpolation: without it the MAD is quantised to integer
         * bin distances {0, 1, 2, ...}, which on a 65535-bin histogram floors any
         * observable σ at ~1.5e-5 in unit space (MAD_TO_SD / 65535) and makes
         * drizzle-stacked frames read identical bin-noise values regardless of
         * their true noise level. Interpolating by the fraction of in-bin count
         * needed to cross medianlength recovers float precision below 1 bin.
         */
        occurances = 0;
        var idxDown = median1;
        var idxUp = median2;
        float? mad = null;
        while (true)
        {
            uint currCount;
            if (idxDown >= 0 && idxDown != idxUp)
            {
                currCount = histogram[idxDown] + histogram[idxUp];
            }
            else
            {
                currCount = histogram[idxUp];
            }
            var prevOccurances = occurances;
            occurances += currCount;

            if (occurances > medianlength)
            {
                var k = (double)idxUp - median;
                var frac = currCount > 0
                    ? (medianlength - prevOccurances) / (double)currCount
                    : 0.5;
                // For k > 0: |delta| range of this bin step is [k - 0.5, k + 0.5].
                // For k == 0 (first iter, all pixels in the median bin): |delta|
                // range collapses to [0, 0.5] (only one side of the bin contributes
                // because pixels on the median's own bin have |delta| <= 0.5).
                var madD = k == 0 ? frac * 0.5 : Math.Max(0, k - 0.5 + frac);
                mad = (float)madD;
                break;
            }

            idxUp++;
            idxDown--;
            if (idxUp >= threshold)
            {
                break;
            }
        }

        return (median, mad);
    }

    public ImageHistogram Statistics(int channel, bool removePedestral = false, int pixelStride = 1, CfaChannel? cfa = null)
        => Histogram(channel, thresholdPct: 100, ignoreBlack: false, calcStats: true, removePedestral, pixelStride, cfa);

    public (float Pedestral, float Median, float MAD) GetPedestralMedianAndMADScaledToUnit(int channel, int pixelStride = 1, CfaChannel? cfa = null)
        => PedestralMedianAndMadScaledToUnit(channel, pixelStride, cfa, inBands: false);

    // inBands walks the samples in parallel row bands (TraverseInBands), which is free of any proof here: no
    // running sum is kept, and a count is an integer. The luminance statistic of a document open asks for it.
    private (float Pedestral, float Median, float MAD) PedestralMedianAndMadScaledToUnit(int channel, int pixelStride, CfaChannel? cfa, bool inBands)
    {
        // Statistics(channel, removePedestral: true) over RENTED bins: only the median and the MAD leave, so
        // the histogram is scratch. It used to be a new array per call, 256 KB for a unit-scaled float image,
        // on every live preview frame (StretchSolver.CollectPerChannelStats, per channel) and every document.
        var (rescaledMaxValue, scaleFactor, threshold) = HistogramScale(channel, thresholdPct: 100);
        var rented = ArrayPool<uint>.Shared.Rent((int)threshold);
        try
        {
            var bins = rented.AsSpan(0, (int)threshold);
            bins.Clear();
            // No running sum: the mean it would feed never leaves this method.
            var total = inBands
                ? TraverseInBands(channel, ignoreBlack: false, pixelStride, cfa, scaleFactor, threshold,
                    bins, MinValue * scaleFactor, sumFirst: false, secondHistogram: default, secondPedestal: 0f).Total
                : Traverse(channel, ignoreBlack: false, pixelStride, cfa, scaleFactor, threshold,
                    bins, MinValue * scaleFactor, sumFirst: false, secondHistogram: default, secondPedestal: 0f).Total;
            var (median, mad) = MedianAndMad(bins, total, threshold);
            return StretchStatsScaledToUnit(median, mad, rescaledMaxValue);
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The display histogram and the stretch statistics of one channel from ONE walk of its samples: bit for
    /// bit what <see cref="Statistics"/> and <see cref="GetPedestralMedianAndMADScaledToUnit"/> return, for the
    /// price of one of them.
    /// </summary>
    /// <remarks>
    /// <para>They are two histograms, not one: the stretch statistics are taken with the pedestal REMOVED (the
    /// shader subtracts it before the curve, so the median that positions the curve must be in that same
    /// space), while a display draws the frame's own levels. What they share is the read of the samples, which
    /// a document open used to make twice for every channel, or every colour of a mosaic.</para>
    /// <para><b>The walk runs in parallel row bands</b> (<see cref="TraverseInBands"/>), whose mean is the ordered
    /// one because it is taken from the bands only when no order could change it. This is the document open's
    /// call, one frame at a time; <see cref="Statistics"/> stays a single walk for callers that already run
    /// frames in parallel.</para>
    /// </remarks>
    public (ImageHistogram Histogram, ChannelStretchStats Stretch) GetStats(int channel, int pixelStride = 1, CfaChannel? cfa = null)
    {
        const byte thresholdPct = 100;
        var (rescaledMaxValue, scaleFactor, threshold) = HistogramScale(channel, thresholdPct);
        var histogram = new uint[threshold];
        var rented = ArrayPool<uint>.Shared.Rent((int)threshold);
        try
        {
            var stretchBins = rented.AsSpan(0, (int)threshold);
            stretchBins.Clear();
            var (total_value, hist_total, stretchTotal, _) = TraverseInBands(channel, ignoreBlack: false, pixelStride, cfa, scaleFactor, threshold,
                histogram, pedestal: 0f, sumFirst: true, stretchBins, secondPedestal: MinValue * scaleFactor);

            // Statistics(channel), field for field: the count behind the mean started at 1.
            var (median, mad) = MedianAndMad(histogram, hist_total, threshold);
            var display = new ImageHistogram(channel, ImmutableCollectionsMarshal.AsImmutableArray(histogram),
                (float)(total_value / (hist_total + 1)), hist_total, threshold, thresholdPct, rescaledMaxValue, median, mad, IgnoreBlack: false);

            var (stretchMedian, stretchMad) = MedianAndMad(stretchBins, stretchTotal, threshold);
            var (pedestral, unitMedian, unitMad) = StretchStatsScaledToUnit(stretchMedian, stretchMad, rescaledMaxValue);
            return (display, new ChannelStretchStats(pedestral, unitMedian, unitMad));
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(rented);
        }
    }

    // The stretch statistics' median and MAD, found in bins, moved into the unit-scaled space the shader
    // works in, beside the pedestal of that same space.
    private (float Pedestral, float Median, float MAD) StretchStatsScaledToUnit(float median, float? madOrNull, float? rescaledMaxValue)
    {
        if (madOrNull is not { } mad)
        {
            throw new InvalidOperationException("Median and MAD should have been calculated");
        }

        // The histogram may have been computed on a rescaled copy (float [0,1] → ushort [0,65535]).
        // Median and MAD are in that rescaled space and need dividing by rescaledMaxValue.
        var maxValueFactor = 1f / (rescaledMaxValue ?? MaxValue);

        // The histogram with removePedestral:true subtracted the rescaled image's MinValue.
        // The pedestal must land in the SAME space as the median above. When the histogram was
        // computed on the face-value rescale (Float32 data already in [0,1]: bins = value*65535,
        // median/65535 = raw pixel space; the shader consumes such an image with NormFactor = 1,
        // see StretchSolver), the pedestal is MinValue verbatim. Dividing by MaxValue there was
        // only a no-op while a normalised image's peak was guaranteed to be exactly 1.0 -- with
        // full-scale normalisation (SensorFullScaleAdu) the observed peak can sit below 1.0 and
        // MinValue/MaxValue would inflate the pedestal relative to the median's space. The
        // un-rescaled (ADU, MaxValue > 1) path keeps MinValue/MaxValue, matching median/MaxValue
        // and the shader's NormFactor = 1/MaxValue.
        var pedestral = rescaledMaxValue is not null ? MinValue : MinValue / MaxValue;

        // Guard against MAD=0 (happens when the distribution is narrower than one histogram bin).
        // Use a minimum of half a bin width in the unit-scaled space.
        var scaledMad = mad * maxValueFactor;
        if (scaledMad < maxValueFactor * 0.5f)
        {
            scaledMad = maxValueFactor * 0.5f;
        }

        return (pedestral, median * maxValueFactor, scaledMad);
    }

    /// <summary>
    /// Robust per-channel noise σ estimate, in unit-scaled [0, 1] coordinates.
    /// For each channel: σ = MAD × 1.4826 (the Gaussian consistency factor for
    /// the median absolute deviation -- recovers the true σ of a Normal
    /// distribution from MAD with negligible bias for N > ~1000 samples).
    /// </summary>
    /// <remarks>
    /// <para>MAD is computed around the channel median, so bright outliers
    /// (stars, nebula cores) are statistically rejected -- the result is a
    /// good proxy for "noise σ of the background" without needing explicit
    /// background segmentation. This is the same primitive PixInsight's
    /// <c>MeasureSNR</c> uses, and what SAS Pro's stretch heuristics check.</para>
    ///
    /// <para>Use case in the AI pipeline: log per-stage σ to quantify how
    /// much grain each enhancer (denoise, deconv) actually removes, and to
    /// detect regressions where a stage <i>amplifies</i> noise (which
    /// deconv can do when run without a denoise follow-up).</para>
    ///
    /// <para>Output length equals the channel count -- 1 for mono, 3 for
    /// RGB. Degenerate channels (all-NaN, fully saturated, or zero variance)
    /// yield σ = 0 -- the histogram can't compute MAD on those, so the
    /// primitive probes the histogram's nullable <see cref="ImageHistogram.MAD"/>
    /// directly and falls back to 0 when missing. Callers tracking "this
    /// stage broke" should compare σ to the entry baseline; a sudden drop
    /// to exactly 0 signals degeneracy.</para>
    /// </remarks>
    public ImmutableArray<float> EstimateNoiseProfile()
    {
        var (channels, _, _) = Shape;
        var builder = ImmutableArray.CreateBuilder<float>(channels);
        for (var c = 0; c < channels; c++)
        {
            // Probe the underlying histogram directly -- its MAD field is
            // nullable when the channel is degenerate, so a null check is a
            // proper precondition rather than catching the throw from
            // GetPedestralMedianAndMADScaledToUnit.
            //
            // No half-bin floor here (unlike GetPedestralMedianAndMADScaledToUnit):
            // the histogram MAD now uses sub-bin linear interpolation, so values
            // below 0.5 bins are real measurements, not quantisation noise. The
            // floor would defeat the whole point of noise tracking on already-
            // stacked / drizzled data where true σ < 1e-5 is common.
            var stats = Statistics(c, removePedestral: true);
            if (stats.MAD is { } madRaw && stats.RescaledMaxValue is { } rescaledMax)
            {
                var scaled = (float)(madRaw / rescaledMax);
                builder.Add(scaled * MAD_TO_SD);
            }
            else if (stats.MAD is { } madUnscaled)
            {
                // No rescale -> MAD already in unit space
                var scaled = madUnscaled / MaxValue;
                builder.Add(scaled * MAD_TO_SD);
            }
            else
            {
                builder.Add(0f);
            }
        }
        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Like <see cref="GetPedestralMedianAndMADScaledToUnit"/> but excludes pixels masked by
    /// <paramref name="starMask"/>. Subsamples with <paramref name="pixelStride"/> for speed;
    /// fallback to full-pixel median/MAD when too few samples remain.
    /// </summary>
    /// <remarks>
    /// <b>On a mosaic, no <paramref name="cfa"/> plus an EVEN <paramref name="pixelStride"/> measures
    /// ONE COLOUR, not the frame.</b> Without a <paramref name="cfa"/> the walk is a fixed grid from
    /// (0, 0), and every even step keeps both parities, so it never leaves the phase it started on --
    /// and the default stride here is 4. The result is a plausible number for whichever photosite sits
    /// at the origin, which is why this reads as correct until the colours are compared. Pass a
    /// <paramref name="cfa"/> for a per-colour statistic, or <paramref name="pixelStride"/> 1 (or any
    /// odd value) for one over the whole mosaic.
    /// </remarks>
    public (float Pedestral, float Median, float MAD) GetStarMaskedMedianAndMADScaledToUnit(
        int channel, BitMatrix starMask, int pixelStride = 4, CfaChannel? cfa = null)
    {
        var (channelCount, width, height) = Shape;
        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        // Same space convention as GetPedestralMedianAndMADScaledToUnit and StretchSolver's
        // NormFactor: an already-normalised image (MaxValue <= 1, shader NormFactor = 1) is
        // consumed at face value -- do NOT re-expand by 1/MaxValue, which was only a no-op while
        // a normalised peak was guaranteed to be exactly 1.0 (full-scale normalisation via
        // SensorFullScaleAdu can leave the observed peak below 1.0). ADU images (MaxValue > 1)
        // keep the observed-peak divisor, matching the shader's NormFactor = 1/MaxValue.
        var unitDivisor = HasUnitScalePeak ? 1f : MaxValue;
        var pedestal = MinValue / unitDivisor;
        // The same per-colour walk Histogram takes (CfaPhaseStarts / CfaStep). Note the fixed grid from
        // (0, 0) at an even stride sits on ONE CFA phase of a mosaic: without `cfa`, a mosaic's masked
        // statistic was whichever colour happened to be at the origin.
        Span<(int Row, int Col)> phaseStarts = stackalloc (int Row, int Col)[2];
        var phaseCount = CfaPhaseStarts(cfa, phaseStarts);
        var step = CfaStep(cfa, pixelStride);
        var maxSamples = phaseCount * ((width / step) + 1) * ((height / step) + 1);
        var samples = new float[maxSamples];
        var count = 0;
        var channelData = Planes[channel].Data;

        for (var phase = 0; phase < phaseCount; phase++)
        {
            var (rowStart, colStart) = phaseStarts[phase];
            for (var y = rowStart; y < height; y += step)
            {
                for (var x = colStart; x < width; x += step)
                {
                    var v = channelData[y, x];
                    if (float.IsNaN(v)) continue;
                    if (starMask[y, x]) continue;
                    if (v <= MinValue) continue;
                    samples[count++] = v;
                }
            }
        }

        if (count < 100)
        {
            return GetPedestralMedianAndMADScaledToUnit(channel, cfa: cfa);
        }

        // Two medians, so historically two full Array.Sort of ~1.5 M samples (a 24 MP frame at
        // stride 4) plus a second 6 MB buffer for the deviations. A median needs SELECTION, not a
        // sort, and the deviations can overwrite the same buffer -- see UpperMedianAndMad, which
        // owns that contract for the four places that need it. Bit-identical: the k-th order
        // statistic is a property of the multiset, so selection returns exactly what
        // sort-and-index did. Measured on the 24 MP 3-channel document-open path: 512 -> 67 ms.
        //
        // UPPER median (sorted[count / 2]), not MedianFast's average of the two middle values:
        // this result feeds the stretch, so the convention is held deliberately.
        var (median, mad) = UpperMedianAndMad(samples.AsSpan(0, count));

        var invMax = 1f / unitDivisor;
        // Return median in pedestal-subtracted unit-scaled space, matching
        // GetPedestralMedianAndMADScaledToUnit's convention. The stretch loop computes
        // `norm = raw * normFactor - pedestal` and then `rescaled = (norm - shadows) * rescale`,
        // so shadows (= median + ...) MUST be in pedestal-subtracted space too. Returning the
        // raw median caused shadows to land at the actual sky-pixel value (~0.029), well above
        // the post-pedestal norm (~0.001), which clamped every below-median pixel to 0.
        var unitMedian = (median - MinValue) * invMax;
        var unitMad = mad * invMax;
        // Floor MAD at half a 16-bit bin width in unit-scaled space (~7.6e-6). The previous
        // formula `invMax * 0.5f` collapsed to 0.5 after ScaleFloatValuesToUnitInPlace set
        // MaxValue=1 -> half the dynamic range, which then drove convergence to a degenerate
        // state with shadows at -2.19 and a flat mid-grey output. Using a fixed bin-width
        // floor stays correct regardless of normalisation state.
        const float MinUnitMad = 0.5f / 65535f;
        if (unitMad < MinUnitMad)
        {
            unitMad = MinUnitMad;
        }

        return (pedestal, unitMedian, unitMad);
    }

    /// <summary>
    /// get background and star level from peek histogram
    /// </summary>
    /// <returns>background and star level</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public (float background, float starLevel, float noise_level, float threshold) Background(int channel)
    {
        var (channelCount, width, height) = Shape;

        if (channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Channel index {channel} is out of range for image with {ChannelCount} channels");
        }

        // get histogram of img_loaded and his_total
        var histogram = Histogram(channel);
        var background = float.NaN; // define something for images containing 0 or 65535 only

        // find peak in histogram which should be the average background
        var pixels = 0u;
        var max_range = Math.Min(histogram.Mean, histogram.Histogram.Length - 1);
        uint i;
        // mean value from histogram
        for (i = 1; i <= max_range; i++)
        {
            // find peak, ignore value 0 from oversize
            var histVal = histogram.Histogram[(int)i];
            if (histVal > pixels) // find colour peak
            {
                pixels = histVal;
                background = i;
            }
        }

        // check alternative mean value
        if (float.IsNaN(background) || histogram.Mean > 1.5f * background) // 1.5 * most common
        {
            background = histogram.Mean; // strange peak at low value, ignore histogram and use mean
        }

        i = (uint)MathF.Ceiling(histogram.RescaledMaxValue ?? MaxValue);

        var starLevel = 0.0f;
        var above = 0u;

        while (starLevel == 0 && i > background + 1)
        {
            i--;
            if (i < histogram.Histogram.Length)
            {
                above += histogram.Histogram[(int)i];
            }
            if (above > 0.001f * histogram.Total)
            {
                starLevel = i;
            }
        }

        if (starLevel <= background)
        {
            starLevel = background + 1; // no or very few stars
        }
        else
        {
            // star level above background. Important subtract 1 for saturated images. Otherwise no stars are detected
            starLevel = starLevel - background - 1;
        }

        // calculate noise level
        var stepSize = (int)MathF.Round(height / 71.0f); // get about 71x71 = 5000 samples.So use only a fraction of the pixels

        // prevent problems with even raw OSC images
        if (stepSize % 2 == 0)
        {
            stepSize++;
        }

        var sd = 99999.0f;
        float sd_old;
        var iterations = 0;

        var rescaledFactor = histogram.RescaledMaxValue ?? 1.0f;
        var channelData = Planes[channel].Data;
        // repeat until sd is stable or 7 iterations
        do
        {
            var counter = 1; // never divide by zero

            sd_old = sd;
            var fitsY = 15;
            while (fitsY <= height - 1 - 15)
            {
                var fitsX = 15;
                while (fitsX <= width - 1 - 15)
                {
                    var value = channelData[fitsY, fitsX];
                    // not an outlier, noise should be symmetrical so should be less then twice background
                    if (!float.IsNaN(value))
                    {
                        var denorm = rescaledFactor * value;

                        // ignore outliers after first run
                        if (denorm < background * 2 && denorm != 0 && (iterations == 0 || (denorm - background) <= 3 * sd_old))
                        {
                            var bgSub = denorm - background;
                            sd += bgSub * bgSub;
                            // keep record of number of pixels processed
                            counter++;
                        }
                    }
                    fitsX += stepSize; // skip pixels for speed
                }
                fitsY += stepSize; // skip pixels for speed
            }
            sd = MathF.Sqrt(sd / counter); // standard deviation
            iterations++;
        } while (sd_old - sd >= 0.05f * sd && iterations < 7); // repeat until sd is stable or 7 iterations

        // renormalize
        if (histogram.RescaledMaxValue is { } rescaledMaxValue)
        {
            var invScale = 1f / rescaledMaxValue;
            return (background * invScale, starLevel * invScale, sd * invScale, histogram.Threshold * invScale);
        }
        else
        {
            return (background, starLevel, sd, histogram.Threshold);
        }
    }

    /// <summary>
    /// Scans the image for the darkest spatial region and returns per-channel averages
    /// in pedestal-subtracted space (matching the shader's norm = raw * normFactor - pedestal).
    /// Uses the median of each patch (not the mean) to reject hot pixels.
    /// </summary>
    /// <param name="pedestals">Per-channel pedestal values (from <see cref="GetPedestralMedianAndMADScaledToUnit(int)"/>.</param>
    /// <param name="squareSize">Size of the sampling square in pixels.</param>
    /// <returns>Per-channel background values and luminance background, both pedestal-subtracted.</returns>
    public (float[] PerChannel, float Luma) ScanBackgroundRegion(ReadOnlySpan<float> pedestals, int squareSize = 32, BitMatrix? starMask = null)
    {
        var region = FindBackgroundRegion(squareSize, starMask);
        return MeasureBackgroundRegion(pedestals, squareSize, region.X, region.Y, starMask);
    }

    /// <summary>
    /// WHERE the sky is: the darkest <paramref name="squareSize"/> square inside the frame's inner
    /// 90 percent, found on a coarse grid four squares apart. The first half of
    /// <see cref="ScanBackgroundRegion"/>, exposed on its own so a consumer that only needs the place
    /// (the dataset gallery's 1:1 sky patch) asks the same question the neutralisation does and lands
    /// on the same pixels; a patch chosen by any other rule can sit inside nebulosity the render has
    /// correctly declined to call sky, and then reads as a colour cast that is nobody's fault.
    /// </summary>
    /// <param name="squareSize">Side of the sampling square, in pixels.</param>
    /// <param name="starMask">Pixels to leave out of the luma, when a detection has run.</param>
    /// <returns>The square, in image pixels; the top-left corner of the inner margin when nothing
    /// passed the luma floor.</returns>
    public PixelRect FindBackgroundRegion(int squareSize = 32, BitMatrix? starMask = null)
    {
        var step = squareSize * 4;

        // Skip a 5% border on each side to avoid stacking artifacts (black edges, vignetting)
        var marginX = (int)(Width * 0.05f);
        var marginY = (int)(Height * 0.05f);

        var minLuma = float.MaxValue;
        int bgX = marginX, bgY = marginY;

        // Build the list of row-strip Y values to scan
        var yStart = marginY;
        var yEnd = Height - squareSize - marginY;
        var xStart = marginX;
        var xEnd = Width - squareSize - marginX;

        // One slot per row-strip, written by exactly one iteration, so the parallel scan needs no
        // synchronisation at all and the argmin is a serial pass over ~28 entries afterwards (a
        // 4000 px frame at step 128). This replaced a `lock (new object())` around the reduction,
        // which was both against the project's lock rule and NON-DETERMINISTIC: the guard is a
        // strict `<`, so on an exact luma tie whichever thread arrived first won, and the chosen
        // background patch therefore depended on scheduling. Ties are not exotic (a synthetic or
        // uniform-background frame produces them by the thousand) and the patch feeds background
        // neutralisation, so the gains moved run to run. The serial pass gives the lowest strip
        // index the tie instead. Parallel.For's return is a full barrier, so the reads below are
        // ordered after every write.
        var stripCount = (yEnd - yStart + step - 1) / step;
        var strips = new (float Luma, int X, int Y)[Math.Max(0, stripCount)];

        Parallel.For(0, stripCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, (yIdx) =>
        {
            var y = yStart + yIdx * step;
            var localMinLuma = float.MaxValue;
            int localBgX = xStart, localBgY = y;

            for (var x = xStart; x < xEnd; x += step)
            {
                var luma = AverageRegionLuma(x, y, squareSize, starMask);
                if (luma > 0.001f && luma < localMinLuma)
                {
                    localMinLuma = luma;
                    localBgX = x;
                    localBgY = y;
                }
            }

            // A strip where nothing passed the luma floor keeps float.MaxValue and loses the
            // argmin below, matching the old code's "skip the reduction entirely" branch.
            strips[yIdx] = (localMinLuma, localBgX, localBgY);
        });

        for (var i = 0; i < strips.Length; i++)
        {
            if (strips[i].Luma < minLuma)
            {
                minLuma = strips[i].Luma;
                bgX = strips[i].X;
                bgY = strips[i].Y;
            }
        }

        return new PixelRect(bgX, bgY, squareSize, squareSize);
    }

    /// <summary>
    /// The per-channel levels of the region <see cref="FindBackgroundRegion"/> chose: the second half
    /// of the scan, kept beside the first so a consumer that wants only WHERE the sky is (the dataset
    /// gallery's 1:1 patch) and one that wants its LEVEL (background neutralisation) look in the same
    /// place by construction. The gallery used to pick its patch with a rule of its own and landed
    /// inside nebulosity the render had correctly not called sky.
    /// </summary>
    private (float[] PerChannel, float Luma) MeasureBackgroundRegion(ReadOnlySpan<float> pedestals, int squareSize, int bgX, int bgY, BitMatrix? starMask)
    {
        var channelCount = ChannelCount;

        // Compute per-channel background, pedestal-subtracted.
        // For 1-channel Bayer images, the channel-0 median pools all four Bayer
        // positions together and is useless for background neutralization.
        // Demosaic the dark region into 3 separate medians (R / G+G / B) so
        // downstream consumers (BackgroundNeutralization.ComputeGains, the
        // toolbar dropdown) get the same shape they'd see on a 3-channel image.
        float[] perChannel;
        if (channelCount == 1 && ImageMeta.SensorType is SensorType.RGGB)
        {
            var pedestal = pedestals.Length > 0 ? pedestals[0] : 0f;
            var (r, g, b) = BayerMediansInRegion(
                bgX, bgY, squareSize, pedestal,
                ImageMeta.BayerOffsetX, ImageMeta.BayerOffsetY, starMask);
            perChannel = [r, g, b];
        }
        else
        {
            perChannel = new float[channelCount];
            for (var c = 0; c < channelCount; c++)
            {
                var pedestal = c < pedestals.Length ? pedestals[c] : pedestals[0];
                perChannel[c] = MedianRegion(c, bgX, bgY, squareSize, starMask) - pedestal;
            }
        }

        // Rec.709 luminance: keyed on the bg array length, not channelCount,
        // so the Bayer-3 path produces a proper luminance instead of just R.
        var lumaBg = perChannel.Length >= 3
            ? LumaWeighting.Rec709.ToLuma(perChannel[0], perChannel[1], perChannel[2])
            : perChannel[0];

        return (perChannel, lumaBg);
    }

    /// <summary>
    /// Samples 3 median values (R, G, B) from a single-channel Bayer mosaic in a
    /// square region by classifying each pixel by its Bayer position. The G median
    /// pools both Bayer green positions (~2x sample count vs R and B). All values
    /// are pedestal-subtracted. Hardcoded for RGGB layout; extend the switch
    /// when other Bayer patterns become supported.
    /// </summary>
    private (float R, float G, float B) BayerMediansInRegion(
        int x0, int y0, int size, float pedestal,
        int bayerOffsetX, int bayerOffsetY, BitMatrix? starMask = null)
    {
        // Worst-case capacity per Bayer channel within an n*n region: n*n/2 for G,
        // n*n/4 for R and B. Round up and add a small slop to absorb any off-by-one.
        var quarter = (size * size) / 4 + 4;
        var half = (size * size) / 2 + 4;
        var rBuf = new float[quarter];
        var gBuf = new float[half];
        var bBuf = new float[quarter];
        int ri = 0, gi = 0, bi = 0;

        for (var y = y0; y < y0 + size && y < Height; y++)
        {
            var yp = (y - bayerOffsetY) & 1;
            for (var x = x0; x < x0 + size && x < Width; x++)
            {
                if (starMask is { } sm && sm[y, x]) continue;
                var v = this[0, y, x];
                if (float.IsNaN(v)) continue;
                var xp = (x - bayerOffsetX) & 1;
                // RGGB layout (relative to bayerOffset): R at (0,0), G at (1,0)+(0,1), B at (1,1)
                switch (yp * 2 + xp)
                {
                    case 0: rBuf[ri++] = v; break;
                    case 1: gBuf[gi++] = v; break;
                    case 2: gBuf[gi++] = v; break;
                    case 3: bBuf[bi++] = v; break;
                }
            }
        }

        return (MedianSpan(rBuf.AsSpan(0, ri)) - pedestal,
                MedianSpan(gBuf.AsSpan(0, gi)) - pedestal,
                MedianSpan(bBuf.AsSpan(0, bi)) - pedestal);

        static float MedianSpan(Span<float> s)
        {
            if (s.Length == 0) return 0f;
            s.Sort();
            return s[s.Length / 2];
        }
    }

    /// <summary>
    /// Builds a <see cref="BitMatrix"/> star mask from detected stars, suitable for passing to
    /// <see cref="ScanBackgroundRegion"/> to exclude star pixels from background estimation.
    /// </summary>
    public BitMatrix BuildStarMask(StarList stars)
    {
        var mask = new BitMatrix(Height, Width);
        foreach (var star in stars)
        {
            var scaledHfd = HfdFactor * star.HFD;
            var r = (int)MathF.Round(scaledHfd);
            var xc_offset = (int)MathF.Round(star.XCentroid - scaledHfd);
            var yc_offset = (int)MathF.Round(star.YCentroid - scaledHfd);
            var starMaskEntry = StarMasks[Math.Clamp(r - 1, 0, StarMasks.Length - 1)];
            mask.SetRegionClipped(yc_offset, xc_offset, starMaskEntry);
        }
        return mask;
    }

    /// <summary>
    /// Computes the median pixel value over a square region of a single channel.
    /// Using median instead of mean rejects hot pixels and other outliers.
    /// When a star mask is provided, star pixels are excluded.
    /// </summary>
    private float MedianRegion(int channel, int x0, int y0, int size, BitMatrix? starMask = null)
    {
        var count = 0;
        var maxCount = size * size;
        var buffer = maxCount <= 4096 ? stackalloc float[maxCount] : new float[maxCount];

        for (var y = y0; y < y0 + size && y < Height; y++)
        {
            for (var x = x0; x < x0 + size && x < Width; x++)
            {
                if (starMask is { } sm && sm[y, x])
                {
                    continue;
                }
                var val = this[channel, y, x];
                if (!float.IsNaN(val))
                {
                    buffer[count++] = val;
                }
            }
        }

        if (count == 0) return 0f;

        var span = buffer[..count];
        span.Sort();
        return span[count / 2];
    }

    private float AverageRegionLuma(int x0, int y0, int size, BitMatrix? starMask = null)
    {
        if (ChannelCount < 3)
        {
            return AverageRegionChannel(0, x0, y0, size, starMask);
        }
        var r = AverageRegionChannel(0, x0, y0, size, starMask);
        var g = AverageRegionChannel(1, x0, y0, size, starMask);
        var b = AverageRegionChannel(2, x0, y0, size, starMask);
        return LumaWeighting.Rec709.ToLuma(r, g, b);
    }

    private float AverageRegionChannel(int channel, int x0, int y0, int size, BitMatrix? starMask = null)
    {
        double sum = 0;
        var count = 0;
        for (var y = y0; y < y0 + size && y < Height; y++)
        {
            for (var x = x0; x < x0 + size && x < Width; x++)
            {
                if (starMask is { } sm && sm[y, x])
                {
                    continue;
                }
                sum += this[channel, y, x];
                count++;
            }
        }
        return count > 0 ? (float)(sum / count) : 0f;
    }
}
