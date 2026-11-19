using System;
using System.Collections.Generic;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Geometry;
using TianWen.Lib.Imaging.Enhancement;

namespace TianWen.Lib.Imaging.Stacking;

/// <summary>What <see cref="MasterCrop"/> did: written, or refused with the reason in words.</summary>
/// <param name="Succeeded">True when the cropped master was written.</param>
/// <param name="Message">Empty on success; on a refusal, what is wrong with the request, naming the
/// rectangle and the image size.</param>
/// <param name="Width">Width of the written crop, 0 on a refusal.</param>
/// <param name="Height">Height of the written crop, 0 on a refusal.</param>
public readonly record struct MasterCropResult(bool Succeeded, string Message, int Width, int Height);

/// <summary>
/// Crops a FITS master to a caller's rectangle. Unlike <see cref="Image.Crop(PixelRect)"/>, which clamps
/// silently, a rectangle that is not wholly inside the image is REFUSED: a crop the caller asked for by
/// four numbers should be exactly those numbers or nothing.
/// </summary>
public static class MasterCrop
{
    /// <summary>
    /// Says in words why <paramref name="rect"/> is not a crop of a <paramref name="width"/> x
    /// <paramref name="height"/> image, or null when it is.
    /// </summary>
    public static string? Validate(PixelRect rect, int width, int height)
    {
        var what = $"the rectangle {rect.Width}x{rect.Height} at ({rect.X},{rect.Y})";
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return $"{what} is empty: width and height must be at least 1 (the image is {width}x{height})";
        }
        if (rect.X < 0 || rect.Y < 0)
        {
            return $"{what} starts outside the image: x and y must be 0 or more (the image is {width}x{height})";
        }
        if (rect.Right > width)
        {
            return $"{what} reaches x={rect.Right}, past the image width {width} (the image is {width}x{height})";
        }
        if (rect.Bottom > height)
        {
            return $"{what} reaches y={rect.Bottom}, past the image height {height} (the image is {width}x{height})";
        }
        return null;
    }

    /// <summary>
    /// Crops the master in <paramref name="inputPath"/> to <paramref name="rect"/> and writes
    /// <paramref name="outputPath"/>. A refusal writes no file.
    /// </summary>
    public static MasterCropResult CropFile(string inputPath, PixelRect rect, string outputPath)
    {
        if (!Image.TryReadFitsFile(inputPath, out var image, out var wcs))
        {
            return new MasterCropResult(false, $"{inputPath} could not be read as a FITS image", 0, 0);
        }

        AlignmentProvenance? provenance;
        using (var fits = Image.OpenFitsHeader(inputPath))
        {
            provenance = fits.ReadFirstImageHduHeaderOnly()?.Header is { } header
                ? AlignmentProvenance.FromHeader(header)
                : null;
        }

        return Crop(image, wcs, provenance, rect, outputPath);
    }

    /// <summary>
    /// Crops <paramref name="image"/> to <paramref name="rect"/> and writes it to <paramref name="outputPath"/>:
    /// the WCS is translated (<see cref="WCS.CroppedTo"/>), the provenance moves its canvas origin by the
    /// crop offset (<see cref="AlignmentProvenance.ForCrop"/>) and is written back, and <c>SWMODIFY</c> is
    /// stamped. A <paramref name="provenance"/> of null (a master written before the card) gets none.
    /// </summary>
    public static MasterCropResult Crop(Image image, WCS? wcs, AlignmentProvenance? provenance, PixelRect rect, string outputPath)
    {
        if (Validate(rect, image.Width, image.Height) is { } refusal)
        {
            return new MasterCropResult(false, refusal, 0, 0);
        }

        var cropped = image.Crop(rect);
        var extras = SharpenPipeline.SwModifyHeader();
        provenance?.ForCrop(rect.X, rect.Y).AddHeaderCards(extras);
        cropped.WriteToFitsFile(outputPath, wcs?.CroppedTo(rect.X, rect.Y), extras);
        return new MasterCropResult(true, string.Empty, cropped.Width, cropped.Height);
    }
}
