using System;
using System.Collections.Generic;
using System.IO;
using nom.tam.fits;
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
/// <param name="SourceWidth">Width of the master that was cropped; 0 when the input could not be read, since
/// there is then no master to measure.</param>
/// <param name="SourceHeight">Height of the master that was cropped; 0 when the input could not be read.</param>
public readonly record struct MasterCropResult(bool Succeeded, string Message, int Width, int Height, int SourceWidth = 0, int SourceHeight = 0)
{
    /// <summary>
    /// The map sidecars cropped and written beside the output, by kind ("coverage", "rejection", "saturation",
    /// "standard error"); empty where the input had none, and for <see cref="MasterCrop.Crop"/>, which is given no
    /// input file. Init-only, so the constructor keeps its shape.
    /// </summary>
    public IReadOnlyList<string> Sidecars { get; init; } = [];

    /// <summary>
    /// Map sidecars the input had that were NOT carried, each with its reason in words: one that is not on the
    /// master's geometry, or a stale one beside the output that could not be removed.
    /// </summary>
    public IReadOnlyList<string> SidecarsLeftOut { get; init; } = [];
}

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

        return CropLoaded(inputPath, image, wcs, rect, outputPath);
    }

    /// <summary>
    /// As <see cref="CropFile"/> for a caller that has already read <paramref name="image"/> and
    /// <paramref name="wcs"/> from <paramref name="inputPath"/> (the autocrop verb scans the pixels first), so
    /// the pixels are read once per crop. Only the header is read again, for the provenance and the stack cards.
    /// </summary>
    public static MasterCropResult CropLoaded(string inputPath, Image image, WCS? wcs, PixelRect rect, string outputPath)
    {
        var (provenance, carried, frameCount, strategy) = ReadStackFacts(inputPath);

        if (Validate(rect, image.Width, image.Height) is not null)
        {
            // A refusal writes nothing, the sidecars included.
            return Crop(image, wcs, provenance, rect, outputPath, carried);
        }

        // The maps are read BEFORE anything is written: the output may be the input's own path. Each is cropped as it
        // is read and the whole one given back, so what is held across the write is the cropped copies and not four
        // full-frame maps beside the master (a map of an RGB master is three float planes, as large as the master).
        var maps = ReadCroppedMaps(inputPath, image.Width, image.Height, rect);
        try
        {
            var result = Crop(image, wcs, provenance, rect, outputPath, carried);
            if (!result.Succeeded)
            {
                return result;
            }

            var (written, leftOut) = WriteMaps(maps, outputPath, frameCount, strategy);
            return result with { Sidecars = written, SidecarsLeftOut = leftOut };
        }
        finally
        {
            ReleaseMaps(maps);
        }
    }

    /// <summary>
    /// Carries the map sidecars of the master in <paramref name="inputPath"/> to <paramref name="outputPath"/>, whose
    /// pixels are the master's own (same grid): what <c>tianwen solve -o</c> does so the solved file keeps the
    /// coverage plane a later auto crop reads, as a crop of the whole frame would. Nothing is written when the output
    /// is the input's own file, which has them already.
    /// </summary>
    /// <returns>The sidecars written and what was left out and why.</returns>
    public static (IReadOnlyList<string> Written, IReadOnlyList<string> LeftOut) CarrySidecars(string inputPath, int width, int height, string outputPath)
    {
        if (IsSameFile(inputPath, outputPath))
        {
            return ([], []);
        }

        var (_, _, frameCount, strategy) = ReadStackFacts(inputPath);

        var maps = ReadCroppedMaps(inputPath, width, height, new PixelRect(0, 0, width, height));
        try
        {
            return WriteMaps(maps, outputPath, frameCount, strategy);
        }
        finally
        {
            ReleaseMaps(maps);
        }
    }

    private static (List<string> Written, List<string> LeftOut) WriteMaps(
        (Image? Coverage, Image? Rejection, Image? Saturation, Image? StandardError, List<string> LeftOut) maps, string outputPath, int frameCount, IntegrationStrategyKind? strategy)
    {
        var leftOut = new List<string>(maps.LeftOut);
        foreach (var stale in IntegrationFitsWriter.RemoveCanvasMapSidecars(outputPath))
        {
            leftOut.Add($"{Path.GetFileName(stale)} was beside the output from an earlier file and could not be removed");
        }

        var written = new List<string>();
        if (maps.Coverage is { } coverage)
        {
            IntegrationFitsWriter.WriteCoverageMap(outputPath, coverage, frameCount, strategy);
            written.Add("coverage");
        }
        if (maps.Rejection is { } rejection)
        {
            IntegrationFitsWriter.WriteRejectionMap(outputPath, rejection, frameCount, MeanOf(rejection), strategy);
            written.Add("rejection");
        }
        if (maps.Saturation is { } saturation)
        {
            IntegrationFitsWriter.WriteSaturationMap(outputPath, saturation, frameCount, strategy);
            written.Add("saturation");
        }
        if (maps.StandardError is { } standardError)
        {
            IntegrationFitsWriter.WriteStandardErrorMap(outputPath, standardError, frameCount, strategy);
            written.Add("standard error");
        }

        return (written, leftOut);
    }

    private static void ReleaseMaps((Image? Coverage, Image? Rejection, Image? Saturation, Image? StandardError, List<string> LeftOut) maps)
    {
        maps.Coverage?.Release();
        maps.Rejection?.Release();
        maps.Saturation?.Release();
        maps.StandardError?.Release();
    }

    /// <summary>
    /// The maps beside a master that describe its PIXELS, each on the master's own grid, so the same rectangle crops
    /// them: coverage (or a drizzle master's weight plane), the rejection fraction, the saturated-frame count and
    /// the standard error. The writer's own rule is that a caller that crops a master crops these too
    /// (<see cref="IntegrationFitsWriter.WriteStandardErrorMap"/>); until this was written the crop verbs left them
    /// behind, so a cropped master had no coverage plane to say where it is whole and no saturation map for SPCC
    /// (#2274). The bad pixel map is not one of them: it is the SENSOR's photosites, not the canvas's pixels, and a
    /// rectangle of the canvas names nothing in it. A crop neither carries it nor removes it, so a crop onto the
    /// master's own path keeps the master's.
    /// </summary>
    /// <returns>Each map already cropped to <paramref name="rect"/>, the caller's to release. One whole map is held
    /// at a time: it is cropped and given back before the next is read.</returns>
    private static (Image? Coverage, Image? Rejection, Image? Saturation, Image? StandardError, List<string> LeftOut) ReadCroppedMaps(
        string inputPath, int width, int height, PixelRect rect)
    {
        var leftOut = new List<string>();
        Image? Cropped(Image? map, string kind)
        {
            if (map is null)
            {
                return null;
            }

            try
            {
                if (map.Width == width && map.Height == height)
                {
                    return map.Crop(rect);
                }

                leftOut.Add($"the {kind} map is {map.Width}x{map.Height}, not the master's {width}x{height}");
                return null;
            }
            finally
            {
                map.Release();
            }
        }

        IntegrationFitsWriter.TryReadCoverageMap(inputPath, out var coverage);
        var croppedCoverage = Cropped(coverage, "coverage");
        IntegrationFitsWriter.TryReadRejectionMap(inputPath, out var rejection);
        var croppedRejection = Cropped(rejection, "rejection");
        IntegrationFitsWriter.TryReadSaturationMap(inputPath, out var saturation);
        var croppedSaturation = Cropped(saturation, "saturation");
        IntegrationFitsWriter.TryReadStandardErrorMap(inputPath, out var standardError);
        return (croppedCoverage, croppedRejection, croppedSaturation, Cropped(standardError, "standard error"), leftOut);
    }

    /// <summary>The mean of every finite sample of <paramref name="map"/>, over all its channels; 0 for none.</summary>
    private static double MeanOf(Image map)
    {
        double sum = 0;
        long count = 0;
        for (var c = 0; c < map.ChannelCount; c++)
        {
            foreach (var value in map.GetChannelSpan(c))
            {
                if (float.IsFinite(value))
                {
                    sum += value;
                    count++;
                }
            }
        }

        return count > 0 ? sum / count : 0.0;
    }

    /// <summary>Whether two paths name the same file (compared by full path; a case-only difference counts as the same
    /// only on a case-insensitive file system). The one rule the crop and the solve verb share.</summary>
    public static bool IsSameFile(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The header cards a crop of the whole frame of the master in <paramref name="inputPath"/> would carry: the stack
    /// cards and the alignment provenance (origin unmoved, a crop at 0,0), as extras for
    /// <see cref="Image.WriteToFitsFile(string, WCS?, IReadOnlyDictionary{string, ValueTuple{object, string}}?, FitsSampleStorage?)"/>.
    /// The one reader both the crop and the solved file go through. Empty when the file has none.
    /// </summary>
    public static Dictionary<string, (object Value, string Comment)> WholeFrameCards(string inputPath)
    {
        var (provenance, carried, _, _) = ReadStackFacts(inputPath);
        provenance?.ForCrop(0, 0).AddHeaderCards(carried);
        return carried;
    }

    private static (AlignmentProvenance? Provenance, Dictionary<string, (object Value, string Comment)> Carried, int FrameCount, IntegrationStrategyKind? Strategy) ReadStackFacts(string inputPath)
    {
        AlignmentProvenance? provenance = null;
        var carried = new Dictionary<string, (object Value, string Comment)>();
        var frameCount = 0;
        IntegrationStrategyKind? strategy = null;
        using var fits = Image.OpenFitsHeader(inputPath);
        if (fits.ReadFirstImageHduHeaderOnly()?.Header is { } header)
        {
            provenance = AlignmentProvenance.FromHeader(header);
            CarryStackCards(header, carried);
            frameCount = Math.Max(0, header.GetIntValue("STACK_N", 0));
            if (Enum.TryParse<IntegrationStrategyKind>(header.GetStringValue("STRATEGY"), ignoreCase: true, out var kind))
            {
                strategy = kind;
            }
        }

        return (provenance, carried, frameCount, strategy);
    }

    /// <summary>
    /// Copies the cards that describe how the master was integrated, which a crop does not change:
    /// the frame count, the SBFITSEXT count, the rejection totals and the strategy. A card the input does
    /// not hold is not invented.
    /// </summary>
    private static void CarryStackCards(Header header, Dictionary<string, (object Value, string Comment)> carried)
    {
        if (header.GetIntValue("STACK_N", 0) is > 0 and var stackN)
        {
            carried["STACK_N"] = (stackN, "Number of frames combined into this master");
        }
        if (header.GetIntValue("SNAPSHOT", 0) is > 0 and var snapshot)
        {
            carried["SNAPSHOT"] = (snapshot, "Number of images combined (SBFITSEXT)");
        }
        if (header.GetLongValue("REJ_TOT", -1L) is >= 0 and var rejTot)
        {
            carried["REJ_TOT"] = (rejTot, "Total per-pixel rejections across the stack (of the uncropped master)");
        }
        if (header.GetDoubleValue("REJ_RATE", -1.0) is >= 0 and var rejRate)
        {
            carried["REJ_RATE"] = (rejRate, "Mean rejection rate (of the uncropped master)");
        }
        if (header.GetStringValue("STRATEGY") is { Length: > 0 } strategy)
        {
            carried["STRATEGY"] = (strategy, "Integration strategy used (IntegrationStrategyKind)");
        }
    }

    /// <summary>
    /// Crops <paramref name="image"/> to <paramref name="rect"/> and writes it to <paramref name="outputPath"/>:
    /// the WCS is translated (<see cref="WCS.CroppedTo"/>), the provenance moves its canvas origin by the
    /// crop offset (<see cref="AlignmentProvenance.ForCrop"/>) and is written back, and <c>SWMODIFY</c> is
    /// stamped. A <paramref name="provenance"/> of null (a master written before the card) gets none.
    /// </summary>
    public static MasterCropResult Crop(Image image, WCS? wcs, AlignmentProvenance? provenance, PixelRect rect, string outputPath,
        IReadOnlyDictionary<string, (object Value, string Comment)>? carried = null)
    {
        if (Validate(rect, image.Width, image.Height) is { } refusal)
        {
            return new MasterCropResult(false, refusal, 0, 0, image.Width, image.Height);
        }

        var cropped = image.Crop(rect);
        var extras = SharpenPipeline.SwModifyHeader();
        if (carried is not null)
        {
            foreach (var (key, value) in carried)
            {
                extras[key] = value;
            }
        }
        provenance?.ForCrop(rect.X, rect.Y).AddHeaderCards(extras);
        cropped.WriteToFitsFile(outputPath, wcs?.CroppedTo(rect.X, rect.Y), extras);
        return new MasterCropResult(true, string.Empty, cropped.Width, cropped.Height, image.Width, image.Height);
    }
}
