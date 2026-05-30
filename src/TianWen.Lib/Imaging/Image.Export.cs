using SharpAstro.Jxr;
using SharpAstro.Tiff;
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

    /// <summary>
    /// Writes the image to <paramref name="path"/> as a JPEG XR (T.832) file
    /// with float-true HDR pixels. Bayer images are debayered first with
    /// <paramref name="debayerAlgorithm"/>; mono / 3-channel images pass through.
    ///
    /// <para>The "HDR" promise is real precision <b>and</b> real dynamic range:</para>
    /// <list type="bullet">
    ///   <item><term>Mono</term><description><c>BD32F</c> — full IEEE single-precision
    ///   float per pixel, encoded with <c>lenMantissa = 8</c>.</description></item>
    ///   <item><term>RGB</term><description><c>BD16F</c> — 16-bit half-float
    ///   per channel (~11-bit effective mantissa, full half-float exponent range
    ///   ~6e-5 to 65,504). The T.832 container has no Table A.6 pixel-format GUID
    ///   for BD32F RGB so half-float is the canonical RGB HDR shape.</description></item>
    /// </list>
    ///
    /// <para><b>No normalisation</b> — values are written verbatim. Bright
    /// star cores that overshoot <c>1.0</c> after MTF / asinh stretches (or
    /// raw unscaled FITS data with values in the tens of thousands) are
    /// preserved as written. Half-float clips at ~65,504; callers writing
    /// raw FITS without prior normalisation should ensure that range fits
    /// (it does for ushort FITS data: max 65,535 → 65,504 after Half cast,
    /// a 0.05% loss at the very brightest pixels).</para>
    ///
    /// <para>Lossless quantisation (QP indices 0) and no POT (<c>overlap = 0</c>),
    /// the <see cref="JxrImageCodec"/> defaults, give a bit-exact round-trip on the
    /// float-pixel representation for BD32F, and as close to lossless as half-float
    /// allows for BD16F.</para>
    /// </summary>
    public async Task WriteJxrAsync(string path, DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
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
        // Drop alpha / extra channels — JXR output is mono (1) or RGB (3).
        var outChannels = channelCount >= 3 ? 3 : 1;
        var pixelCount = width * height;

        byte[] jxrBytes;
        if (outChannels == 1)
        {
            // BD32F grayscale: full float32 fidelity. Source values are
            // written verbatim -- if the pipeline overshot 1.0 on bright
            // cores, those magnitudes are preserved (HDR semantics).
            var pixels = new float[pixelCount];
            source.GetChannelSpan(0).CopyTo(pixels);
            // jxrlib-faithful re-port: lenMantissa 8 is the astrophotography
            // precision/size trade-off; expBias, lossless QP and OL_NONE take the
            // codec's defaults (which match jxrlib's encoder).
            jxrBytes = JxrImageCodec.EncodeGrayF32(pixels, width, height, lenMantissa: 8);
        }
        else
        {
            // BD16F RGB: half-float interleaved. Order is RGBRGB... contig
            // per JxrEncoder convention. (Half)x silently clips finite
            // values above ~65,504 -- a non-issue for our pipeline (post-
            // stretch peaks well below that) but noted for raw-FITS callers.
            //
            // The codec applies the T.832 §9.6.2.7 YCoCg-R reversible lifting
            // pre-FCT and tags the codestream InternalClrFmt=YUV444. The colour
            // transform is lossless (reversible lifting), so JXR round-trips are
            // bit-exact for the half-float values via SharpAstro.Jxr's decoder.
            var halfPixels = new Half[pixelCount * 3];
            var r = source.GetChannelSpan(0);
            var g = source.GetChannelSpan(1);
            var b = source.GetChannelSpan(2);
            for (var i = 0; i < pixelCount; i++)
            {
                halfPixels[i * 3 + 0] = (Half)r[i];
                halfPixels[i * 3 + 1] = (Half)g[i];
                halfPixels[i * 3 + 2] = (Half)b[i];
            }
            // Re-port emits OutputClrFmt=Rgb, which is verified to open in WIC /
            // Windows Photos with non-zero pixels -- the old NComponent workaround
            // is not needed by this codec (JxrHdrWicTests in the StbImageSharp repo).
            jxrBytes = JxrImageCodec.EncodeRgbF16(halfPixels, width, height);
        }

        await File.WriteAllBytesAsync(path, jxrBytes, cancellationToken);
    }
}
