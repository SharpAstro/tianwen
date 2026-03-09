using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Per-channel stretch statistics cached from the debayered image.
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
    (float R, float G, float B) Rescale);

/// <summary>
/// Core document model for the FITS viewer. Manages the image lifecycle:
/// loading, debayering, channel extraction, plate solving,
/// and conversion to display-ready RGBA pixels.
/// Stretch is performed entirely on the GPU via shader uniforms.
/// </summary>
public sealed class FitsDocument
{
    private readonly string _filePath;

    /// <summary>Raw image as loaded from the FITS file.</summary>
    public Image RawImage { get; }

    /// <summary>Debayered image (or same as <see cref="RawImage"/> if not Bayer). This is the permanent base image.</summary>
    public Image DebayeredImage { get; }

    /// <summary>Display image — always the debayered image (stretch is done in the GPU shader).</summary>
    public Image DisplayImage => DebayeredImage;

    /// <summary>WCS solution, available after plate solving.</summary>
    public WCS? Wcs { get; private set; }

    /// <summary>Per-channel statistics computed from the raw image.</summary>
    public ImageHistogram[] ChannelStatistics { get; }

    /// <summary>Debayer algorithm actually used when loading this image.</summary>
    public DebayerAlgorithm DebayerAlgorithm { get; }

    /// <summary>Per-channel stretch stats from the debayered image.</summary>
    public ChannelStretchStats[] PerChannelStats { get; }

    /// <summary>Luminance stretch stats (for luma mode). Only populated for color images (>=3 channels).</summary>
    public ChannelStretchStats? LumaStats { get; }

    public bool IsPlateSolved => Wcs?.HasCDMatrix == true;

    private FitsDocument(
        string filePath,
        Image rawImage,
        Image debayeredImage,
        DebayerAlgorithm debayerAlgorithm,
        ChannelStretchStats[] perChannelStats,
        ChannelStretchStats? lumaStats)
    {
        _filePath = filePath;
        RawImage = rawImage;
        DebayeredImage = debayeredImage;
        DebayerAlgorithm = debayerAlgorithm;
        PerChannelStats = perChannelStats;
        LumaStats = lumaStats;

        var stats = new ImageHistogram[rawImage.ChannelCount];
        for (var c = 0; c < rawImage.ChannelCount; c++)
        {
            stats[c] = rawImage.Statistics(c);
        }
        ChannelStatistics = stats;
    }

    /// <summary>
    /// Loads a FITS file, applies debayering once, and caches stretch statistics.
    /// The debayer result becomes the permanent base image; stretch is done on the GPU.
    /// </summary>
    public static async Task<FitsDocument?> OpenAsync(string filePath, DebayerAlgorithm algorithm = DebayerAlgorithm.VNG, CancellationToken cancellationToken = default)
    {
        if (!Image.TryReadFitsFile(filePath, out var rawImage) || rawImage is null)
        {
            return null;
        }

        Image debayered;
        DebayerAlgorithm actualAlgorithm;

        if (rawImage.ImageMeta.SensorType is SensorType.RGGB && algorithm is not DebayerAlgorithm.None)
        {
            debayered = (await rawImage.DebayerAsync(algorithm, cancellationToken)).ScaleFloatValuesToUnit();
            actualAlgorithm = algorithm;
        }
        else
        {
            debayered = rawImage.ScaleFloatValuesToUnit();
            actualAlgorithm = DebayerAlgorithm.None;
        }

        // Cache per-channel stretch stats
        var channelCount = debayered.ChannelCount;
        var perChannelStats = new ChannelStretchStats[channelCount];
        for (var c = 0; c < channelCount; c++)
        {
            var (ped, med, mad) = debayered.GetPedestralMedianAndMADScaledToUnit(c);
            perChannelStats[c] = new ChannelStretchStats(ped, med, mad);
        }

        // Compute luminance stats for color images
        ChannelStretchStats? lumaStats = null;
        if (channelCount >= 3)
        {
            var (lumaPed, lumaMed, lumaMad) = await debayered.GetLumaStretchStatsAsync(DebayerAlgorithm.None, cancellationToken);
            lumaStats = new ChannelStretchStats(lumaPed, lumaMed, lumaMad);
        }

        return new FitsDocument(filePath, rawImage, debayered, actualAlgorithm, perChannelStats, lumaStats);
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

        var normFactor = DebayeredImage.MaxValue > 1.0f + float.Epsilon ? 1f / DebayeredImage.MaxValue : 1f;
        var factor = parameters.Factor;
        var clipping = parameters.ShadowsClipping;

        if (mode is StretchMode.Luma && LumaStats is { } luma)
        {
            var (s, m, h, r) = Image.ComputeStretchParameters(luma.Median, luma.Mad, factor, clipping);
            return new GpuStretchUniforms(
                Mode: 2,
                NormFactor: normFactor,
                Pedestal: default, // not used in luma mode per-pixel
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
        var imageDim = RawImage.GetImageDim();
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
    /// Returns raw (unstretched) values from the debayered image.
    /// </summary>
    public PixelInfo GetPixelInfo(int x, int y)
    {
        var image = DebayeredImage;
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
    /// Extracts per-channel float arrays from the debayered image for GPU upload as R32f textures.
    /// </summary>
    public float[][] GetChannelArrays(ChannelView channelView)
    {
        var image = DebayeredImage;
        var (channelCount, _, _) = image.Shape;

        if (channelView is ChannelView.Composite && channelCount >= 3)
        {
            return
            [
                image.GetChannelSpan(0).ToArray(),
                image.GetChannelSpan(1).ToArray(),
                image.GetChannelSpan(2).ToArray(),
            ];
        }

        var ch = channelView switch
        {
            ChannelView.Red or ChannelView.Channel0 => 0,
            ChannelView.Green or ChannelView.Channel1 => 1,
            ChannelView.Blue or ChannelView.Channel2 => 2,
            ChannelView.Channel3 => 3,
            _ => 0
        };

        if (ch >= channelCount)
        {
            ch = 0;
        }

        return [image.GetChannelSpan(ch).ToArray()];
    }

}
