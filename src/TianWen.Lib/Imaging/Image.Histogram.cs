using System;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using TianWen.Lib.Stat;

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

        float? rescaledMaxValue;
        Image image;
        if (BitDepth is BitDepth.Float32 && MaxValue <= 1.0f)
        {
            rescaledMaxValue = ushort.MaxValue;
            image = ScaleFloatValues(rescaledMaxValue.Value);
        }
        else
        {
            rescaledMaxValue = null;
            image = this;
        }

        var threshold = (uint)Math.Round(image.MaxValue * (0.01d * thresholdPct), MidpointRounding.ToPositiveInfinity) + 1;
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
        var total_value = 0f;
        var pedestralAdjustValue = removePedestral ? image.MinValue : 0f;

        for (var h = 0; h <= height - 1; h++)
        {
            for (var w = 0; w <= width - 1; w++)
            {
                var value = image[channel, h, w];
                if (!float.IsNaN(value))
                {
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

        var hist_mean = 1.0f / count * total_value;

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

    private (float Pedestral, float Median, float MAD) GetPedestralMedianAndMADScaledToUnit(int channel)
    {
        var stats = Statistics(channel, removePedestral: true);
        if (stats.Median is not { } median || stats.MAD is not { } mad)
        {
            throw new InvalidOperationException("Median and MAD should have been calculated");
        }

        var maxValueFactor = 1f / (stats.RescaledMaxValue ?? MaxValue);

        return (blackLevel * maxValueFactor, median * maxValueFactor, mad * maxValueFactor);
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
        var max_range = histogram.Mean;
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
                    var value = data[channel, fitsY, fitsX];
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
            var values = VectorMath.Divide([background, starLevel, sd, histogram.Threshold], rescaledMaxValue);

            return ((float)values[0], (float)values[1], (float)values[2], (float)values[3]);
        }
        else
        {
            return (background, starLevel, sd, histogram.Threshold);
        }
    }
}
