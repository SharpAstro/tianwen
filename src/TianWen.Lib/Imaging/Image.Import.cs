using FC.SDK.Raw;
using SharpAstro.Codecs;
using SharpAstro.Exif;
using SharpAstro.Tiff;
using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using TianWen.Lib.Astrometry;
using CodecSampleFormat = SharpAstro.Codecs.Abstractions.SampleFormat;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Reads a supported astronomy image file and returns an <see cref="Image"/> with float
    /// channel data. Supported formats (extension dispatch):
    /// <list type="bullet">
    /// <item><b>TIFF</b> (.tif / .tiff) via DIR.Lib's pure-managed <c>TiffReader</c>.</item>
    /// <item><b>Canon CR2</b> (.cr2) and <b>Canon CR3</b> (.cr3) via FC.SDK.Raw's
    /// pure-managed decoder. Populates <see cref="ImageMeta.CameraToSrgbMatrix"/> via
    /// the spectral (SASP) or dcraw factory lookup; null when neither matches.</item>
    /// <item><b>FITS</b> (.fits / .fit / .fts, and .fz for fpack tile compression) via
    /// <see cref="TryReadFitsFile(string, out Image?)"/>.</item>
    /// <item><b>PNG / JPEG / JPEG XR / OpenEXR / JPEG XL</b> via the <c>SharpAstro.Codecs</c>
    /// facade (<see cref="TryReadViaCodecs"/>): the raster formats tianwen writes (PNG previews,
    /// EXR/JXR HDR masters) but has no bespoke reader for, so an exported frame can be reopened.</item>
    /// </list>
    /// Anything the facade cannot sniff returns <c>false</c>; there is no Magick.NET fallback. Pixel values
    /// are normalised to [0, 1] regardless of source bit depth. EXIF metadata is extracted
    /// into <see cref="ImageMeta"/> where present.
    /// </summary>
    public static bool TryReadImageFile(string fileName, [NotNullWhen(true)] out Image? image)
        => TryReadImageFile(fileName, out image, out _);

    /// <summary>
    /// As <see cref="TryReadImageFile(string, out Image?)"/>, additionally yielding the file's own WCS when it
    /// carries one. Only the FITS branch can: every other format here has nowhere to put one, so
    /// <paramref name="wcs"/> is null for them rather than absent.
    ///
    /// This overload exists so a caller that wants BOTH does not have to re-derive which extensions
    /// are FITS. A plate solver needs exactly that pair -- pixels to solve, plus a header WCS to seed
    /// the search -- and the previous shape pushed it to call TryReadFitsFile directly, which THROWS
    /// on a non-FITS file instead of returning false.
    /// </summary>
    public static bool TryReadImageFile(string fileName, [NotNullWhen(true)] out Image? image, out WCS? wcs)
    {
        wcs = null;
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext is ".tif" or ".tiff")
        {
            return TryReadTiff(fileName, out image);
        }
        if (ext is ".cr2" or ".cr3")
        {
            // FC.SDK.Raw pure-managed Canon decoder. Internal CanonRaw.Open
            // dispatches on file signature: TIFF magic -> Cr2Decoder, ISO BMFF
            // ftyp -> Cr3Decoder. Both produce the same CanonRawFile shape so
            // the downstream preprocess / matrix / ImageMeta wiring is shared.
            return TryReadCanonRaw(fileName, out image);
        }
        // .fz is fpack tile compression, which FITS.Lib decodes as an ordinary image, so it
        // goes down the same path. Path.GetExtension("x.fit.fz") is ".fz", so matching that
        // alone covers .fit.fz and .fits.fz too.
        if (ext is ".fits" or ".fit" or ".fts" or ".fz")
        {
            return TryReadFitsFile(fileName, out image, out wcs);
        }
        if (ext is ".png" or ".jpg" or ".jpeg" or ".jxr" or ".wdp" or ".exr" or ".jxl")
        {
            return TryReadViaCodecs(fileName, out image);
        }

        image = null;
        return false;
    }

    /// <summary>Decode a Canon CR2 or CR3 via FC.SDK.Raw into a 1-channel
    /// float Bayer mosaic with as-shot WB applied and CameraToSrgbMatrix
    /// populated. Caller should debayer + apply the matrix downstream
    /// (drizzle-friendly, the mosaic is preserved for stacking workflows
    /// that need it).</summary>
    private static bool TryReadCanonRaw(string fileName, [NotNullWhen(true)] out Image? image)
    {
        try
        {
            var raw = CanonRaw.Open(fileName);

            // Only RGGB CFA is currently mapped to SensorType. Other Canon
            // patterns would need BayerOffset mapping (RGGB+offset encodes
            // BGGR/GBRG/GRBG in TianWen's convention). Fall through to
            // Magick.NET for now.
            if (raw.CfaPattern != CanonCfaPattern.Rggb)
            {
                image = null;
                return false;
            }

            // Fused ushort -> float + black-subtract + per-CFA-cell WB.
            var mosaic = CanonRaw.PreprocessMosaic(raw);

            // Reshape flat float[] to channel-planar [height, width].
            var channel = new float[raw.Height, raw.Width];
            for (var y = 0; y < raw.Height; y++)
            for (var x = 0; x < raw.Width; x++)
                channel[y, x] = mosaic[y * raw.Width + x];

            // Data-driven MaxValue: walk the post-WB mosaic to find the
            // actual peak. For daylight WB it tops out around 2.0 (R channel);
            // narrow-band or extreme WB can push higher. The downstream
            // stretch pipeline divides by MaxValue, so accuracy here keeps
            // [0, 1] normalisation correct.
            var max = 0f;
            foreach (var v in mosaic) if (v > max) max = v;
            if (max < 1f) max = 1f; // defensive: never below the natural 1.0 ceiling

            // Camera -> sRGB matrix: spectral (SASP) first when the database is
            // already loaded (lazy: don't force-load it here, astro startup
            // pre-loads via SPCC), dcraw factory table as fallback, null if
            // neither has the model.
            float[]? matrix = null;
            if (FilterCurveDatabase.IsLoaded
                && FilterCurveDatabase.TryComputeCameraToSrgbMatrix(raw.Exif?.Model ?? "", out var spectral))
            {
                matrix = spectral;
            }
            else if (CanonCameraProfiles.ResolveProfile(raw.Exif?.Model)?.ComputeRgbCam() is { } dcraw)
            {
                matrix = dcraw;
            }

            var meta = BuildCanonRawImageMeta(raw, matrix);
            image = new Image([channel], BitDepth.Float32,
                maxValue: max, minValue: 0f, pedestal: 0f, meta);
            return true;
        }
        catch (Exception)
        {
            image = null;
            return false;
        }
    }

    /// <summary>Construct an <see cref="ImageMeta"/> from a decoded CR2's
    /// EXIF + the resolved camera matrix. Fields not in the CR2's EXIF
    /// (telescope, observatory site, target coords) stay as their sentinel
    /// "unknown" values: the caller can populate via <c>meta with { ... }</c>
    /// when those facts are known from session context.</summary>
    private static ImageMeta BuildCanonRawImageMeta(CanonRawFile raw, float[]? cameraToSrgb)
    {
        var captureTime = raw.Exif?.CaptureTime is { } ct
            ? new DateTimeOffset(DateTime.SpecifyKind(ct, DateTimeKind.Utc), TimeSpan.Zero)
            : DateTimeOffset.UnixEpoch;
        var exposure = raw.Exif?.ExposureTime is { } et && et.Denominator != 0
            ? TimeSpan.FromSeconds((double)et.Numerator / et.Denominator)
            : TimeSpan.Zero;

        return new ImageMeta(
            Instrument: raw.Exif?.Model ?? "Unknown Canon",
            ExposureStartTime: captureTime,
            ExposureDuration: exposure,
            FrameType: FrameType.Light,
            Telescope: "",
            PixelSizeX: 0, PixelSizeY: 0,
            FocalLength: -1, FocusPos: -1,
            Filter: Filter.Unknown,
            BinX: 1, BinY: 1,
            CCDTemperature: float.NaN,
            SensorType: SensorType.RGGB,
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: float.NaN, Longitude: float.NaN
        ) { CameraToSrgbMatrix = cameraToSrgb };
    }

    /// <summary>
    /// Internal rather than private so the allocation test can call it directly: the point of that test
    /// is what this method allocates, and going through the extension-dispatching entry point would put
    /// unrelated work inside the measured window.
    /// </summary>
    internal static unsafe bool TryReadTiff(string fileName, [NotNullWhen(true)] out Image? image)
    {
        try
        {
            // MEMORY-MAPPED, not File.ReadAllBytes. With the strip-streaming decode below, the file
            // buffer became the largest single allocation on this path: an UNCOMPRESSED TIFF is
            // raster-sized on disk (354 MB for the 13228x9354 page in
            // docs/plans/viewer-memory-footprint.md), so ReadAllBytes was handing back a byte[] as big
            // as the raster the streaming decode had just eliminated -- one term traded for another.
            // A mapping has no such array: the pages are file-backed, so they need no managed heap, no
            // LOH allocation, and can be evicted under pressure instead of swapped.
            //
            // It also completes the zero-copy path. SharpAstro.Tiff hands an uncompressed, unpredicted
            // strip over as a slice of its INPUT, so with the input being the mapping those samples are
            // converted straight out of the file with no intermediate copy anywhere.
            var info = new FileInfo(fileName);

            // Zero length has nothing to map (CreateFromFile rejects a 0-capacity mapping), and past
            // int.MaxValue a span cannot address it -- academic for this reader, which does not support
            // BigTIFF and so cannot follow 64-bit offsets anyway, but a wrong answer is worse than a
            // refusal.
            if (info.Length is 0 or > int.MaxValue)
            {
                image = null;
                return false;
            }

            // FileShare.Read to match what ReadAllBytes did, so a file another process holds open for
            // writing is refused exactly as before rather than newly succeeding on a half-written file.
            using var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var mapping = MemoryMappedFile.CreateFromFile(stream, mapName: null, capacity: 0,
                MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            using var view = mapping.CreateViewAccessor(0, info.Length, MemoryMappedFileAccess.Read);

            byte* origin = null;
            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref origin);

                // PointerOffset, because a view is aligned to an allocation granularity boundary and the
                // pointer is to that boundary, not to the requested offset. It is 0 for offset 0 on every
                // platform we run, but reading it is free and assuming it is not.
                var bytes = new ReadOnlySpan<byte>(origin + view.PointerOffset, (int)info.Length);

                // ReadInto, not Read: Read returns the page as one assembled raster, so converting it here
                // means the raster and these float planes are BOTH fully resident. For a 13228x9354 RGB
                // page that is 354 MiB of intermediate whose only purpose is to be read once, left-to-right
                // -- see docs/plans/viewer-memory-footprint.md (M2). The sink converts each strip as it
                // arrives and the raster never exists.
                var sink = new TiffChannelSink();
                var doc = TiffReader.ReadInto(bytes, ref sink);
                if (doc.Pages.Count == 0 || sink.Channels is not { } channels)
                {
                    // No pages, or the sink declined the first one (an unsupported photometric). Either way
                    // the caller is told we could not read it, which is the same answer as before.
                    image = null;
                    return false;
                }

                var page = doc.Pages[0];
                var width = page.Width;
                var height = page.Height;
                var isSeparated = sink.IsSeparated;
                var bitDepth = sink.BitDepth;

                // The photometric interpretation decides what the samples MEAN, and ignoring it is not a
                // graceful degradation -- it renders a confidently wrong picture. A Separated (CMYK)
                // print export read as RGB comes out as a colour-shifted NEGATIVE, because a high CMYK
                // value is more ink, i.e. darker. That is worse than refusing the file, since nothing on
                // screen says the interpretation was guessed.
                if (page.Photometric == TiffPhotometric.MinIsWhite)
                {
                    // Zero means WHITE for this photometric (scanners and fax), so the stored samples are
                    // a negative of the intensity every consumer downstream assumes.
                    InvertSamplesInPlace(channels);
                }
                else if (isSeparated)
                {
                    channels = ConvertSeparatedToRgb(channels, width, height);
                }

                var exif = ExifReader.FromTiff(bytes);
                var imageMeta = BuildImageMetaFromExif(exif, page.FileIsLittleEndian);

                // Values are now in [0, 1] (DecodeTiffPixels normalises by sample-format max).
                image = new Image(channels, bitDepth, 1.0f, 0f, 0f, imageMeta, samplesAreUnitReferred: true);
                return true;
            }
            finally
            {
                if (origin is not null)
                {
                    // Paired with AcquirePointer. Without it the handle keeps a reference count and the
                    // mapping is never released, which is a leak that only shows up as a file staying
                    // locked -- so it presents as "cannot delete that file" long after the decode.
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
        catch
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Decode a raster the <c>SharpAstro.Codecs</c> facade recognises (PNG / JPEG /
    /// JPEG XR / OpenEXR / JPEG XL) into a mono or RGB float <see cref="Image"/>; the read
    /// counterpart to tianwen's own writers (PNG previews, EXR/JXR HDR masters). TIFF is
    /// deliberately routed through <see cref="TryReadTiff"/> instead, which recovers EXIF into
    /// <see cref="ImageMeta"/> that the facade's decoded raster does not carry.
    /// </summary>
    private static bool TryReadViaCodecs(string fileName, [NotNullWhen(true)] out Image? image)
    {
        try
        {
            var bytes = File.ReadAllBytes(fileName);
            return TryDecodeRaster(bytes, out image);
        }
        catch
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Decode an in-memory raster buffer (PNG / JPEG / JPEG XR / OpenEXR / JPEG XL) the
    /// <c>SharpAstro.Codecs</c> facade recognises into a mono or RGB float <see cref="Image"/>,
    /// normalised to [0, 1]. The byte-buffer core of <see cref="TryReadViaCodecs"/> (which reads a
    /// file then delegates here). The Canon Live View path decodes each EVF JPEG frame straight from
    /// the SDK <c>byte[]</c> through this: a per-frame temp-file round-trip would dominate a
    /// 15-30 fps stream. A camera-processed EVF JPEG is demosaiced RGB, so it decodes to a
    /// 3-channel <see cref="Image"/> the live-stack pipeline consumes as a colour master.
    /// <para>
    /// Public because the same "bytes off a wire, no temp file" need exists outside this assembly:
    /// <c>RemoteSessionMirror</c> decodes the node's JPEG preview frames through it
    /// (docs/plans/remote-profile.md). Both callers are streams, where a per-frame file round-trip
    /// would dominate the cost.
    /// </para>
    /// </summary>
    public static bool TryDecodeRaster(byte[] bytes, [NotNullWhen(true)] out Image? image)
    {
        try
        {
            if (!ImageCodecs.TryDecode(bytes, out var decoded))
            {
                image = null;
                return false;
            }

            var width = decoded.Width;
            var height = decoded.Height;
            // Image carries mono (1) or RGB (3); drop alpha / gray-alpha's extra channel,
            // matching TryReadTiff's R/G/B-only extraction.
            var outChannels = decoded.Channels >= 3 ? 3 : 1;

            // ToFloats widens to interleaved RGBA float32: integer samples normalise to [0, 1]
            // (endpoints exact), Float32 samples pass through verbatim, gray broadcasts across
            // R/G/B. Container-only: values keep decoded.ColorEncoding's meaning (a PQ/HLG
            // raster stays non-linear), which matches the [0, 1] float convention TryReadTiff
            // already trusts. A tone / linearisation pass for non-sRGB HDR inputs is deferred.
            var rgba = decoded.ToFloats();

            var channels = CreateChannelData(outChannels, height, width);
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var pix = (row + x) * 4; // interleaved RGBA stride
                    for (var c = 0; c < outChannels; c++)
                    {
                        channels[c][y, x] = rgba[pix + c];
                    }
                }
            }

            var bitDepth = decoded.SampleFormat switch
            {
                CodecSampleFormat.Float32 => BitDepth.Float32,
                CodecSampleFormat.UInt16 => BitDepth.Int16,
                _ => BitDepth.Int8,
            };

            // The facade's decoded raster carries no structured EXIF, so build a default
            // "generic light frame, unknown sensor" ImageMeta (null EXIF => NaN pixel size,
            // empty instrument). Values are already [0, 1] for integer sources and follow the
            // [0, 1] convention for float, so maxValue = 1 mirrors TryReadTiff.
            var meta = BuildImageMetaFromExif(null, fileIsLittleEndian: true);
            image = new Image(channels, bitDepth, 1.0f, 0f, 0f, meta, samplesAreUnitReferred: true);
            return true;
        }
        catch
        {
            image = null;
            return false;
        }
    }

    /// <summary>
    /// Flip sample polarity for <see cref="TiffPhotometric.MinIsWhite"/>, where 0 means white.
    /// Samples are already normalised to [0, 1] by the decode, so the inversion is 1 - v.
    /// </summary>
    private static void InvertSamplesInPlace(float[][,] channels)
    {
        foreach (var plane in channels)
        {
            var flat = MemoryMarshal.CreateSpan(ref plane[0, 0], plane.Length);
            for (var i = 0; i < flat.Length; i++)
            {
                flat[i] = 1f - flat[i];
            }
        }
    }

    /// <summary>
    /// Naive Separated (CMYK) -> RGB: <c>R = (1-C)(1-K)</c> and so on, with all four samples already
    /// normalised to [0, 1] and 0 meaning no ink (the TIFF 6.0 Separated convention).
    ///
    /// Deliberately naive, and the limitation is worth stating: an accurate conversion needs the
    /// embedded ICC output profile, because CMYK is device-dependent in a way RGB is not -- these
    /// files do carry one. What this buys is the part that was unambiguously WRONG rather than merely
    /// imprecise: the polarity. A print export now reads as a positive with roughly right hues
    /// instead of a negative. Anyone colour-managing a proof should not be doing it here.
    /// </summary>
    private static float[][,] ConvertSeparatedToRgb(float[][,] cmyk, int width, int height)
    {
        var rgb = CreateChannelData(3, height, width);
        var c = cmyk[0];
        var m = cmyk[1];
        var y = cmyk[2];
        var k = cmyk[3];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var ink = 1f - k[row, col];
                rgb[0][row, col] = (1f - c[row, col]) * ink;
                rgb[1][row, col] = (1f - m[row, col]) * ink;
                rgb[2][row, col] = (1f - y[row, col]) * ink;
            }
        }
        return rgb;
    }

    /// <summary>
    /// Converts a TIFF page into float planes STRIP BY STRIP, so the assembled raster never exists.
    /// </summary>
    /// <remarks>
    /// <para>A mutable struct, passed by <c>ref</c> to <see cref="TiffReader.ReadInto{TSink}"/> so it is
    /// neither boxed nor allocated -- the point of this path is to stop materialising things.</para>
    ///
    /// <para>It also owns the decisions that used to sit in <c>TryReadTiff</c> (which photometrics are
    /// supported, how many channels to decode versus emit, the bit depth), because
    /// <see cref="ITiffStripSink.BeginPage"/> is where they can be made -- before a single pixel is
    /// decoded -- and refusing there means an unsupported file costs no decode at all.</para>
    /// </remarks>
    private struct TiffChannelSink : ITiffStripSink
    {
        public float[][,]? Channels;
        public bool IsSeparated;
        public BitDepth BitDepth;
        private int _width;
        private int _srcChannels;
        private int _decodeChannels;
        private int _bitsPerSample;
        private bool _isFloat;

        public bool BeginPage(int pageIndex, TiffPage page)
        {
            // Image carries ONE page, and page 0 is the one TryReadTiff has always used. Declining the
            // rest is not merely tidy: their strips are then never decoded, so a multi-page TIFF costs
            // what its first page costs.
            if (pageIndex != 0)
            {
                return false;
            }

            if (page.Photometric is not (TiffPhotometric.Rgb or TiffPhotometric.MinIsBlack
                or TiffPhotometric.MinIsWhite or TiffPhotometric.Cmyk))
            {
                // Palette / YCbCr / CIELab need a colour transform this reader does not carry. Refuse
                // rather than reinterpret; the caller reports that it could not read the file. Channels
                // stays null, which is how TryReadTiff sees the refusal.
                return false;
            }

            // CMYK is the one case where the output channel count is not the input's: K is needed to
            // convert and then discarded, so decode 4 and emit 3.
            IsSeparated = page.Photometric == TiffPhotometric.Cmyk && page.SamplesPerPixel >= 4;
            // Drop alpha / extras: Image carries mono (1) or RGB (3); the prior Magick path
            // also extracted only R/G/B from interleaved RGBA strides.
            _decodeChannels = IsSeparated ? 4 : page.SamplesPerPixel >= 3 ? 3 : 1;
            BitDepth = (page.SampleFormat, page.BitsPerSample) switch
            {
                (TiffSampleFormat.IeeeFloat, _) => BitDepth.Float32,
                (_, <= 8) => BitDepth.Int8,
                (_, <= 16) => BitDepth.Int16,
                _ => BitDepth.Float32,
            };

            _width = page.Width;
            _srcChannels = page.SamplesPerPixel;
            _bitsPerSample = page.BitsPerSample;
            _isFloat = page.SampleFormat == TiffSampleFormat.IeeeFloat;

            // The one allocation this path makes, and the destination the strips write straight into.
            Channels = CreateChannelData(_decodeChannels, page.Height, page.Width);
            return true;
        }

        public void Strip(int pageIndex, int firstRow, int rowCount, ReadOnlySpan<byte> samples)
        {
            if (Channels is not { } channels)
            {
                return;
            }

            // Rows the strip actually delivered, which is not always rowCount: a truncated file gives a
            // short final strip, and converting past it would read the neighbouring row's samples as
            // this row's. Trusting the span's length is what makes a partial file decode to a partial
            // image rather than to a garbled one.
            var bytesPerRow = _width * _srcChannels * (_bitsPerSample / 8);
            var rows = bytesPerRow > 0 ? Math.Min(rowCount, samples.Length / bytesPerRow) : 0;
            if (rows <= 0)
            {
                return;
            }

            ConvertTiffStrip(samples, channels, _decodeChannels, _width, _srcChannels, _bitsPerSample,
                _isFloat, firstRow, rows);
        }
    }

    /// <summary>
    /// One strip's interleaved samples into the float planes, normalised to [0, 1] by sample-format
    /// max. <paramref name="firstRow"/> is where the strip sits in the image, which is the only thing
    /// that changed when this stopped being a whole-raster pass.
    /// </summary>
    private static void ConvertTiffStrip(ReadOnlySpan<byte> samples, float[][,] channels, int outChannels,
        int width, int srcChannels, int bps, bool isFloat, int firstRow, int rowCount)
    {
        if (isFloat && bps == 32)
        {
            // Float TIFFs follow the [0, 1] convention regardless of writer (Magick.NET via
            // SMin/SMax, scientific tools as literal scene-linear values). Store as-is.
            var floats = MemoryMarshal.Cast<byte, float>(samples);
            for (var localRow = 0; localRow < rowCount; localRow++)
            {
                var row = localRow * width;
                var y = firstRow + localRow;
                for (var x = 0; x < width; x++)
                {
                    var pix = (row + x) * srcChannels;
                    for (var c = 0; c < outChannels; c++)
                    {
                        channels[c][y, x] = floats[pix + c];
                    }
                }
            }
        }
        else if (!isFloat && bps == 16)
        {
            var shorts = MemoryMarshal.Cast<byte, ushort>(samples);
            const float inv = 1f / 65535f;
            for (var localRow = 0; localRow < rowCount; localRow++)
            {
                var row = localRow * width;
                var y = firstRow + localRow;
                for (var x = 0; x < width; x++)
                {
                    var pix = (row + x) * srcChannels;
                    for (var c = 0; c < outChannels; c++)
                    {
                        channels[c][y, x] = shorts[pix + c] * inv;
                    }
                }
            }
        }
        else if (!isFloat && bps == 8)
        {
            const float inv = 1f / 255f;
            for (var localRow = 0; localRow < rowCount; localRow++)
            {
                var row = localRow * width;
                var y = firstRow + localRow;
                for (var x = 0; x < width; x++)
                {
                    var pix = (row + x) * srcChannels;
                    for (var c = 0; c < outChannels; c++)
                    {
                        channels[c][y, x] = samples[pix + c] * inv;
                    }
                }
            }
        }
        else
        {
            throw new NotSupportedException(
                $"TIFF BitsPerSample={bps} isFloat={isFloat} not supported by the SharpAstro.Tiff path");
        }
    }

    private static ImageMeta BuildImageMetaFromExif(ExifMetadata? exif, bool fileIsLittleEndian)
    {
        var instrument = exif?.Model ?? "";

        var exposureDuration = exif?.ExposureTime is { Numerator: > 0, Denominator: > 0 } et
            ? TimeSpan.FromSeconds((double)et.Numerator / et.Denominator)
            : TimeSpan.Zero;

        var exposureStartTime = exif?.CaptureTime is { } dt
            ? new DateTimeOffset(dt, TimeSpan.Zero)
            : DateTimeOffset.MinValue;

        var focalLength = exif?.FocalLength is { Numerator: > 0, Denominator: > 0 } fl
            ? (int)(fl.Numerator / fl.Denominator)
            : -1;

        // Optional: pixel size from XResolution/YResolution tags when ResolutionUnit==3 (cm).
        // Rarely populated: most cameras write ResolutionUnit=2 (inch) which we don't try to
        // interpret as pixel size (would conflate "DPI metadata" with sensor pitch). Reads
        // straight from raw IFD bytes since the strongly-typed projection doesn't carry these.
        var (pixelSizeX, pixelSizeY) = ReadPixelSizeMicrons(exif, fileIsLittleEndian);

        return new ImageMeta(
            instrument,
            exposureStartTime,
            exposureDuration,
            FrameType.Light,
            Telescope: "",
            pixelSizeX,
            pixelSizeY,
            focalLength,
            FocusPos: -1,
            Filter.None,
            BinX: 1,
            BinY: 1,
            CCDTemperature: float.NaN,
            SensorType.Unknown,
            BayerOffsetX: 0,
            BayerOffsetY: 0,
            RowOrder.TopDown,
            Latitude: float.NaN,
            Longitude: float.NaN,
            ObjectName: "");
    }

    private static (float X, float Y) ReadPixelSizeMicrons(ExifMetadata? exif, bool fileIsLittleEndian)
    {
        var pixelSizeX = float.NaN;
        var pixelSizeY = float.NaN;
        if (exif?.RawTags is not { } raw) return (pixelSizeX, pixelSizeY);

        // ResolutionUnit: 1=none, 2=inch, 3=cm. We only convert when cm (preserves the prior
        // Magick-path behaviour: see Image.Import.cs commit history pre-Phase-4).
        if (!raw.TryGetValue(0x0128, out var unitVal)
            || unitVal.Type != TiffFieldType.Short
            || unitVal.Bytes.Length < 2)
        {
            return (pixelSizeX, pixelSizeY);
        }
        var resUnit = ReadUInt16(unitVal.Bytes, fileIsLittleEndian);
        if (resUnit != 3) return (pixelSizeX, pixelSizeY);

        pixelSizeX = TryReadResolutionMicrons(raw, 0x011A, fileIsLittleEndian);
        pixelSizeY = TryReadResolutionMicrons(raw, 0x011B, fileIsLittleEndian);
        return (pixelSizeX, pixelSizeY);
    }

    private static float TryReadResolutionMicrons(System.Collections.Generic.IReadOnlyDictionary<ushort, SharpAstro.Exif.ExifTagValue> raw, ushort tag, bool fileIsLittleEndian)
    {
        if (!raw.TryGetValue(tag, out var val)
            || val.Type != TiffFieldType.Rational
            || val.Bytes.Length < 8)
        {
            return float.NaN;
        }
        var num = ReadUInt32(val.Bytes.AsSpan(0, 4), fileIsLittleEndian);
        var den = ReadUInt32(val.Bytes.AsSpan(4, 4), fileIsLittleEndian);
        // Rational is pixels/cm → microns/pixel = 10000 / (num/den) = 10000 * den / num.
        return num > 0 ? 10000f * den / num : float.NaN;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
        => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
            : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    /// <summary>
    /// Detects whether an image that is already normalized to [0,1] appears to be pre-stretched
    /// (i.e., the pixel values have already been through a screen transfer function).
    /// A linear unstretched astro image has most of its pixels concentrated near the black point,
    /// with median typically below 0.1. A stretched image has median much higher.
    /// </summary>
    public static bool DetectPreStretched(Image image)
    {
        // 8 bits settles it without measuring anything: 255 levels cannot hold an astronomical
        // dynamic range, so the data has already been through a transfer function. Checked FIRST
        // because it is the reliable half of this question -- the median test below is a heuristic
        // over pixel statistics and has a known blind spot (a planetary frame is mostly empty sky
        // whether stretched or not, so its median reads low either way).
        if (image.BitDepth.CarriesDisplayDataOnly)
        {
            return true;
        }

        var span = image.GetChannelSpan(0);

        const int sampleCount = 1024;
        if (span.Length < sampleCount)
        {
            return false;
        }

        // Slice from the middle of the image to avoid edge artifacts
        var mid = span.Length / 2;
        var region = span.Slice(mid - sampleCount / 2, sampleCount);

        Span<float> samples = stackalloc float[sampleCount];
        region.CopyTo(samples);
        samples.Sort();
        var median = samples[sampleCount / 2];

        // In a typical unstretched astro image, the median is well below 0.15
        // (most pixels are dark sky). A stretched image has median > 0.2 typically.
        return median > 0.2f;
    }
}
