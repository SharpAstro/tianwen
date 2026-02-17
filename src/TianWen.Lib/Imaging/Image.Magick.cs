using CommunityToolkit.HighPerformance;
using ImageMagick;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    public async Task<IMagickImage<float>> ToMagickImageAsync(DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        var scaled = BitDepth != BitDepth.Float32 ? ScaleFloatValues(MaxValue, missingValue: 0f) : this;

        Image debayered;
        if (scaled.ImageMeta.SensorType is SensorType.RGGB)
        {
            if (debayerAlgorithm is DebayerAlgorithm.None)
            {
                throw new ArgumentException("Must specify an algorithm for debayering", nameof(debayerAlgorithm));
            }
            debayered = await scaled.DebayerAsync(debayerAlgorithm, cancellationToken);
        }
        else
        {
            debayered = scaled;
        }

        return debayered.DoToMagickImage();
    }

    /// <summary>
    /// Assumes that imge has been converted to floats and debayered.
    /// </summary>
    /// <returns></returns>
    private IMagickImage<float> DoToMagickImage()
    {
        var (channelCount, width, height) = Shape;
        var firstChannel = ChannelToImage(0); // mono or red

        IMagickImage<float> result;
        if (channelCount is 3)
        {
            var blue = ChannelToImage(1);
            var green = ChannelToImage(2);

            using var coll = new MagickImageCollection
            {
                firstChannel,
                blue,
                green
            };

            result = coll.Combine(ColorSpace.sRGB);
            result.SetProfile(ColorProfiles.SRGB);
        }
        else
        {
            result = firstChannel;
        }

        return result;

        MagickImage ChannelToImage(int channel)
        {
            var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height)
            {
                Format = MagickFormat.Tiff,
                Depth = 32,
                Endian = BitConverter.IsLittleEndian ? Endian.LSB : Endian.MSB,
                ColorType = ColorType.Grayscale
            };

            using var pix = image.GetPixelsUnsafe();
            pix.SetPixels(data.AsSpan(channel));

            return image;
        }
    }
}
