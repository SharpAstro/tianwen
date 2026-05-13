using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpAstro.Color.Icc;
using SharpAstro.Png;
using SharpAstro.Tiff;

namespace TianWen.Lib.Tests.Helpers;

/// <summary>
/// Test-only helper that writes <b>display-encoded</b> 8-bit RGB/RGBA buffers as
/// browser-viewable TIFF or PNG with an embedded sRGB v4 ICC profile. Centralises
/// the pattern previously duplicated across <see cref="GpuStretchPipelineTests"/>,
/// <see cref="VkRendererPrimitiveTests"/>, and <see cref="StretchTests_NewPipeline"/>.
///
/// <para>
/// The buffers passed in must already be sRGB-display-encoded — i.e., post-stretch
/// RGBA from <see cref="Imaging.Image.RenderStretchedRgba"/> or anything that has
/// applied the sRGB transfer function. Scene-linear data does <i>not</i> belong
/// here; tag-as-sRGB on linear data is a lie that produces double-dark images in
/// any colour-managed viewer.
/// </para>
/// </summary>
internal static class DisplayImageWriter
{
    /// <summary>
    /// Writes an RGBA byte buffer as an 8-bit RGB TIFF (Deflate compression,
    /// sRGB v4 ICC tag). Alpha is dropped — for stretch-pipeline output it is
    /// always 0xFF and never carries signal.
    /// </summary>
    public static async Task WriteTiffAsync(byte[] rgba, int width, int height, string path, CancellationToken ct = default)
    {
        var pixelCount = width * height;
        var rgb = new byte[pixelCount * 3];
        for (int p = 0, src = 0, dst = 0; p < pixelCount; p++, src += 4, dst += 3)
        {
            rgb[dst]     = rgba[src];
            rgb[dst + 1] = rgba[src + 1];
            rgb[dst + 2] = rgba[src + 2];
        }

        await using var fs = File.Create(path);
        await using var writer = TiffWriter.Create(fs);
        await writer.AddPageAsync(rgb, width, height, new TiffPageOptions
        {
            SamplesPerPixel = 3,
            BitsPerSample = 8,
            Photometric = TiffPhotometric.Rgb,
            SampleFormat = TiffSampleFormat.Uint,
            Compression = TiffCompression.Deflate,
            IccProfile = IccProfiles.SRgbV4,
        }, ct);
        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Encodes an RGBA byte buffer as a PNG with an iCCP sRGB v4 chunk. Returns
    /// the byte buffer; callers write it to disk themselves so the helper stays
    /// allocation-disciplined for callers that want to write to memory streams.
    /// </summary>
    public static byte[] EncodePng(byte[] rgba, int width, int height)
        => PngWriter.Encode(rgba, width, height, IccProfiles.SRgbV4.Span);
}
