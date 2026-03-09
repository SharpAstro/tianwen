using System;
using System.Threading;
using System.Threading.Tasks;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    public async Task<Image> StretchLinkedAsync(double stretchFactor = 0.2d, double shadowsClipping = -3d, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken);
            return await debayered.StretchLinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else if (imageMeta.SensorType is SensorType.Monochrome)
        {
            return await StretchUnlinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else
        {
            var (channelCount, width, height) = Shape;

            var (pedestral, median, mad) = GetPedestralMedianAndMADScaledToUnit(0);

            var stretchedData = CreateChannelData(channelCount, height, width);
            for (var c = 0; c < channelCount; c++)
            {
                await StretchChannelAsync(stretchedData, c, stretchFactor, shadowsClipping, pedestral, median, mad, cancellationToken);
            }
            // stretched images are always normalized to unit, so max value is 1.0f
            var stretchedImage = new Image(stretchedData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
            // rescale if required
            return MaxValue > stretchedImage.MaxValue ? stretchedImage.ScaleFloatValues(MaxValue) : stretchedImage;
        }
    }

    public async Task<Image> StretchUnlinkedAsync(double stretchFactor = 0.2d, double shadowsClipping = -3d, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken);
            return await debayered.StretchUnlinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }

        var (channelCount, width, height) = Shape;

        var stretchedData = CreateChannelData(channelCount, height, width);

        for (var c = 0; c < channelCount; c++)
        {
            var (pedestral, median, mad) = GetPedestralMedianAndMADScaledToUnit(c);
            await StretchChannelAsync(stretchedData, c, stretchFactor, shadowsClipping, pedestral, median, mad, cancellationToken);
        }

        // stretched images are always normalized to unit, so max value is 1.0f
        var stretchedImage = new Image(stretchedData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);

        // rescale if required
        return MaxValue > stretchedImage.MaxValue ? stretchedImage.ScaleFloatValues(MaxValue) : stretchedImage;
    }

    /// <summary>
    /// Luma-only stretch: computes luminance Y from RGB, stretches Y → Y', then scales
    /// all channels by Y'/Y to preserve chrominance ratios.
    /// Falls back to unlinked stretch for mono images.
    /// </summary>
    public async Task<Image> StretchLumaAsync(double stretchFactor = 0.2d, double shadowsClipping = -3d, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken);
            return await debayered.StretchLumaAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else if (imageMeta.SensorType is SensorType.Monochrome || Shape.ChannelCount < 3)
        {
            return await StretchUnlinkedAsync(stretchFactor, shadowsClipping, DebayerAlgorithm.None, cancellationToken);
        }
        else
        {
            var (channelCount, width, height) = Shape;
            var stretchedData = CreateChannelData(channelCount, height, width);
            await StretchLumaCoreAsync(stretchedData, stretchFactor, shadowsClipping, cancellationToken);
            var stretchedImage = new Image(stretchedData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
            return MaxValue > stretchedImage.MaxValue ? stretchedImage.ScaleFloatValues(MaxValue) : stretchedImage;
        }
    }

    /// <summary>
    /// Luma-only stretch into a pre-allocated destination buffer, reusing memory.
    /// </summary>
    internal async Task<Image> StretchLumaIntoAsync(float[][,] destination, double stretchFactor = 0.2d, double shadowsClipping = -3d, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.Monochrome || Shape.ChannelCount < 3)
        {
            return await StretchUnlinkedIntoAsync(destination, stretchFactor, shadowsClipping, cancellationToken);
        }

        await StretchLumaCoreAsync(destination, stretchFactor, shadowsClipping, cancellationToken);
        return new Image(destination, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Computes luminance stretch statistics (pedestal, median, MAD) from a color image.
    /// Builds a Rec. 709 luminance channel and computes histogram statistics on it.
    /// Falls back to channel 0 stats for mono images. Optionally debayers Bayer images first.
    /// </summary>
    public async Task<(float Pedestal, float Median, float MAD)> GetLumaStretchStatsAsync(DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.RGGB)
        {
            var debayered = await DebayerAsync(debayerAlgorithm, cancellationToken);
            return await debayered.GetLumaStretchStatsAsync(DebayerAlgorithm.None, cancellationToken);
        }

        if (ChannelCount < 3)
        {
            return GetPedestralMedianAndMADScaledToUnit(0);
        }

        return await Task.Run(() =>
        {
            var (_, width, height) = Shape;
            var needsNorm = MaxValue > 1.0f + float.Epsilon;
            var normFactor = 1.0f / MaxValue;

            var lumaData = new float[1][,];
            lumaData[0] = new float[height, width];
            var dst = lumaData[0];

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var r = data[0][y, x];
                    var g = data[1][y, x];
                    var b = data[2][y, x];
                    if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
                    {
                        dst[y, x] = float.NaN;
                    }
                    else
                    {
                        if (needsNorm) { r *= normFactor; g *= normFactor; b *= normFactor; }
                        dst[y, x] = LumaR * r + LumaG * g + LumaB * b;
                    }
                }
            }

            var lumaImage = new Image(lumaData, BitDepth.Float32, 1.0f, 0f, 0f,
                imageMeta with { SensorType = SensorType.Monochrome });
            return lumaImage.GetPedestralMedianAndMADScaledToUnit(0);
        }, cancellationToken);
    }

    // Rec. 709 luminance weights
    private const float LumaR = 0.2126f;
    private const float LumaG = 0.7152f;
    private const float LumaB = 0.0722f;

    private async Task StretchLumaCoreAsync(float[][,] destination, double stretchFactor, double shadowsClipping, CancellationToken cancellationToken)
    {
        var (channelCount, width, height) = Shape;

        // Build luminance channel
        var lumaData = new float[height, width];
        var srcR = data[0];
        var srcG = data[1];
        var srcB = data[2];
        var needsNorm = MaxValue > 1.0f + float.Epsilon;
        var normFactor = 1.0f / MaxValue;

        await Parallel.ForAsync(0, height, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, async (y, ct) => await Task.Run(() =>
        {
            for (var x = 0; x < width; x++)
            {
                var r = srcR[y, x];
                var g = srcG[y, x];
                var b = srcB[y, x];
                if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b))
                {
                    lumaData[y, x] = float.NaN;
                }
                else
                {
                    if (needsNorm) { r *= normFactor; g *= normFactor; b *= normFactor; }
                    lumaData[y, x] = LumaR * r + LumaG * g + LumaB * b;
                }
            }
            return ValueTask.CompletedTask;
        }, ct));

        // Compute stats on the luminance channel using a temporary single-channel image
        var lumaChannelData = new float[1][,];
        lumaChannelData[0] = lumaData;
        var lumaImage = new Image(lumaChannelData, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta with { SensorType = SensorType.Monochrome });

        var (_, median, mad) = lumaImage.GetPedestralMedianAndMADScaledToUnit(0);
        var (shadows, midtones, highlights, rescale) = ComputeStretchParameters(median, mad, stretchFactor, shadowsClipping);

        // Stretch luminance and scale RGB channels by Y'/Y ratio
        await Parallel.ForAsync(0, height, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, async (y, ct) => await Task.Run(() =>
        {
            for (var x = 0; x < width; x++)
            {
                var luma = lumaData[y, x];
                if (float.IsNaN(luma))
                {
                    for (var c = 0; c < channelCount; c++)
                    {
                        destination[c][y, x] = float.NaN;
                    }
                    continue;
                }

                // Stretch the luminance
                var rescaled = (1 - highlights + luma - shadows) * rescale;
                var stretchedLuma = (float)MidtonesTransferFunction(midtones, rescaled);

                // Scale factor: Y'/Y (avoid division by zero)
                var scale = luma > 1e-7f ? stretchedLuma / luma : 0f;

                for (var c = 0; c < channelCount; c++)
                {
                    var value = data[c][y, x];
                    if (needsNorm) { value *= normFactor; }
                    destination[c][y, x] = Math.Clamp(value * scale, 0f, 1f);
                }
            }
            return ValueTask.CompletedTask;
        }, ct));
    }

    /// <summary>
    /// Stretches linked into a pre-allocated destination buffer, reusing memory.
    /// The destination array must have the same dimensions as this image.
    /// </summary>
    internal async Task<Image> StretchLinkedIntoAsync(float[][,] destination, double stretchFactor = 0.2d, double shadowsClipping = -3d, CancellationToken cancellationToken = default)
    {
        if (imageMeta.SensorType is SensorType.Monochrome)
        {
            return await StretchUnlinkedIntoAsync(destination, stretchFactor, shadowsClipping, cancellationToken);
        }

        var (channelCount, _, _) = Shape;
        var (pedestral, median, mad) = GetPedestralMedianAndMADScaledToUnit(0);

        for (var c = 0; c < channelCount; c++)
        {
            await StretchChannelAsync(destination, c, stretchFactor, shadowsClipping, pedestral, median, mad, cancellationToken);
        }

        return new Image(destination, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    /// <summary>
    /// Stretches unlinked into a pre-allocated destination buffer, reusing memory.
    /// The destination array must have the same dimensions as this image.
    /// </summary>
    internal async Task<Image> StretchUnlinkedIntoAsync(float[][,] destination, double stretchFactor = 0.2d, double shadowsClipping = -3d, CancellationToken cancellationToken = default)
    {
        var (channelCount, _, _) = Shape;

        for (var c = 0; c < channelCount; c++)
        {
            var (pedestral, median, mad) = GetPedestralMedianAndMADScaledToUnit(c);
            await StretchChannelAsync(destination, c, stretchFactor, shadowsClipping, pedestral, median, mad, cancellationToken);
        }

        return new Image(destination, BitDepth.Float32, 1.0f, 0f, 0f, imageMeta);
    }

    private async Task StretchChannelAsync(float[][,] stretched, int channel, double stretchFactor, double shadowsClipping, float pedestral, float median, float mad, CancellationToken cancellationToken = default)
    {
        var (channelCount, width, height) = Shape;

        if (channel < 0 || channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        var needsNorm = MaxValue > 1.0f + float.Epsilon;
        var normFactor = 1.0 / MaxValue;

        double shadows, midtones, highlights, rescale;

        // assume the image is inverted or overexposed when median is higher than half of the possible value
        if (median > 0.5)
        {
            shadows = 0f;
            highlights = median - shadowsClipping * mad * MAD_TO_SD;
            rescale = 1.0 / (highlights - 0);
            midtones = MidtonesTransferFunction(stretchFactor, 1f - (highlights - median) * rescale);
        }
        else
        {
            shadows = median + shadowsClipping * mad * MAD_TO_SD;
            rescale = 1.0 / (1.0 - shadows);
            // Rescaled median: (median - shadows) / (1 - shadows)
            midtones = MidtonesTransferFunction(stretchFactor, (median - shadows) * rescale);
            highlights = 1;
        }

        var srcChannel = data[channel];
        var dstChannel = stretched[channel];

        await Parallel.ForAsync(0, height, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, async (y, ct) => await Task.Run(() =>
        {
            for (var x = 0; x < width; x++)
            {
                var value = srcChannel[y, x];
                if (!float.IsNaN(value))
                {
                    var normValue = (needsNorm ? value * normFactor : value) - pedestral;
                    // Subtract blackpoint and rescale to [0,1] before applying MTF
                    var rescaled = (1 - highlights + normValue - shadows) * rescale;
                    dstChannel[y, x] = (float)MidtonesTransferFunction(midtones, rescaled);
                }
                else
                {
                    dstChannel[y, x] = float.NaN;
                }
            }
            return ValueTask.CompletedTask;
        }, ct));
    }

    /// <summary>
    /// Computes the stretch parameters (shadows, midtones, highlights, rescale) from channel statistics.
    /// These can be passed as GPU shader uniforms to perform the stretch on the GPU.
    /// </summary>
    public static (double Shadows, double Midtones, double Highlights, double Rescale) ComputeStretchParameters(
        float median, float mad, double stretchFactor, double shadowsClipping)
    {
        double shadows, midtones, highlights, rescale;

        if (median > 0.5)
        {
            shadows = 0f;
            highlights = median - shadowsClipping * mad * MAD_TO_SD;
            rescale = 1.0 / (highlights - 0);
            midtones = MidtonesTransferFunction(stretchFactor, 1f - (highlights - median) * rescale);
        }
        else
        {
            shadows = median + shadowsClipping * mad * MAD_TO_SD;
            rescale = 1.0 / (1.0 - shadows);
            midtones = MidtonesTransferFunction(stretchFactor, (median - shadows) * rescale);
            highlights = 1;
        }

        return (shadows, midtones, highlights, rescale);
    }

    /// <summary>
    /// Adjusts x for a given midToneBalance
    /// </summary>
    /// <param name="midToneBalance"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    private static double MidtonesTransferFunction(double midToneBalance, double value)
    {
        var clamped = Math.Clamp(value, 0, 1d);
        if (value == clamped)
        {
            return (midToneBalance - 1) * value / Math.FusedMultiplyAdd(Math.FusedMultiplyAdd(2, midToneBalance, -1), value, - midToneBalance);
        }
        else
        {
            return clamped;
        }
    }
}
