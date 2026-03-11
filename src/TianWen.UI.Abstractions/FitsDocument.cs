using CommunityToolkit.HighPerformance;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Stat;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Per-channel stretch statistics cached from the processedRawImage image.
/// </summary>
public record struct ChannelStretchStats(float Pedestal, float Median, float Mad);

/// <summary>
/// Stretch parameters ready to pass as GPU shader uniforms.
/// Each field is a 3-component vector (R/G/B or replicated for mono/linked).
/// </summary>
public record struct GpuStretchUniforms(
    int Mode,
    float NormFactor,
    (float R, float G, float B) Pedestal,
    (float R, float G, float B) Shadows,
    (float R, float G, float B) Midtones,
    (float R, float G, float B) Highlights,
    (float R, float G, float B) Rescale)
{
    /// <summary>
    /// Computes the post-stretch background level by stretching the measured
    /// background values (from <see cref="FitsDocument.PerChannelBackground"/>)
    /// through <see cref="Image.StretchValue"/> — the same pipeline as the GLSL shader.
    /// </summary>
    public float ComputePostStretchBackground(float[] perChannelBackground, float lumaBackground)
    {
        if (Mode == 0)
        {
            // No stretch — background is the raw luminance
            return Math.Clamp(lumaBackground * NormFactor, 0.01f, 0.99f);
        }

        if (Mode == 2)
        {
            // Luma mode: stretch the luma background value
            var bg = Image.StretchValue(lumaBackground, 1f, 0f, Shadows.R, Midtones.R, Rescale.R);
            return Math.Clamp(bg, 0.01f, 0.99f);
        }

        // Per-channel or linked: stretch each channel's measured background, then Rec.709 luminance
        var r = Image.StretchValue(GetChannelBg(perChannelBackground, 0), 1f, 0f, Shadows.R, Midtones.R, Rescale.R);
        var g = Image.StretchValue(GetChannelBg(perChannelBackground, 1), 1f, 0f, Shadows.G, Midtones.G, Rescale.G);
        var b = Image.StretchValue(GetChannelBg(perChannelBackground, 2), 1f, 0f, Shadows.B, Midtones.B, Rescale.B);

        var Y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        return Math.Clamp(Y, 0.01f, 0.99f);
    }

    private static float GetChannelBg(float[] perChannelBackground, int ch)
        => ch < perChannelBackground.Length ? perChannelBackground[ch] : perChannelBackground[0];
}

/// <summary>
/// Core document model for the FITS viewer. Manages the image lifecycle:
/// loading, debayering, channel extraction, plate solving,
/// and conversion to display-ready RGBA pixels.
/// Stretch is performed entirely on the GPU via shader uniforms.
/// </summary>
public sealed class FitsDocument
{
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
    public float[] PerChannelBackground { get; }

    /// <summary>Luminance background from the unstretched image (pedestal-subtracted).</summary>
    public float LumaBackground { get; }

    /// <summary>Detected stars: <c>null</c> while detection is in progress, empty on failure/no stars, populated on success.</summary>
    public StarList? Stars { get; set; }

    /// <summary>Average HFR of detected stars (median).</summary>
    public float AverageHFR { get; private set; }

    /// <summary>Average FWHM of detected stars (median).</summary>
    public float AverageFWHM { get; private set; }

    /// <summary>Time taken for star detection.</summary>
    public TimeSpan StarDetectionDuration { get; private set; }


    public bool IsPlateSolved => Wcs is { HasCDMatrix: true, IsApproximate: false };

    private FitsDocument(
        string filePath,
        Image image,
        DebayerAlgorithm debayerAlgorithm,
        ChannelStretchStats[] perChannelStats,
        ChannelStretchStats? lumaStats,
        float[] perChannelBackground,
        float lumaBackground,
        WCS? wcs)
    {
        _filePath = filePath;
        UnstretchedImage = image;
        DebayerAlgorithm = debayerAlgorithm;
        PerChannelStats = perChannelStats;
        LumaStats = lumaStats;
        PerChannelBackground = perChannelBackground;
        LumaBackground = lumaBackground;
        Wcs = wcs;

        var stats = new ImageHistogram[image.ChannelCount];
        for (var c = 0; c < image.ChannelCount; c++)
        {
            stats[c] = image.Statistics(c);
        }
        ChannelStatistics = stats;
    }

    /// <summary>
    /// Loads a FITS file, applies debayering once, and caches stretch statistics.
    /// The debayer result becomes the permanent base image; stretch is done on the GPU.
    /// </summary>
    public static async Task<FitsDocument?> OpenAsync(string filePath, DebayerAlgorithm algorithm = DebayerAlgorithm.AHD, CancellationToken cancellationToken = default)
    {
        if (!Image.TryReadFitsFile(filePath, out var rawImage, out var fileWcs) || rawImage is null)
        {
            return null;
        }

        Image processedRawImage;
        DebayerAlgorithm actualAlgorithm;

        if (rawImage.ImageMeta.SensorType is SensorType.RGGB && algorithm is not DebayerAlgorithm.None)
        {
            processedRawImage = (await rawImage.DebayerAsync(algorithm, cancellationToken)).ScaleFloatValuesToUnit();
            actualAlgorithm = algorithm;
        }
        else
        {
            processedRawImage = rawImage.ScaleFloatValuesToUnit();
            actualAlgorithm = DebayerAlgorithm.None;
        }

        // Cache per-channel stretch stats
        var channelCount = processedRawImage.ChannelCount;
        var perChannelStats = new ChannelStretchStats[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, med, mad) = processedRawImage.GetPedestralMedianAndMADScaledToUnit(c);
            perChannelStats[c] = new ChannelStretchStats(ped, med, mad);
        }

        // Compute luminance stats for color images
        ChannelStretchStats? lumaStats = null;
        if (channelCount >= 3)
        {
            var (lumaPed, lumaMed, lumaMad) = await processedRawImage.GetLumaStretchStatsAsync(DebayerAlgorithm.None, cancellationToken);
            lumaStats = new ChannelStretchStats(lumaPed, lumaMed, lumaMad);
        }

        // Scan for the darkest spatial region to get a reliable background level.
        // This is pedestal-subtracted (matching the shader's norm = raw * normFactor - pedestal).
        var pedestals = new float[channelCount];
        for (var c = 0; c < channelCount; c++) { pedestals[c] = perChannelStats[c].Pedestal; }
        var (perChannelBg, lumaBg) = processedRawImage.ScanBackgroundRegion(pedestals);

        // If the FITS header didn't have a full CD matrix, try companion ASTAP .ini file
        if (fileWcs is not { HasCDMatrix: true } || fileWcs.Value.IsApproximate)
        {
            var iniPath = Path.ChangeExtension(filePath, ".ini");
            if (WCS.FromAstapIniFile(iniPath) is { HasCDMatrix: true } astapWcs)
            {
                fileWcs = astapWcs;
            }
        }

        return new FitsDocument(filePath, processedRawImage, actualAlgorithm, perChannelStats, lumaStats, perChannelBg, lumaBg, fileWcs);
    }

    /// <summary>
    /// Computes stretch shader uniforms for the current stretch mode and parameters.
    /// </summary>
    public GpuStretchUniforms ComputeStretchUniforms(StretchMode mode, StretchParameters parameters)
    {
        if (mode is StretchMode.None)
        {
            return new GpuStretchUniforms(0, 1f, default, default, default, default, default);
        }

        var normFactor = UnstretchedImage.MaxValue > 1.0f + float.Epsilon ? 1f / UnstretchedImage.MaxValue : 1f;
        var factor = parameters.Factor;
        var clipping = parameters.ShadowsClipping;

        if (mode is StretchMode.Luma && LumaStats is { } luma)
        {
            var (s, m, h, r) = Image.ComputeStretchParameters(luma.Median, luma.Mad, factor, clipping);
            // Pass the luma pedestal via Pedestal.R — the shader uses it to subtract from Y and channels
            return new GpuStretchUniforms(
                Mode: 2,
                NormFactor: normFactor,
                Pedestal: (luma.Pedestal, luma.Pedestal, luma.Pedestal),
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

        return new GpuStretchUniforms(
            Mode: 1,
            NormFactor: normFactor,
            Pedestal: (ch0.Pedestal, ch1.Pedestal, ch2.Pedestal),
            Shadows: ((float)p0.Shadows, (float)p1.Shadows, (float)p2.Shadows),
            Midtones: ((float)p0.Midtones, (float)p1.Midtones, (float)p2.Midtones),
            Highlights: ((float)p0.Highlights, (float)p1.Highlights, (float)p2.Highlights),
            Rescale: ((float)p0.Rescale, (float)p1.Rescale, (float)p2.Rescale));
    }

    /// <summary>
    /// Plate-solves the image using the provided factory.
    /// </summary>
    public async Task<bool> PlateSolveAsync(IPlateSolverFactory solverFactory, CancellationToken cancellationToken = default)
    {
        var imageDim = UnstretchedImage.GetImageDim();
        var result = await solverFactory.SolveFileAsync(_filePath, imageDim, cancellationToken: cancellationToken);
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
}
