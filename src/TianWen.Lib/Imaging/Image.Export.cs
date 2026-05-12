using DIR.Lib.Tiff;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    // libtiff-HDRI (and Magick.NET in Q16-HDRI) round-trips float TIFFs through
    // SMinSampleValue / SMaxSampleValue: on read the file's [0, 1] floats are
    // multiplied by SMaxSampleValue back to the in-memory range. 65535f is the
    // canonical Q16-HDRI quantum, so this constant keeps Magick.NET reads coming
    // back at [0, 65535] (matching what ToMagickImageAsync used to produce) while
    // scientific readers — tifffile, PixInsight, ImageJ — see the literal
    // scene-linear [0, 1] floats as written.
    private const float Q16HdriQuantumMax = 65535f;

    /// <summary>
    /// Writes the image to <paramref name="path"/> as a 32-bit IEEE-float TIFF via
    /// DIR.Lib (no Magick.NET on this path). Bayer images are debayered first with
    /// <paramref name="debayerAlgorithm"/>; mono / 3-channel images pass through.
    ///
    /// File values are in the [0, 1] scene-linear convention. The
    /// <c>SMinSampleValue=0</c> / <c>SMaxSampleValue=<see cref="Q16HdriQuantumMax"/></c>
    /// tags are emitted so libtiff-HDRI / Magick.NET re-maps to [0, Quantum.Max]
    /// on read, matching what the old <c>ToMagickImageAsync</c> + Magick.NET write
    /// produced byte-for-byte (modulo round-trip-tested quantum tolerance). 32-bit
    /// floats compress poorly without it, so Deflate is used — typically halves
    /// the on-disk size relative to uncompressed.
    /// </summary>
    public async Task WriteTiffAsync(string path, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        Image source;
        if (ImageMeta.SensorType is SensorType.RGGB)
        {
            if (debayerAlgorithm is DebayerAlgorithm.None)
                throw new ArgumentException("Must specify a debayer algorithm for RGGB images", nameof(debayerAlgorithm));
            source = await DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken);
        }
        else
        {
            source = this;
        }

        var (channelCount, width, height) = source.Shape;
        // Drop alpha / extra channels — TIFF output is mono (1) or RGB (3).
        var outChannels = channelCount >= 3 ? 3 : 1;
        var pixelCount = width * height;
        var byteBuffer = new byte[pixelCount * outChannels * 4];
        var floats = MemoryMarshal.Cast<byte, float>(byteBuffer.AsSpan());

        // Normalize source values to [0, 1]. If MaxValue is already 1.0 (or below)
        // this is a no-op scale; for unnormalized FITS data (MaxValue=65535 etc.)
        // it brings the file into the [0, 1] convention.
        var scale = source.MaxValue > 0f ? 1f / source.MaxValue : 1f;

        // Interleave channel-planar → pixel-interleaved for the TIFF strip. Each
        // pixel is `outChannels` consecutive floats; readers expecting RGB-RGB-RGB
        // stride match (which is the contig PlanarConfig=1 default).
        for (var c = 0; c < outChannels; c++)
        {
            var channelSpan = source.GetChannelSpan(c);
            for (var i = 0; i < pixelCount; i++)
            {
                floats[i * outChannels + c] = channelSpan[i] * scale;
            }
        }

        await using var fs = File.Create(path);
        await using var writer = TiffWriter.Create(fs);
        await writer.AddPageAsync(byteBuffer, width, height, new TiffPageOptions
        {
            SamplesPerPixel = outChannels,
            BitsPerSample = 32,
            Photometric = outChannels == 1 ? TiffPhotometric.MinIsBlack : TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.IeeeFloat,
            SMinSampleValue = 0f,
            SMaxSampleValue = Q16HdriQuantumMax,
            Compression = TiffCompression.Deflate,
        }, cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}
