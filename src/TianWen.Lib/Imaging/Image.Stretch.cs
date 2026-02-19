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

            var stretchedData = new float[channelCount, height, width];
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

        var stretchedData = new float[channelCount, height, width];

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

    private async Task StretchChannelAsync(float[,,] stretched, int channel, double stretchFactor, double shadowsClipping, float pedestral, float median, float mad, CancellationToken cancellationToken = default)
    {
        var (channelCount, width, height) = Shape;

        if (channel < 0 || channel >= channelCount)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        var needsNorm = MaxValue > 1.0f + float.Epsilon;
        var normFactor = 1.0 / MaxValue;

        double shadows, midtones, highlights;

        // assume the image is inverted or overexposed when median is higher than half of the possible value
        if (median > 0.5)
        {
            shadows = 0f;
            highlights = median - shadowsClipping * mad * MAD_TO_SD;
            midtones = MidtonesTransferFunction(stretchFactor, 1f - (highlights - median));
        }
        else
        {
            shadows = median + shadowsClipping * mad * MAD_TO_SD;
            midtones = MidtonesTransferFunction(stretchFactor, median - shadows);
            highlights = 1;
        }

        await Parallel.ForAsync(0, height, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, async (y, ct) => await Task.Run(() =>
        {
            for (var x = 0; x < width; x++)
            {
                var value = data[channel, y, x];
                if (!float.IsNaN(value))
                {
                    var normValue = (needsNorm ? value * normFactor : value) - pedestral;
                    stretched[channel, y, x] = (float)MidtonesTransferFunction(midtones, 1 - highlights + normValue - shadows);
                }
                else
                {
                    stretched[channel, y, x] = float.NaN;
                }
            }
            return ValueTask.CompletedTask;
        }, ct));
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
