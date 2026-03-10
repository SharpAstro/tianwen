using CommunityToolkit.HighPerformance;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

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
    /// Computes the post-stretch background level (luminance) by running
    /// the median through the same stretch pipeline the shader uses.
    /// Uses <see cref="Image.StretchValue"/> — the single source of truth.
    /// </summary>
    public float EstimatePostStretchBackground(ChannelStretchStats[] perChannelStats, ChannelStretchStats? lumaStats)
    {
        if (Mode == 0)
        {
            // No stretch — background is just the normalized median
            if (perChannelStats.Length == 0) return 0.5f;
            // Median is already pedestal-subtracted; add pedestal back for the raw value, then normalize
            var med = (perChannelStats[0].Median + perChannelStats[0].Pedestal) * NormFactor;
            return Math.Clamp(med, 0.01f, 0.99f);
        }

        if (Mode == 2 && lumaStats is { } luma)
        {
            // Luma mode: median from stats is already pedestal-subtracted,
            // which is exactly what the shader computes as norm = Y - pedestal.
            // Feed it through the shared stretch pipeline with normFactor=1, pedestal=0
            // since the median is already in post-pedestal-subtraction space.
            var bg = Image.StretchValue(luma.Median, 1f, 0f, Shadows.R, Midtones.R, Rescale.R);
            return Math.Clamp(bg, 0.01f, 0.99f);
        }

        // Per-channel or linked: stretch each channel's median, then take Rec.709 luminance.
        // Stats medians are already pedestal-subtracted, so pass normFactor=1, pedestal=0.
        var r = StretchChannelMedian(perChannelStats, 0, Shadows.R, Midtones.R, Rescale.R);
        var g = StretchChannelMedian(perChannelStats, 1, Shadows.G, Midtones.G, Rescale.G);
        var b = StretchChannelMedian(perChannelStats, 2, Shadows.B, Midtones.B, Rescale.B);

        // Rec.709 luminance
        var Y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        return Math.Clamp(Y, 0.01f, 0.99f);
    }

    private static float StretchChannelMedian(ChannelStretchStats[] stats, int ch, float shadows, float midtones, float rescale)
    {
        var median = ch < stats.Length ? stats[ch].Median : stats[0].Median;
        return Image.StretchValue(median, 1f, 0f, shadows, midtones, rescale);
    }
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

    public bool IsPlateSolved => Wcs is { HasCDMatrix: true, IsApproximate: false };

    private FitsDocument(
        string filePath,
        Image image,
        DebayerAlgorithm debayerAlgorithm,
        ChannelStretchStats[] perChannelStats,
        ChannelStretchStats? lumaStats,
        WCS? wcs)
    {
        _filePath = filePath;
        UnstretchedImage = image;
        DebayerAlgorithm = debayerAlgorithm;
        PerChannelStats = perChannelStats;
        LumaStats = lumaStats;
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

        // If the FITS header didn't have a full CD matrix, try companion ASTAP .ini file
        if (fileWcs is not { HasCDMatrix: true } || fileWcs.Value.IsApproximate)
        {
            var iniPath = Path.ChangeExtension(filePath, ".ini");
            if (WCS.FromAstapIniFile(iniPath) is { HasCDMatrix: true } astapWcs)
            {
                fileWcs = astapWcs;
            }
        }

        return new FitsDocument(filePath, processedRawImage, actualAlgorithm, perChannelStats, lumaStats, fileWcs);
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
