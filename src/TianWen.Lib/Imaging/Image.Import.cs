using FC.SDK.Raw;
using SharpAstro.Exif;
using SharpAstro.Tiff;
using ImageMagick;
using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Reads an image file (TIFF, CR2, CR3, etc.) and returns an <see cref="Image"/> with float
    /// channel data. TIFF (.tif / .tiff) goes through DIR.Lib's TiffReader; CR2 (.cr2) and
    /// CR3 (.cr3) both go through FC.SDK.Raw's pure-managed Canon raw decoder. Other formats
    /// fall through to Magick.NET. CR2 and CR3 imports both populate
    /// <see cref="ImageMeta.CameraToSrgbMatrix"/> via the spectral (SASP) or dcraw factory
    /// lookup; null when neither matches.
    ///
    /// Pixel values are normalised to [0, 1] regardless of source bit depth. EXIF metadata is
    /// extracted into <see cref="ImageMeta"/> where present.
    /// </summary>
    public static bool TryReadImageFile(string fileName, [NotNullWhen(true)] out Image? image)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        if (ext is ".tif" or ".tiff")
        {
            // DIR.Lib path. Falls through to Magick.NET on unsupported compression / tiled
            // layout / other format variants the DIR.Lib reader doesn't yet handle.
            if (TryReadTiffViaDirLib(fileName, out image))
                return true;
        }
        else if (ext is ".cr2" or ".cr3")
        {
            // FC.SDK.Raw pure-managed Canon decoder. Internal CanonRaw.Open
            // dispatches on file signature: TIFF magic -> Cr2Decoder, ISO BMFF
            // ftyp -> Cr3Decoder. Both produce the same CanonRawFile shape so
            // the downstream preprocess / matrix / ImageMeta wiring is shared.
            // Falls through to Magick.NET on decode failure (corrupt file,
            // unsupported variant) — same safety net as the TIFF DIR.Lib path.
            if (TryReadCanonRaw(fileName, out image))
                return true;
        }

        return TryReadViaMagick(fileName, ext, out image);
    }

    /// <summary>Decode a Canon CR2 or CR3 via FC.SDK.Raw into a 1-channel
    /// float Bayer mosaic with as-shot WB applied and CameraToSrgbMatrix
    /// populated. Caller should debayer + apply the matrix downstream
    /// (drizzle-friendly — the mosaic is preserved for stacking workflows
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
            // already loaded (lazy: don't force-load it here — astro startup
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
    /// "unknown" values — the caller can populate via <c>meta with { ... }</c>
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

    private static bool TryReadTiffViaDirLib(string fileName, [NotNullWhen(true)] out Image? image)
    {
        try
        {
            var bytes = File.ReadAllBytes(fileName);
            var doc = TiffReader.Read(bytes);
            if (doc.Pages.Count == 0)
            {
                image = null;
                return false;
            }

            var page = doc.Pages[0];
            var width = page.Width;
            var height = page.Height;
            // Drop alpha / extras — Image carries mono (1) or RGB (3); the prior Magick path
            // also extracted only R/G/B from interleaved RGBA strides.
            var outChannels = page.SamplesPerPixel >= 3 ? 3 : 1;
            var bitDepth = (page.SampleFormat, page.BitsPerSample) switch
            {
                (TiffSampleFormat.IeeeFloat, _) => BitDepth.Float32,
                (_, <= 8) => BitDepth.Int8,
                (_, <= 16) => BitDepth.Int16,
                _ => BitDepth.Float32,
            };

            var channels = CreateChannelData(outChannels, height, width);
            DecodeTiffPixels(page, channels, outChannels);

            var exif = ExifReader.FromTiff(bytes);
            var imageMeta = BuildImageMetaFromExif(exif, page.FileIsLittleEndian);

            // Values are now in [0, 1] (DecodeTiffPixels normalises by sample-format max).
            image = new Image(channels, bitDepth, 1.0f, 0f, 0f, imageMeta);
            return true;
        }
        catch
        {
            image = null;
            return false;
        }
    }

    private static void DecodeTiffPixels(TiffPage page, float[][,] channels, int outChannels)
    {
        var width = page.Width;
        var height = page.Height;
        var srcChannels = page.SamplesPerPixel;
        var bps = page.BitsPerSample;
        var isFloat = page.SampleFormat == TiffSampleFormat.IeeeFloat;

        if (isFloat && bps == 32)
        {
            // Float TIFFs follow the [0, 1] convention regardless of writer (Magick.NET via
            // SMin/SMax, scientific tools as literal scene-linear values). Store as-is.
            var floats = MemoryMarshal.Cast<byte, float>(page.Pixels.AsSpan());
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
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
            var shorts = MemoryMarshal.Cast<byte, ushort>(page.Pixels.AsSpan());
            const float inv = 1f / 65535f;
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
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
            for (var y = 0; y < height; y++)
            {
                var row = y * width;
                for (var x = 0; x < width; x++)
                {
                    var pix = (row + x) * srcChannels;
                    for (var c = 0; c < outChannels; c++)
                    {
                        channels[c][y, x] = page.Pixels[pix + c] * inv;
                    }
                }
            }
        }
        else
        {
            throw new NotSupportedException(
                $"TIFF BitsPerSample={bps} SampleFormat={page.SampleFormat} not supported by DIR.Lib path");
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
        // Rarely populated — most cameras write ResolutionUnit=2 (inch) which we don't try to
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
        // Magick-path behaviour — see Image.Import.cs commit history pre-Phase-4).
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

    private static bool TryReadViaMagick(string fileName, string ext, [NotNullWhen(true)] out Image? image)
    {
        try
        {
            // CR2 and CR3 are both handled by FC.SDK.Raw upstream in TryReadImageFile and
            // never reach here unless the FC.SDK.Raw decoder threw (corrupt file, unsupported
            // variant — e.g. encType=3 / encType=1 CR3 or a non-RGGB CFA pattern). The
            // explicit format hint keeps Magick.NET honest when it falls through anyway,
            // since otherwise ImageMagick interprets .cr3 as TIFF.
            var settings = ext switch
            {
                ".cr2" => new MagickReadSettings { Format = MagickFormat.Cr2 },
                ".cr3" => new MagickReadSettings { Format = MagickFormat.Cr3 },
                _ => null
            };
            using var magick = settings is not null ? new MagickImage(fileName, settings) : new MagickImage(fileName);
            var width = (int)magick.Width;
            var height = (int)magick.Height;
            var depth = (int)magick.Depth;

            var bitDepth = depth switch
            {
                <= 8 => BitDepth.Int8,
                <= 16 => BitDepth.Int16,
                _ => BitDepth.Float32,
            };

            var isColor = magick.ColorSpace is not (ColorSpace.Gray or ColorSpace.LinearGray);
            var channelCount = isColor ? 3 : 1;
            var channels = CreateChannelData(channelCount, height, width);

            // Quantum.Max is the maximum value for the current Magick.NET quantum depth (Q16 = 65535)
            var invQuantum = 1f / Quantum.Max;

            using var pixels = magick.GetPixelsUnsafe();
            var area = pixels.GetArea(0, 0, (uint)width, (uint)height)
                ?? throw new InvalidOperationException("Failed to read pixel data from image file.");

            if (isColor)
            {
                var stride = magick.HasAlpha ? 4 : 3;
                for (var y = 0; y < height; y++)
                {
                    var rowOffset = y * width * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var pixOffset = rowOffset + x * stride;
                        channels[0][y, x] = area[pixOffset] * invQuantum;
                        channels[1][y, x] = area[pixOffset + 1] * invQuantum;
                        channels[2][y, x] = area[pixOffset + 2] * invQuantum;
                    }
                }
            }
            else
            {
                var stride = magick.HasAlpha ? 2 : 1;
                for (var y = 0; y < height; y++)
                {
                    var rowOffset = y * width * stride;
                    for (var x = 0; x < width; x++)
                    {
                        channels[0][y, x] = area[rowOffset + x * stride] * invQuantum;
                    }
                }
            }

            // Extract EXIF metadata where available (mirror of the DIR.Lib path).
            var profile = magick.GetExifProfile();
            var instrument = GetExifString(profile, ExifTag.Model) ?? "";
            var exposureDuration = TimeSpan.Zero;
            var exposureStartTime = DateTimeOffset.MinValue;
            var focalLength = -1;
            var pixelSizeX = float.NaN;
            var pixelSizeY = float.NaN;

            if (profile is not null)
            {
                var expTimeValue = profile.GetValue(ExifTag.ExposureTime);
                if (expTimeValue?.Value is { } expRational)
                {
                    exposureDuration = TimeSpan.FromSeconds((double)expRational.Numerator / expRational.Denominator);
                }

                var dateValue = profile.GetValue(ExifTag.DateTimeOriginal);
                if (dateValue?.Value is { } dateStr && DateTime.TryParse(dateStr, out var dt))
                {
                    exposureStartTime = new DateTimeOffset(dt, TimeSpan.Zero);
                }

                var focalValue = profile.GetValue(ExifTag.FocalLength);
                if (focalValue?.Value is { } focalRational)
                {
                    focalLength = (int)(focalRational.Numerator / focalRational.Denominator);
                }

                var xRes = profile.GetValue(ExifTag.XResolution);
                var yRes = profile.GetValue(ExifTag.YResolution);
                if (xRes?.Value is { Numerator: > 0 } xr)
                {
                    var resUnit = profile.GetValue(ExifTag.ResolutionUnit);
                    if (resUnit?.Value is 3) // centimeters
                    {
                        pixelSizeX = 10000f * xr.Denominator / xr.Numerator;
                    }
                }
                if (yRes?.Value is { Numerator: > 0 } yr)
                {
                    var resUnit = profile.GetValue(ExifTag.ResolutionUnit);
                    if (resUnit?.Value is 3)
                    {
                        pixelSizeY = 10000f * yr.Denominator / yr.Numerator;
                    }
                }
            }

            var imageMeta = new ImageMeta(
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

            image = new Image(channels, bitDepth, 1.0f, 0f, 0f, imageMeta);
            return true;
        }
        catch
        {
            image = null;
            return false;
        }
    }

    private static string? GetExifString(IExifProfile? profile, ExifTag<string> tag)
    {
        if (profile is null) return null;
        var value = profile.GetValue(tag);
        return value?.Value is { Length: > 0 } s ? s.Trim('\0', ' ') : null;
    }

    /// <summary>
    /// Detects whether an image that is already normalized to [0,1] appears to be pre-stretched
    /// (i.e., the pixel values have already been through a screen transfer function).
    /// A linear unstretched astro image has most of its pixels concentrated near the black point,
    /// with median typically below 0.1. A stretched image has median much higher.
    /// </summary>
    public static bool DetectPreStretched(Image image)
    {
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
