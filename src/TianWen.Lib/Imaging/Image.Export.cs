using ImageMagick;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    public async Task<IMagickImage<float>> ToMagickImageAsync(DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        Image debayered;
        if (ImageMeta.SensorType is SensorType.RGGB)
        {
            if (debayerAlgorithm is DebayerAlgorithm.None)
            {
                throw new ArgumentException("Must specify an algorithm for debayering", nameof(debayerAlgorithm));
            }
            debayered = await DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
        }
        else
        {
            debayered = this;
        }

        return debayered.DoToMagickImage();
    }

    /// <summary>
    /// Converts the image to a MagickImage, scaling pixel values to [0, Quantum.Max].
    /// Uses a single reusable buffer per channel to avoid allocating a full scaled Image copy.
    /// </summary>
    private IMagickImage<float> DoToMagickImage()
    {
        var (channelCount, width, height) = Shape;
        var pixelCount = width * height;
        var scale = MaxValue > 0f ? Quantum.Max / MaxValue : 1f;

        // Reusable buffer for scaling one channel at a time
        var buffer = new float[pixelCount];

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

        // ZIP (Deflate) is lossless and typically halves the on-disk size of
        // 32-bit float TIFFs. Without this, libtiff defaults to no compression
        // and a 4Kx4K RGB frame is ~192 MB. Settings.Compression is the
        // encoder-side knob; the read-only Compression property reflects the
        // source. Set on the combined result as well because Combine() produces
        // a new image with default (Undefined) compression regardless of the
        // source channels' settings.
        result.Settings.Compression = CompressionMethod.Zip;

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
            image.Settings.Compression = CompressionMethod.Zip;

            // Scale channel data into reusable buffer (SIMD-accelerated)
            MultiplyScalar(GetChannelSpan(channel), scale, buffer);

            using var pix = image.GetPixelsUnsafe();
            pix.SetPixels(buffer.AsSpan());

            return image;
        }
    }
}
