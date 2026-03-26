using CommunityToolkit.HighPerformance;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Core document model for the astro image viewer. Manages the image lifecycle:
/// loading (FITS, TIFF), debayering, channel extraction, plate solving,
/// and conversion to display-ready RGBA pixels.
/// Stretch is performed entirely on the GPU via shader uniforms.
/// </summary>
public sealed class AstroImageDocument : IDisposable
{
    /// <summary>Supported file extensions for the image viewer.</summary>
    public static readonly ImmutableArray<string> SupportedExtensions = [".fits", ".fit", ".fts", ".tif", ".tiff"];

    /// <summary>Glob patterns matching all supported file extensions (for folder scanning).</summary>
    public static readonly ImmutableArray<string> SupportedPatterns = [.. SupportedExtensions.Select(ext => "*" + ext)];

    /// <summary>File dialog filter definitions.</summary>
    public static readonly (string Name, string[] Extensions)[] FileDialogFilters =
    [
        ("FITS files", [".fits", ".fit", ".fts"]),
        ("TIFF files", [".tif", ".tiff"]),
    ];

    private readonly string _filePath;

    /// <summary>Debayered image (or raw image if it is a colour or mono image). This is the permanent base image.</summary>
    public Image UnstretchedImage { get; }

    /// <summary>WCS solution, available after plate solving.</summary>
    public WCS? Wcs { get; private set; }

    /// <summary>Per-channel statistics computed from the raw image.</summary>
    public ImageHistogram[] ChannelStatistics { get; }

    /// <summary>Debayer algorithm actually used when loading this image.</summary>
    public DebayerAlgorithm DebayerAlgorithm { get; }

    /// <summary>Per-channel stretch stats from the processedRawImage image.</summary>
    public ChannelStretchStats[] PerChannelStats { get; }

    /// <summary>Luminance stretch stats (for luma mode). Only populated for color images (>=3 channels).</summary>
    public ChannelStretchStats? LumaStats { get; }

    /// <summary>
    /// Per-channel background values measured from the unstretched image (pedestal-subtracted).
    /// These are the average values of the darkest spatial region, ready to feed into
    /// <see cref="Image.StretchValue"/> to get the post-stretch background level.
    /// </summary>
    public float[] PerChannelBackground { get; private set; }

    /// <summary>Luminance background from the unstretched image (pedestal-subtracted).</summary>
    public float LumaBackground { get; private set; }

    /// <summary>Detected stars: <c>null</c> while detection is in progress, empty on failure/no stars, populated on success.</summary>
    public StarList? Stars { get; set; }

    /// <summary>Average HFR of detected stars (median).</summary>
    public float AverageHFR { get; private set; }

    /// <summary>Average FWHM of detected stars (median).</summary>
    public float AverageFWHM { get; private set; }

    /// <summary>Time taken for star detection.</summary>
    public TimeSpan StarDetectionDuration { get; private set; }

    /// <summary>Whether the image appears to be already stretched (e.g. processed TIFF). When true, STF should be disabled by default.</summary>
    public bool IsPreStretched { get; }

    public bool IsPlateSolved => Wcs is { HasCDMatrix: true, IsApproximate: false };

    /// <summary>Returns true if the given file extension is a supported image format.</summary>
    public static bool IsSupportedExtension(string extension)
        => SupportedExtensions.Any(ext => ext.Equals(extension, StringComparison.OrdinalIgnoreCase));

    private AstroImageDocument(
        string filePath,
        Image image,
        DebayerAlgorithm debayerAlgorithm,
        ChannelStretchStats[] perChannelStats,
        ChannelStretchStats? lumaStats,
        float[] perChannelBackground,
        float lumaBackground,
        WCS? wcs,
        bool isPreStretched)
    {
        _filePath = filePath;
        UnstretchedImage = image;
        DebayerAlgorithm = debayerAlgorithm;
        PerChannelStats = perChannelStats;
        LumaStats = lumaStats;
        PerChannelBackground = perChannelBackground;
        LumaBackground = lumaBackground;
        Wcs = wcs;
        IsPreStretched = isPreStretched;

        var stats = new ImageHistogram[image.ChannelCount];
        for (var c = 0; c < image.ChannelCount; c++)
        {
            stats[c] = image.Statistics(c);
        }
        ChannelStatistics = stats;
    }

    /// <summary>
    /// Creates a document from an in-memory <see cref="Image"/> (e.g. from the live session capture).
    /// The image data is used directly — no file I/O, no debayering.
    /// </summary>
    public static async Task<AstroImageDocument> CreateFromImageAsync(Image image, DebayerAlgorithm algorithm = DebayerAlgorithm.AHD, WCS? wcs = null, string filePath = "", CancellationToken cancellationToken = default)
    {
        // Normalize to [0,1] if needed
        var viewImage = image.MaxValue > 1.0f + float.Epsilon
            ? image.ScaleFloatValuesToUnit()
            : image;

        // Debayer RGGB raw Bayer data into 3-channel color
        DebayerAlgorithm actualAlgorithm;
        if (viewImage.ImageMeta.SensorType is SensorType.RGGB && algorithm is not DebayerAlgorithm.None)
        {
            viewImage = await viewImage.DebayerAsync(algorithm, normalizeToUnit: false, cancellationToken);
            actualAlgorithm = algorithm;
        }
        else
        {
            actualAlgorithm = DebayerAlgorithm.None;
        }

        var (perChannelStats, lumaStats, perChannelBg, lumaBg) = await ComputeStretchStatsAsync(viewImage, cancellationToken);

        return new AstroImageDocument(
            filePath,
            viewImage,
            actualAlgorithm,
            perChannelStats,
            lumaStats,
            perChannelBg,
            lumaBg,
            wcs,
            isPreStretched: false);
    }

    /// <summary>
    /// Opens an image file (FITS or TIFF), applies debayering if needed, and caches stretch statistics.
    /// The debayer result becomes the permanent base image; stretch is done on the GPU.
    /// </summary>
    public static async Task<AstroImageDocument?> OpenAsync(string filePath, DebayerAlgorithm algorithm = DebayerAlgorithm.AHD, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(filePath);

        if (ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenTiffAsync(filePath, cancellationToken);
        }

        return await OpenFitsAsync(filePath, algorithm, cancellationToken);
    }

    private static async Task<AstroImageDocument?> OpenFitsAsync(string filePath, DebayerAlgorithm algorithm, CancellationToken cancellationToken)
    {
        if (!Image.TryReadFitsFile(filePath, out var rawImage, out var fileWcs) || rawImage is null)
        {
            return null;
        }

        // If the FITS header didn't have a full CD matrix, try companion ASTAP .ini file
        if (fileWcs is not { HasCDMatrix: true } || fileWcs.Value.IsApproximate)
        {
            var iniPath = Path.ChangeExtension(filePath, ".ini");
            if (WCS.FromAstapIniFile(iniPath) is { HasCDMatrix: true } astapWcs)
            {
                fileWcs = astapWcs;
            }
        }

        return await CreateFromImageAsync(rawImage, algorithm, fileWcs, filePath, cancellationToken);
    }

    private static async Task<AstroImageDocument?> OpenTiffAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!Image.TryReadTiffFile(filePath, out var image))
        {
            return null;
        }

        var isPreStretched = Image.DetectPreStretched(image);

        // Image is already normalized to [0,1] by TryReadTiffFile
        var channelCount = image.ChannelCount;
        var perChannelStats = new ChannelStretchStats[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, med, mad) = image.GetPedestralMedianAndMADScaledToUnit(c);
            perChannelStats[c] = new ChannelStretchStats(ped, med, mad);
        }

        ChannelStretchStats? lumaStats = null;
        if (channelCount >= 3)
        {
            var (lumaPed, lumaMed, lumaMad) = await image.GetLumaStretchStatsAsync(DebayerAlgorithm.None, cancellationToken);
            lumaStats = new ChannelStretchStats(lumaPed, lumaMed, lumaMad);
        }

        Span<float> pedestals = stackalloc float[channelCount];
        for (var c = 0; c < channelCount; c++) { pedestals[c] = perChannelStats[c].Pedestal; }
        var (perChannelBg, lumaBg) = image.ScanBackgroundRegion(pedestals);

        // Try companion ASTAP .ini file for WCS
        WCS? wcs = null;
        var iniPath = Path.ChangeExtension(filePath, ".ini");
        if (WCS.FromAstapIniFile(iniPath) is { HasCDMatrix: true } astapWcs)
        {
            wcs = astapWcs;
        }

        return new AstroImageDocument(filePath, image, DebayerAlgorithm.None, perChannelStats, lumaStats, perChannelBg, lumaBg, wcs, isPreStretched);
    }

    private static async Task<(ChannelStretchStats[] PerChannelStats, ChannelStretchStats? LumaStats, float[] PerChannelBg, float LumaBg)> ComputeStretchStatsAsync(
        Image processedRawImage, CancellationToken cancellationToken)
    {
        var channelCount = processedRawImage.ChannelCount;
        var perChannelStats = new ChannelStretchStats[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, med, mad) = processedRawImage.GetPedestralMedianAndMADScaledToUnit(c);
            perChannelStats[c] = new ChannelStretchStats(ped, med, mad);
        }

        ChannelStretchStats? lumaStats = null;
        if (channelCount >= 3)
        {
            var (lumaPed, lumaMed, lumaMad) = await processedRawImage.GetLumaStretchStatsAsync(DebayerAlgorithm.None, cancellationToken);
            lumaStats = new ChannelStretchStats(lumaPed, lumaMed, lumaMad);
        }

        Span<float> pedestals = stackalloc float[channelCount];
        for (var c = 0; c < channelCount; c++) { pedestals[c] = perChannelStats[c].Pedestal; }
        var (perChannelBg, lumaBg) = processedRawImage.ScanBackgroundRegion(pedestals);

        return (perChannelStats, lumaStats, perChannelBg, lumaBg);
    }

    /// <summary>
    /// Computes stretch shader uniforms for the current stretch mode and parameters.
    /// </summary>
    public StretchUniforms ComputeStretchUniforms(StretchMode mode, StretchParameters parameters)
    {
        if (mode is StretchMode.None)
        {
            return new StretchUniforms(StretchMode.None, 1f, default, default, default, default, default);
        }

        var normFactor = UnstretchedImage.MaxValue > 1.0f + float.Epsilon ? 1f / UnstretchedImage.MaxValue : 1f;
        var factor = parameters.Factor;
        var clipping = parameters.ShadowsClipping;

        if (mode is StretchMode.Luma && LumaStats is { } luma)
        {
            var (s, m, h, r) = Image.ComputeStretchParameters(luma.Median, luma.Mad, factor, clipping);

            // Use per-channel pedestals for background subtraction (avoids green cast from RGGB)
            // but luma-derived midtone/shadows/rescale for consistent stretch across channels
            var chStats = PerChannelStats;
            var ped0 = chStats.Length > 0 ? chStats[0].Pedestal : luma.Pedestal;
            var ped1 = chStats.Length > 1 ? chStats[1].Pedestal : ped0;
            var ped2 = chStats.Length > 2 ? chStats[2].Pedestal : ped0;

            return new StretchUniforms(
                Mode: StretchMode.Luma,
                NormFactor: normFactor,
                Pedestal: (ped0, ped1, ped2),
                Shadows: ((float)s, (float)s, (float)s),
                Midtones: ((float)m, (float)m, (float)m),
                Highlights: ((float)h, (float)h, (float)h),
                Rescale: ((float)r, (float)r, (float)r));
        }

        // Linked or unlinked
        var stats = PerChannelStats;
        var ch0 = stats.Length > 0 ? stats[0] : default;
        var ch1 = stats.Length > 1 ? stats[1] : ch0;
        var ch2 = stats.Length > 2 ? stats[2] : ch0;

        if (mode is StretchMode.Linked)
        {
            ch1 = ch0;
            ch2 = ch0;
        }

        var p0 = Image.ComputeStretchParameters(ch0.Median, ch0.Mad, factor, clipping);
        var p1 = Image.ComputeStretchParameters(ch1.Median, ch1.Mad, factor, clipping);
        var p2 = Image.ComputeStretchParameters(ch2.Median, ch2.Mad, factor, clipping);

        return new StretchUniforms(
            Mode: mode,
            NormFactor: normFactor,
            Pedestal: (ch0.Pedestal, ch1.Pedestal, ch2.Pedestal),
            Shadows: ((float)p0.Shadows, (float)p1.Shadows, (float)p2.Shadows),
            Midtones: ((float)p0.Midtones, (float)p1.Midtones, (float)p2.Midtones),
            Highlights: ((float)p0.Highlights, (float)p1.Highlights, (float)p2.Highlights),
            Rescale: ((float)p0.Rescale, (float)p1.Rescale, (float)p2.Rescale));
    }

    /// <summary>
    /// Plate-solves the image using the provided factory.
    /// When the document already has an approximate WCS (from FITS headers or ASTAP .ini),
    /// it is passed as the search origin so that the catalog plate solver can use it.
    /// </summary>
    public async Task<bool> PlateSolveAsync(IPlateSolverFactory solverFactory, CancellationToken cancellationToken = default)
    {
        var imageDim = UnstretchedImage.GetImageDim();
        var result = await solverFactory.SolveFileAsync(_filePath, imageDim, searchOrigin: Wcs, cancellationToken: cancellationToken);
        if (result.Solution is { } wcs)
        {
            Wcs = wcs;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Detects stars in the image. Should be called as a background task after loading.
    /// </summary>
    public async Task DetectStarsAsync(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var stars = await UnstretchedImage.FindStarsAsync(channel: 0, snrMin: 10f, maxStars: 2000, cancellationToken: cancellationToken);
        sw.Stop();

        Stars = stars;
        StarDetectionDuration = sw.Elapsed;

        if (stars.Count > 0)
        {
            AverageHFR = stars.MapReduceStarProperty(SampleKind.HFD, AggregationMethod.Median);
            AverageFWHM = stars.MapReduceStarProperty(SampleKind.FWHM, AggregationMethod.Median);

            // Re-scan background with star mask for more accurate boost operation
            Span<float> pedestals = stackalloc float[PerChannelStats.Length];
            for (var c = 0; c < PerChannelStats.Length; c++) { pedestals[c] = PerChannelStats[c].Pedestal; }
            var (perChannelBg, lumaBg) = UnstretchedImage.ScanBackgroundRegion(pedestals, squareSize: 48, stars.StarMask);
            PerChannelBackground = perChannelBg;
            LumaBackground = lumaBg;
        }
    }

    /// <summary>
    /// Gets pixel information at the given display coordinates, including sky coordinates if plate-solved.
    /// Returns raw (unstretched) values from the processedRawImage image.
    /// </summary>
    public PixelInfo GetPixelInfo(int x, int y)
    {
        var image = UnstretchedImage;
        if (x < 0 || x >= image.Width || y < 0 || y >= image.Height)
        {
            return new PixelInfo(x, y, [], null, null);
        }

        var values = new float[image.ChannelCount];
        for (var c = 0; c < image.ChannelCount; c++)
        {
            values[c] = image[c, y, x];
        }

        double? ra = null, dec = null;
        if (Wcs is { } wcs)
        {
            var sky = wcs.PixelToSky(x + 1, y + 1);
            if (sky.HasValue)
            {
                ra = sky.Value.RA;
                dec = sky.Value.Dec;
            }
        }

        return new PixelInfo(x, y, values, ra, dec);
    }

    /// <summary>
    /// Returns image channel arrays to <see cref="Array2DPool{T}"/> for reuse.
    /// </summary>
    public void Dispose() => UnstretchedImage.ReturnChannelData();
}
