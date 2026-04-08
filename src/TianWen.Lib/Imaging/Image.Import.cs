using ImageMagick;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace TianWen.Lib.Imaging;

public partial class Image
{
    /// <summary>
    /// Reads an image file (TIFF, CR2, CR3, etc.) using Magick.NET and returns an <see cref="Image"/> with float channel data.
    /// Extracts available EXIF metadata into <see cref="ImageMeta"/>.
    /// </summary>
    public static bool TryReadImageFile(string fileName, [NotNullWhen(true)] out Image? image)
    {
        try
        {
            // CR2/CR3 require an explicit format hint — otherwise ImageMagick interprets them as TIFF
            var settings = Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".cr2" => new MagickReadSettings { Format = MagickFormat.Cr2 },
                ".cr3" => new MagickReadSettings { Format = MagickFormat.Cr3 },
                _ => null
            };
            using var magick = settings is not null ? new MagickImage(fileName, settings) : new MagickImage(fileName);
            var width = (int)magick.Width;
            var height = (int)magick.Height;
            var depth = (int)magick.Depth;

            // Determine bit depth and max value from the source image
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
                ?? throw new InvalidOperationException("Failed to read pixel data from TIFF file.");

            if (isColor)
            {
                // RGB interleaved: R, G, B[, A] per pixel
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
                // Grayscale: 1 value[+alpha] per pixel
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

            // Extract EXIF metadata where available
            var profile = magick.GetExifProfile();
            var instrument = GetExifString(profile, ExifTag.Model) ?? "";
            var telescope = "";
            var objectName = "";
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

                // Try to get pixel size from resolution
                var xRes = profile.GetValue(ExifTag.XResolution);
                var yRes = profile.GetValue(ExifTag.YResolution);
                if (xRes?.Value is { Numerator: > 0 } xr)
                {
                    // Resolution in pixels/cm → pixel size in µm
                    var resUnit = profile.GetValue(ExifTag.ResolutionUnit);
                    if (resUnit?.Value is 3) // centimeters
                    {
                        pixelSizeX = 10000f * xr.Denominator / xr.Numerator; // cm to µm
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
                telescope,
                pixelSizeX,
                pixelSizeY,
                focalLength,
                -1, // focusPos
                Filter.None,
                1, // binX
                1, // binY
                float.NaN, // ccdTemp
                SensorType.Unknown,
                0, 0, // bayer offsets
                RowOrder.TopDown,
                float.NaN, // latitude
                float.NaN, // longitude
                objectName
            );

            // Values are already normalized to [0, 1] via invQuantum
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
