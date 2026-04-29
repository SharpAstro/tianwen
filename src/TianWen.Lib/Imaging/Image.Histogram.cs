using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
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
    /// <returns>historgram values</returns>
    public ImageHistogram Histogram(int channel, byte thresholdPct = 91, bool ignoreBlack = true, bool calcStats = false, bool removePedestral = false)
    {
        var (channelCount, width, height) = Shape;

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
        if (BitDepth is BitDepth.Float32 && MaxValue <= 1.0f)
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
        var histogram = ImmutableArray.CreateBuilder<uint>((int)threshold);

        const int size = 1024;
        Span<uint> zeros = stackalloc uint[size];
        zeros.Clear();

        for (var i = 0; i < threshold; i += size)
        {
            if (i + size > threshold)
            {
                histogram.AddRange(zeros[..(int)(threshold - i)]);
            }
            else
            {
                histogram.AddRange(zeros);
            }
        }

        var hist_total = 0u;
        var count = 1; /* prevent divide by zero */
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
        var pedestralAdjustValue = removePedestral ? MinValue * scaleFactor : 0f;
        var channelData = data[channel];

        for (var h = 0; h <= height - 1; h++)
        {
            for (var w = 0; w <= width - 1; w++)
            {
                var rawValue = channelData[h, w];
                if (!float.IsNaN(rawValue))
                {
                    var value = rawValue * scaleFactor;
                    var valueMinusPedestral = value - pedestralAdjustValue;

                    // ignore black overlap areas and bright stars (if threshold percentage is below 100%)
                    if ((!ignoreBlack || valueMinusPedestral >= 1) && valueMinusPedestral < threshold)
                    {
                        var valueAsInt = (int)Math.Clamp(MathF.Round(valueMinusPedestral), 0, threshold - 1);
                        histogram[valueAsInt]++; // calculate histogram
                        hist_total++;
                        total_value += valueMinusPedestral;
                        count++;
                    }
                }
            }
        }

        var hist_mean = (float)(total_value / count);

        float? median, mad;
        if (calcStats)
        {
            var medianlength = histogram.Count / 2.0;
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
                    for (int j = i + 1; j < threshold; j++)
                    {
                        if (histValue > 0)
                        {
                            median2 = j;
                            break;
                        }
                    }
                    break;
                }
            }
            median = median1 * 0.5f + median2 * 0.5f;

            /* Determine median Absolute Deviation out of histogram array and previously determined median
             * As the histogram already has the values sorted and we know the median,
             * we can determine the mad by beginning from the median and step up and down
             * By doing so we will gain a sorted list automatically, because MAD = DetermineMedian(|xn - median|)
             * So starting from the median will be 0 (as median - median = 0), going up and down will increment by the steps
             */
            occurances = 0;
            var idxDown = median1;
            var idxUp = median2;
            mad = null;
            while (true)
            {
                if (idxDown >= 0 && idxDown != idxUp)
                {
                    occurances += histogram[idxDown] + histogram[idxUp];
                }
                else
                {
                    occurances += histogram[idxUp];
                }

                if (occurances > medianlength)
                {
                    mad = MathF.Abs(idxUp - median.Value);
                    break;
                }

                idxUp++;
                idxDown--;
                if (idxUp >= threshold)
                {
                    break;
                }
            }
        }
        else
        {
            median = null;
            mad = float.NaN;
        }

        return new ImageHistogram(channel, histogram.ToImmutableArray(), hist_mean, hist_total, threshold, thresholdPct, rescaledMaxValue, median, mad, ignoreBlack);
    }

    public ImageHistogram Statistics(int channel, bool removePedestral = false)
        => Histogram(channel, thresholdPct: 100, ignoreBlack: false, calcStats: true, removePedestral);

    public (float Pedestral, float Median, float MAD) GetPedestralMedianAndMADScaledToUnit(int channel)
    {
        var stats = Statistics(channel, removePedestral: true);
        if (stats.Median is not { } median || stats.MAD is not { } mad)
        {
            throw new InvalidOperationException("Median and MAD should have been calculated");
        }

        // The histogram may have been computed on a rescaled copy (float [0,1] → ushort [0,65535]).
        // Median and MAD are in that rescaled space and need dividing by rescaledMaxValue.
        var maxValueFactor = 1f / (stats.RescaledMaxValue ?? MaxValue);

        // The histogram with removePedestral:true subtracted the rescaled image's MinValue.
        // The pedestal must be expressed in the [0,1] (unit-scaled) coordinate space that the
        // stretch formula operates in. MinValue / MaxValue always gives us this, regardless of
        // whether the histogram rescaled the data or not.
        var pedestral = MinValue / MaxValue;

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
        var channelData = data[channel];
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
        var step = squareSize * 4;
        var channelCount = ChannelCount;

        // Skip a 5% border on each side to avoid stacking artifacts (black edges, vignetting)
        var marginX = (int)(Width * 0.05f);
        var marginY = (int)(Height * 0.05f);

        var minLuma = float.MaxValue;
        int bgX = marginX, bgY = marginY;
        var lockObj = new object();

        // Build the list of row-strip Y values to scan
        var yStart = marginY;
        var yEnd = Height - squareSize - marginY;
        var xStart = marginX;
        var xEnd = Width - squareSize - marginX;

        Parallel.For(0, (yEnd - yStart + step - 1) / step, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, (yIdx) =>
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

            if (localMinLuma < float.MaxValue)
            {
                lock (lockObj)
                {
                    if (localMinLuma < minLuma)
                    {
                        minLuma = localMinLuma;
                        bgX = localBgX;
                        bgY = localBgY;
                    }
                }
            }
        });

        // Compute per-channel background, pedestal-subtracted
        var perChannel = new float[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var pedestal = c < pedestals.Length ? pedestals[c] : pedestals[0];
            perChannel[c] = MedianRegion(c, bgX, bgY, squareSize, starMask) - pedestal;
        }

        // Rec.709 luminance in pedestal-subtracted space
        var lumaBg = channelCount >= 3
            ? 0.2126f * perChannel[0] + 0.7152f * perChannel[1] + 0.0722f * perChannel[2]
            : perChannel[0];

        return (perChannel, lumaBg);
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
        return 0.2126f * r + 0.7152f * g + 0.0722f * b;
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
