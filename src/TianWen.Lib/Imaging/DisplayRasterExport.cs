using SharpAstro.Color.Icc;
using SharpAstro.Jpeg;
using SharpAstro.Png;
using SharpAstro.Tiff;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Imaging;

/// <summary>The container a display raster is written to.</summary>
/// <remarks>
/// <para>Deliberately NOT the full set the codecs facade can write. EXR is absent because it carries
/// no transfer tag and is read as scene-linear, so a display raster in an EXR is re-interpreted by
/// every reader that honours the convention -- the same reason
/// <see cref="Image.WriteStretchedTiffAsync"/> emits TIFF rather than EXR for stretched plates. JXR
/// is absent for want of a reader, not for want of correctness.</para>
/// <para><see cref="Png16"/> is the default because it is the only one of these that is LOSSLESS
/// against the raster the shader produced: the GPU path quantises to 8 bits only at the very end of
/// the swapchain, so 16-bit PNG holds strictly more of what was on screen than a screenshot could.</para>
/// </remarks>
public enum DisplayRasterFormat
{
    /// <summary>16-bit-per-channel PNG. Lossless against the display raster.</summary>
    Png16,

    /// <summary>8-bit-per-channel PNG. Half the size, and what a screenshot would have given.</summary>
    Png8,

    /// <summary>Baseline JPEG, sRGB-tagged. Lossy; for sharing rather than for keeping.</summary>
    Jpeg,

    /// <summary>32-bit IEEE-float TIFF in the repo's <c>[0, 1]</c> convention.</summary>
    TiffFloat,
}

/// <summary>
/// Writes the DISPLAY raster -- the pixels as the viewer is showing them, stretch, white balance,
/// curves, HDR and channel view included -- to an image file.
/// </summary>
/// <remarks>
/// <para><b>This is not a screenshot, and the difference is resolution.</b> The raster comes from
/// <see cref="Image.RenderStretchedRgba"/> / <see cref="Image.RenderStretchedRgba16"/>, the CPU mirror
/// of the viewer's shader, so it is produced at the image's own size rather than the window's and
/// needs no framebuffer readback. A 9576x6388 master saves at 9576x6388 from a 1280-pixel-wide window.
/// </para>
/// <para><b>Nothing drawn OVER the image is included</b>: no WCS grid, no star markers, no object
/// labels, no before/after split. Those live in the renderer, not in the pixels, and reproducing them
/// here would be a second drawing path beside the GPU one -- see P22 in
/// <c>docs/plans/viewer-prerelease-fixes.md</c>, which is where that job is tracked.</para>
/// <para>Because it goes through the same <see cref="StretchUniforms"/> the shader was handed, the
/// CPU/GPU mirror rule in CLAUDE.md applies: a stage added to one owes the other. A file that does not
/// match the screen is this contract being broken somewhere upstream, not a bug here.</para>
/// </remarks>
public static class DisplayRasterExport
{
    /// <summary>The canonical extension for each format, and what the save dialog offers.</summary>
    public static string Extension(this DisplayRasterFormat format) => format switch
    {
        DisplayRasterFormat.Png16 or DisplayRasterFormat.Png8 => ".png",
        DisplayRasterFormat.Jpeg => ".jpg",
        DisplayRasterFormat.TiffFloat => ".tif",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>A human name for the format, for a menu row or a status line.</summary>
    public static string DisplayName(this DisplayRasterFormat format) => format switch
    {
        DisplayRasterFormat.Png16 => "PNG (16-bit)",
        DisplayRasterFormat.Png8 => "PNG (8-bit)",
        DisplayRasterFormat.Jpeg => "JPEG",
        DisplayRasterFormat.TiffFloat => "TIFF (32-bit float)",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// The format a path's extension asks for, or <c>null</c> when the extension is not one we write.
    /// </summary>
    /// <remarks>
    /// <c>.png</c> resolves to <see cref="DisplayRasterFormat.Png16"/>, never the 8-bit variant: both
    /// share an extension, so a path alone cannot distinguish them and the lossless one is the better
    /// default to land on. Choosing 8-bit is therefore an explicit act in the Save-As menu, which is
    /// the only place the distinction is visible.
    /// </remarks>
    public static DisplayRasterFormat? FromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => DisplayRasterFormat.Png16,
        ".jpg" or ".jpeg" => DisplayRasterFormat.Jpeg,
        ".tif" or ".tiff" => DisplayRasterFormat.TiffFloat,
        _ => null,
    };

    /// <summary>
    /// Renders <paramref name="image"/> through <paramref name="uniforms"/> and writes the result to
    /// <paramref name="path"/>.
    /// </summary>
    /// <param name="image">The UNSTRETCHED image the viewer is displaying.</param>
    /// <param name="path">Output path. Its extension is not consulted; <paramref name="format"/> decides.</param>
    /// <param name="format">Container and bit depth.</param>
    /// <param name="uniforms">The stretch the viewer is displaying with.</param>
    /// <param name="curvesBoost">Power-law boost amount, as passed to the shader.</param>
    /// <param name="curvesMode">0 = power-law boost, 1 = spline LUT.</param>
    /// <param name="curveLut">Spline knots when <paramref name="curvesMode"/> is 1.</param>
    /// <param name="curvesMidpoint">Post-stretch background level the boost pivots on.</param>
    /// <param name="hdrAmount">HDR knee-compression amount; 0 = off.</param>
    /// <param name="hdrKnee">HDR knee point.</param>
    /// <param name="displayedChannel">
    /// The single source channel the viewer is showing (0 = R, 1 = G, 2 = B), or <c>null</c> for the
    /// composite. A single-channel view saves as MONO, which is what the screen shows: the GPU path
    /// uploads that one channel into slot 0 and samples it as grey.
    /// </param>
    /// <param name="debayerAlgorithm">
    /// How to debayer a raw CFA frame before rendering. Only consulted for a 1-channel RGGB image in
    /// the composite view, which is the one case where the screen is showing colour that the image's
    /// own channels do not carry.
    /// </param>
    /// <param name="jpegQuality">JPEG quality, 1-100. Ignored by every other format.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteAsync(
        Image image,
        string path,
        DisplayRasterFormat format,
        StretchUniforms uniforms,
        float curvesBoost = 0f,
        int curvesMode = 0,
        ImmutableArray<float> curveLut = default,
        float curvesMidpoint = 0.25f,
        float hdrAmount = 0f,
        float hdrKnee = 0.8f,
        int? displayedChannel = null,
        DebayerAlgorithm debayerAlgorithm = DebayerAlgorithm.VNG,
        int jpegQuality = 92,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrEmpty(path);

        // A raw CFA frame in the composite view is the one case where the SCREEN carries colour the
        // image's own channels do not: the shader debayers from the mosaic. A single-channel view of
        // the same frame shows the mosaic itself as grey, so it must NOT be debayered -- matching what
        // ImageRendererBase.UploadDocumentTextures decides between.
        var isComposite = displayedChannel is null;
        var source = image.ImageMeta.SensorType is SensorType.RGGB && image.Shape.ChannelCount == 1 && isComposite
            ? await image.DebayerAsync(debayerAlgorithm, cancellationToken: cancellationToken).ConfigureAwait(false)
            : image;

        var (channelCount, width, height) = source.Shape;
        var pixelCount = width * height;
        var isColour = channelCount >= 3 && isComposite;

        byte[] encoded;
        switch (format)
        {
            case DisplayRasterFormat.Png16:
            {
                var rgba = RenderRgba16();
                encoded = PngWriter.EncodeRgba16(rgba, width, height, new PngWriteOptions { Cicp = CicpChunk.Srgb });
                break;
            }

            case DisplayRasterFormat.Png8:
            {
                var rgba = RenderRgba8();
                encoded = PngWriter.Encode(rgba, width, height, IccProfiles.SRgbV4.Span);
                break;
            }

            case DisplayRasterFormat.Jpeg:
            {
                // JPEG has no alpha, so the RGBA render is packed down to RGB (or to grey for a mono
                // view, which is a third of the bytes for identical pixels).
                var rgba = RenderRgba8();
                var samplesPerPixel = isColour ? 3 : 1;
                var packed = new byte[pixelCount * samplesPerPixel];
                for (var i = 0; i < pixelCount; i++)
                {
                    if (isColour)
                    {
                        packed[i * 3 + 0] = rgba[i * 4 + 0];
                        packed[i * 3 + 1] = rgba[i * 4 + 1];
                        packed[i * 3 + 2] = rgba[i * 4 + 2];
                    }
                    else
                    {
                        packed[i] = rgba[i * 4];
                    }
                }

                var jpeg = JpegEncoder.Encode(packed, width, height, samplesPerPixel,
                    new JpegEncodeOptions { Quality = Math.Clamp(jpegQuality, 1, 100) });
                encoded = JpegIccInjector.EmbedIccProfile(jpeg, IccProfiles.SRgbV4);
                break;
            }

            case DisplayRasterFormat.TiffFloat:
            {
                // The 16-bit render divided back to [0, 1] rather than a second float render: the
                // display raster IS quantised at 16 bits by the time it is a raster, so a float TIFF
                // of it is a container choice, not extra precision. Keeping one render path also keeps
                // the two files pixel-identical where the containers can both express the value.
                var rgba = RenderRgba16();
                var samplesPerPixel = isColour ? 3 : 1;
                var bytes = new byte[pixelCount * samplesPerPixel * sizeof(float)];
                var floats = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
                for (var i = 0; i < pixelCount; i++)
                {
                    if (isColour)
                    {
                        floats[i * 3 + 0] = rgba[i * 4 + 0] / 65535f;
                        floats[i * 3 + 1] = rgba[i * 4 + 1] / 65535f;
                        floats[i * 3 + 2] = rgba[i * 4 + 2] / 65535f;
                    }
                    else
                    {
                        floats[i] = rgba[i * 4] / 65535f;
                    }
                }

                await using var writer = TiffWriter.Create(path);
                await writer.AddPageAsync(bytes, width, height, new TiffPageOptions
                {
                    SampleFormat = TiffSampleFormat.IeeeFloat,
                    BitsPerSample = 32,
                    SamplesPerPixel = samplesPerPixel,
                    Photometric = isColour ? TiffPhotometric.Rgb : TiffPhotometric.MinIsBlack,
                    IccProfile = IccProfiles.SRgbV4,
                    SMinSampleValue = 0f,
                    SMaxSampleValue = 1f,
                    Compression = TiffCompression.Deflate,
                    Software = "TianWen",
                }, cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }

        await File.WriteAllBytesAsync(path, encoded, cancellationToken).ConfigureAwait(false);

        // Both renders are local functions rather than inline expressions so no Span<T> ever has to
        // live across the awaits above -- a ref struct cannot, and the compiler's error for it names
        // the await rather than the span.
        byte[] RenderRgba8()
        {
            var rgba = new byte[pixelCount * 4];
            source.RenderStretchedRgba(uniforms, rgba, curvesBoost, curvesMode,
                curveLut.IsDefaultOrEmpty ? default : curveLut.AsSpan(), curvesMidpoint, hdrAmount, hdrKnee);
            CollapseToDisplayedChannel(rgba, byte.MaxValue);
            return rgba;
        }

        ushort[] RenderRgba16()
        {
            var rgba = new ushort[pixelCount * 4];
            source.RenderStretchedRgba16(uniforms, rgba, curvesBoost, curvesMode,
                curveLut.IsDefaultOrEmpty ? default : curveLut.AsSpan(), curvesMidpoint, hdrAmount, hdrKnee);
            CollapseToDisplayedChannel(rgba, ushort.MaxValue);
            return rgba;
        }

        // A single-channel view shows ONE channel as grey. The render above already applied that
        // channel's own curve -- the uniforms carry a per-channel shadow / midtone / rescale triple --
        // so the displayed value is already sitting in its own slot and only has to be replicated
        // across the other two. Re-rendering a single-channel image instead would lose exactly that.
        void CollapseToDisplayedChannel<T>(T[] rgba, T opaque) where T : struct
        {
            if (displayedChannel is not { } c || channelCount < 3)
            {
                return;
            }

            for (var i = 0; i < pixelCount; i++)
            {
                var v = rgba[i * 4 + c];
                rgba[i * 4 + 0] = v;
                rgba[i * 4 + 1] = v;
                rgba[i * 4 + 2] = v;
                rgba[i * 4 + 3] = opaque;
            }
        }
    }
}
