using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using nom.tam.fits;
using SharpAstro.Ser;

namespace TianWen.Lib.Imaging
{
    /// <summary>
    /// A stretched RGBA8 raster no larger than the requested edge, alpha always 255. RGBA rather than
    /// a platform bitmap: this type is what a headless consumer (a test, the hosted API, a future
    /// file-list strip) reads directly; the Windows shell DLL swizzles it into a BGRA DIB at the boundary.
    /// </summary>
    public readonly record struct ThumbnailRaster(byte[] Rgba, int Width, int Height);

    /// <summary>
    /// Renders a small preview of an astronomical file for a thumbnail surface, today Windows Explorer
    /// through <c>tianwen-thumb.dll</c> (<c>TianWen.Shell.Thumbnails</c>).
    /// <para>
    /// <b>It stretches through <see cref="StretchSolver"/> and <see cref="Image.RenderStretchedRgba"/>, the
    /// one pipeline the GPU viewer, the TUI and the hosted JPEG preview share</b>, with the stretch mode the
    /// viewer itself would pick for a fresh document (<see cref="StretchModeExtensions.ResolveAuto"/>:
    /// colour without a calibration renders Unlinked, mono renders Linked). So the thumbnail Explorer draws
    /// is what the viewer shows when the file is opened, not a third rendering of the same frame.
    /// </para>
    /// <para>
    /// <b>The container is sniffed from the first bytes, never taken from an extension.</b> The shell hands
    /// a thumbnail handler a stream and nothing else (a packaged handler runs out of process and only
    /// <c>IInitializeWithStream</c> is available there), so one COM class serves every registered type.
    /// FITS opens with <c>SIMPLE  =</c> and SER with <c>LUCAM-RECORDER</c>; a tile-compressed <c>.fz</c> is
    /// FITS whose image sits in an extension HDU, which <see cref="Image.TryReadFitsFile(Fits, out Image)"/>
    /// already walks to, so it needs no case of its own.
    /// </para>
    /// <para>
    /// <b>Cost is bounded by the OUTPUT, not the input.</b> The frame is debayered (a CFA mosaic binned
    /// as-is averages the pattern away into grey), then mean-binned with <see cref="Image.Downsample"/> so
    /// its short edge lands at or just above the requested size, and only that small raster is
    /// stat-scanned, stretched and box-resampled. A 3008x3008 RGGB light renders at 256 px in about 110 ms
    /// on an arm64 laptop; a full-frame stretch would spend most of a second on the same file for pixels
    /// nobody sees.
    /// </para>
    /// <para>
    /// <b>No cache lives here, by design.</b> Windows keeps its own thumbnail cache per size class and
    /// re-asks the handler only when a file is missing from it or has a newer modified time than the
    /// cached copy, so a handler-side cache would be a second copy of the same keys in a process the shell
    /// tears down between requests. A future viewer file-list strip should read the shell's cache
    /// (<c>IThumbnailCache</c>) rather than build another.
    /// </para>
    /// </summary>
    public static class ThumbnailRenderer
    {
        /// <summary>The size Explorer asks for most; also the cap on the pre-bin target so a request for
        /// a 1024 px cache entry does not stretch a 24 MP frame at full resolution.</summary>
        public const int DefaultMaxEdge = 256;

        /// <summary>
        /// The COM class identity under which Windows activates this renderer. Defined beside the renderer,
        /// because two Windows projects that do not reference each other both need it: the shell DLL that
        /// implements the class (<c>TianWen.Shell.Thumbnails</c>) and the viewer that registers it for an
        /// unpackaged install (<c>FileAssociationRegistrar</c>). The MSIX manifest carries the same GUID
        /// as text and <c>build-msix.ps1 -ValidateOnly</c> checks the manifest agrees with itself.
        /// Stable for the life of the product: a changed CLSID is a handler Windows no longer finds.
        /// </summary>
        public static readonly Guid ShellExtensionClsid = new Guid("bde44417-0b48-4d32-931e-8b3192e81be2");

        /// <summary>The shell's well-known handler id for <c>IThumbnailProvider</c>, the <c>ShellEx</c>
        /// subkey a file type's thumbnail provider CLSID is written under.</summary>
        public static readonly Guid ThumbnailProviderHandlerId = new Guid("e357fccd-a995-4576-b01f-234630154e96");

        private const int MinEdge = 16;
        private const int MaxEdgeCap = 1024;

        private static ReadOnlySpan<byte> FitsMagic => "SIMPLE  ="u8;
        private static ReadOnlySpan<byte> SerMagic => "LUCAM-RECORDER"u8;

        /// <summary>
        /// Decodes the file in <paramref name="source"/> (FITS incl. <c>.fz</c>, or the first frame of a
        /// SER capture) and renders it to at most <paramref name="maxEdge"/> pixels on its long edge,
        /// aspect preserved, never upscaled. The stream is consumed and may be left at any position.
        /// </summary>
        /// <exception cref="InvalidDataException">The bytes are neither FITS nor SER, or carry no image.</exception>
        public static async Task<ThumbnailRaster> RenderAsync(Stream source, int maxEdge = DefaultMaxEdge, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);

            var image = Decode(source);
            return await RenderAsync(image, maxEdge, cancellationToken);
        }

        /// <summary>
        /// Renders an already decoded frame. <paramref name="image"/> is only read, never released or
        /// rescaled in place (the debayer is asked for a fresh instance).
        /// </summary>
        public static async Task<ThumbnailRaster> RenderAsync(Image image, int maxEdge = DefaultMaxEdge, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxEdge, 1);

            var target = Math.Clamp(maxEdge, MinEdge, MaxEdgeCap);

            // Colour first: a CFA mosaic binned as-is averages the pattern away into grey. For a mono or
            // 3-channel frame this returns the same instance, so there is no copy to pay for.
            // normalizeToUnit stays false because the normalising overload rescales its input IN PLACE.
            var rgb = await image.DebayerAsync(DebayerAlgorithm.MHC, normalizeToUnit: false, cancellationToken);

            // Bin before stretching. The factor keeps the short edge at or above the target, so the final
            // resample only ever shrinks, and the stat scan + MTF run over a few hundred pixels a side.
            var (channels, width, height) = rgb.Shape;
            var factor = Math.Max(1, Math.Min(width, height) / target);
            var small = rgb.Downsample(factor);
            (channels, width, height) = small.Shape;

            var isColour = channels >= 3;
            var mode = StretchMode.Auto.ResolveAuto(isColour, calibrationActive: false);
            var stats = StretchSolver.CollectPerChannelStats(small, channels);
            var uniforms = StretchSolver.ComputeStretchUniforms(
                mode,
                StretchParameters.Default,
                stats,
                lumaStats: null,
                small.MaxValue);

            var rgba = new byte[width * height * 4];
            small.RenderStretchedRgba(uniforms, rgba);

            // Fit inside target x target and keep the aspect: the shell pads to a square itself and asks
            // handlers not to.
            var scale = Math.Min(1.0, (double)target / Math.Max(width, height));
            var outWidth = Math.Max(1, (int)Math.Round(width * scale));
            var outHeight = Math.Max(1, (int)Math.Round(height * scale));
            if (outWidth == width && outHeight == height)
            {
                return new ThumbnailRaster(rgba, width, height);
            }

            var output = new byte[outWidth * outHeight * 4];
            BoxDownsample(rgba, width, height, output, outWidth, outHeight);
            return new ThumbnailRaster(output, outWidth, outHeight);
        }

        /// <summary>Sniffs the container and decodes one frame; internal so the tests can pin the dispatch.</summary>
        internal static Image Decode(Stream source)
        {
            if (!source.CanSeek)
            {
                // The sniff needs to rewind. A handler always hands over a MemoryStream, so this is the
                // odd caller, and copying is cheaper than a second code path.
                var buffered = new MemoryStream();
                source.CopyTo(buffered);
                buffered.Position = 0;
                source = buffered;
            }

            Span<byte> head = stackalloc byte[SerMagic.Length];
            var start = source.Position;
            var read = source.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            source.Position = start;

            if (read >= FitsMagic.Length && head[..FitsMagic.Length].SequenceEqual(FitsMagic))
            {
                return DecodeFits(source);
            }

            if (read >= SerMagic.Length && head.SequenceEqual(SerMagic))
            {
                return DecodeSerFirstFrame(source);
            }

            throw new InvalidDataException("Not a FITS or SER file: the first bytes match neither signature.");
        }

        private static Image DecodeFits(Stream source)
        {
            using var fits = new Fits(source);
            return Image.TryReadFitsFile(fits, out var image)
                ? image
                : throw new InvalidDataException("The FITS file carries no image HDU.");
        }

        private static Image DecodeSerFirstFrame(Stream source)
        {
            Span<byte> headerBytes = stackalloc byte[SerHeader.Size];
            source.ReadExactly(headerBytes);
            var header = SerHeader.Parse(headerBytes);
            if (header.FrameCount < 1 || header.Width < 1 || header.Height < 1)
            {
                throw new InvalidDataException("The SER file has no frames.");
            }

            var frameSize = header.FrameSizeBytes;
            if (frameSize > int.MaxValue)
            {
                throw new InvalidDataException($"A SER frame of {frameSize} bytes is larger than a thumbnail can decode.");
            }

            var frame = new byte[frameSize];
            source.ReadExactly(frame);
            return SerImageBridge.ToImage(in header, frame);
        }

        /// <summary>
        /// Box average, RGBA to RGBA with opaque alpha. Averaging rather than point-sampling is deliberate:
        /// a star a few pixels wide falls between the samples of a 1:8 nearest-neighbour pass and vanishes,
        /// so the preview under-reports what was captured; a box filter keeps it as a dimmer pixel.
        /// </summary>
        private static void BoxDownsample(ReadOnlySpan<byte> rgba, int srcWidth, int srcHeight, Span<byte> dst, int outWidth, int outHeight)
        {
            var xRatio = (double)srcWidth / outWidth;
            var yRatio = (double)srcHeight / outHeight;

            for (var y = 0; y < outHeight; y++)
            {
                var sy0 = (int)(y * yRatio);
                var sy1 = Math.Min(srcHeight, Math.Max(sy0 + 1, (int)((y + 1) * yRatio)));

                for (var x = 0; x < outWidth; x++)
                {
                    var sx0 = (int)(x * xRatio);
                    var sx1 = Math.Min(srcWidth, Math.Max(sx0 + 1, (int)((x + 1) * xRatio)));

                    uint r = 0, g = 0, b = 0;
                    var samples = 0;
                    for (var sy = sy0; sy < sy1; sy++)
                    {
                        var row = sy * srcWidth * 4;
                        for (var sx = sx0; sx < sx1; sx++)
                        {
                            var i = row + (sx * 4);
                            r += rgba[i];
                            g += rgba[i + 1];
                            b += rgba[i + 2];
                            samples++;
                        }
                    }

                    var o = ((y * outWidth) + x) * 4;
                    dst[o] = (byte)(r / samples);
                    dst[o + 1] = (byte)(g / samples);
                    dst[o + 2] = (byte)(b / samples);
                    dst[o + 3] = 255;
                }
            }
        }
    }
}
