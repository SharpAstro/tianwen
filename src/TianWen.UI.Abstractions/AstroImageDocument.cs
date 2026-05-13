using CommunityToolkit.HighPerformance;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.Lib.Stat;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Core document model for the astro image viewer. Manages the image lifecycle:
/// loading (FITS, TIFF), debayering, channel extraction, plate solving,
/// and conversion to display-ready RGBA pixels.
/// Stretch is performed entirely on the GPU via shader uniforms.
/// </summary>
public sealed class AstroImageDocument
{
    /// <summary>Supported file extensions for the image viewer.</summary>
    public static readonly ImmutableArray<string> SupportedExtensions = [".fits", ".fit", ".fts", ".tif", ".tiff", ".cr2", ".cr3"];

    /// <summary>Glob patterns matching all supported file extensions (for folder scanning).</summary>
    public static readonly ImmutableArray<string> SupportedPatterns = [.. SupportedExtensions.Select(ext => "*" + ext)];

    /// <summary>File dialog filter definitions.</summary>
    public static readonly (string Name, string[] Extensions)[] FileDialogFilters =
    [
        ("FITS files", [".fits", ".fit", ".fts"]),
        ("TIFF files", [".tif", ".tiff"]),
        ("Canon RAW", [".cr2", ".cr3"]),
    ];

    private readonly string _filePath;

    /// <summary>The file path this document was loaded from.</summary>
    public string FilePath => _filePath;

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

    /// <summary>Per-channel stretch stats recomputed with star mask exclusion. Only available after star detection.</summary>
    public ChannelStretchStats[]? StarMaskedStats { get; private set; }

    /// <summary>Luminance stretch stats recomputed with star mask exclusion. Only available after star detection.</summary>
    public ChannelStretchStats? StarMaskedLumaStats { get; private set; }

    /// <summary>Detected stars: <c>null</c> while detection is in progress, empty on failure/no stars, populated on success.</summary>
    public StarList? Stars { get; set; }

    /// <summary>Average HFR of detected stars (median).</summary>
    public float AverageHFR { get; private set; }

    /// <summary>Average FWHM of detected stars (median).</summary>
    public float AverageFWHM { get; private set; }

    /// <summary>Time taken for star detection.</summary>
    public TimeSpan StarDetectionDuration { get; private set; }

    /// <summary>White balance multipliers from Tycho-2 color calibration. null until computed.</summary>
    public (float R, float G, float B)? ColorCalibration { get; private set; }

    /// <summary>Background neutralization gains from pivot1 sampling (1,1,1) = no neutralization.</summary>
    public (float R, float G, float B)? BackgroundNeutralization { get; set; }

    /// <summary>When true, the stretch factor is iteratively adjusted so the post-stretch median converges to <see cref="ConvergenceTarget"/>.</summary>
    public bool UseIterativeConvergence { get; set; }

    /// <summary>Target post-stretch median for iterative convergence (default 0.25, PixInsight STF convention).</summary>
    public double ConvergenceTarget { get; set; } = 0.25;

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
        // For Bayer images: skip CPU debayer but normalize to [0,1] so stretch stats
        // match the existing histogram-based computation. The GPU shader does bilinear debayer.
        Image viewImage;
        DebayerAlgorithm actualAlgorithm;
        if (image.ImageMeta.SensorType is SensorType.RGGB && algorithm is not DebayerAlgorithm.None)
        {
            // Normalize to [0,1] but don't debayer — GPU shader handles debayer.
            viewImage = image.MaxValue > 1.0f + float.Epsilon
                ? image.ScaleFloatValuesToUnitInPlace()
                : image;
            actualAlgorithm = algorithm;
        }
        else
        {
            // Mono/color: normalize in place (no extra allocation)
            viewImage = image.MaxValue > 1.0f + float.Epsilon
                ? image.ScaleFloatValuesToUnitInPlace()
                : image;
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

        if (ext.Equals(".fits", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fit", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fts", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenFitsAsync(filePath, algorithm, cancellationToken);
        }

        // TIFF, CR2, CR3 — pure-managed: DIR.Lib TiffReader for TIFF,
        // FC.SDK.Raw for CR2/CR3. No Magick.NET fallback.
        return await OpenImageFileAsync(filePath, cancellationToken);
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

    private static async Task<AstroImageDocument?> OpenImageFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!Image.TryReadImageFile(filePath, out var image))
        {
            return null;
        }

        var isPreStretched = Image.DetectPreStretched(image);

        // Image is already normalized to [0,1] by TryReadImageFile
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
        var isRawBayer = processedRawImage.ImageMeta.SensorType is SensorType.RGGB
            && processedRawImage.ChannelCount == 1;

        if (isRawBayer)
        {
            return await ComputeBayerStretchStatsAsync(processedRawImage, cancellationToken);
        }

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
    /// Computes per-channel stretch stats from a raw Bayer mosaic.
    /// Uses the existing histogram-based statistics on the full raw channel (which is a mix
    /// of all Bayer sub-channels), then replicates to all 3 RGB channels.
    /// This gives a good stretch approximation — the GPU shader handles the actual per-pixel
    /// color separation during bilinear debayer.
    /// </summary>
    private static Task<(ChannelStretchStats[] PerChannelStats, ChannelStretchStats? LumaStats, float[] PerChannelBg, float LumaBg)> ComputeBayerStretchStatsAsync(
        Image rawImage, CancellationToken cancellationToken)
    {
        // Use the existing robust histogram-based stats on channel 0 (the raw mosaic).
        // The histogram naturally mixes R/G/G/B pixels — since the background level is similar
        // for all channels, the blended median/MAD gives a good stretch baseline.
        var (ped, med, mad) = rawImage.GetPedestralMedianAndMADScaledToUnit(0);
        var stats = new ChannelStretchStats(ped, med, mad);

        // Replicate to all 3 channels — the GPU debayer will produce slightly different
        // R/G/B values but the stretch parameters are close enough for a good result.
        var perChannelStats = new[] { stats, stats, stats };
        var lumaStats = stats;

        Span<float> pedestals = stackalloc float[1];
        pedestals[0] = ped;
        var (perChannelBg, lumaBg) = rawImage.ScanBackgroundRegion(pedestals);
        var bg3 = new[] { perChannelBg[0], perChannelBg[0], perChannelBg[0] };

        return Task.FromResult((perChannelStats, (ChannelStretchStats?)lumaStats, bg3, lumaBg));
    }

    /// <summary>
    /// Resolves a <see cref="LumaWeighting"/> profile to the concrete (R,G,B) triple stored
    /// in <see cref="StretchUniforms.LumaWeights"/>. For <see cref="LumaWeighting.SensorMatched"/>
    /// queries <see cref="FilterCurveDatabase"/> for the sensor's broadband response;
    /// falls back to Rec.709 if the database is not loaded or the sensor name cannot be matched.
    /// </summary>
    public (float R, float G, float B) ResolveLumaWeights(LumaWeighting weighting)
    {
        if (weighting is LumaWeighting.SensorMatched
            && FilterCurveDatabase.TryComputeSensorLumaWeights(UnstretchedImage.ImageMeta, out var sensorW))
        {
            return sensorW;
        }
        return weighting.Weights;
    }

    /// <summary>
    /// Computes stretch shader uniforms for the current stretch mode and parameters.
    /// Optional knobs mirror the SetiAstro UX: luma weighting profile (Rec.709/601/2020/SensorMatched),
    /// luma-vs-linked blend (only meaningful when <paramref name="mode"/> is
    /// <see cref="StretchMode.Luma"/>), and post-stretch normalize.
    /// </summary>
    public StretchUniforms ComputeStretchUniforms(
        StretchMode mode,
        StretchParameters parameters,
        LumaWeighting weighting = LumaWeighting.Rec709,
        float lumaBlend = 1f,
        bool normalize = false,
        int curvesMode = 0,
        System.ReadOnlySpan<float> curveLut = default,
        float curvesBoost = 0f,
        float curvesMidpoint = 0.25f,
        float hdrAmount = 0f,
        float hdrKnee = 0.8f)
    {
        var stats = PerChannelStats;
        var luma = LumaStats;
        var factor = parameters.Factor;
        var clipping = parameters.ShadowsClipping;
        var weights = ResolveLumaWeights(weighting);

        if (UseIterativeConvergence && StarMaskedStats is { } masked)
        {
            // Star-masked stats have a lower median (stars excluded), so a fixed
            // stretchFactor under-stretches. Convergence compensates by adjusting
            // the factor to hit the target median regardless of which stats are used.
            stats = masked;
            luma = StarMaskedLumaStats ?? luma;

            var convStats = luma ?? stats[0];
            var hist = ChannelStatistics.Length > 0 ? ChannelStatistics[0] : null;
            if (hist is not null)
            {
                // For luma convergence the WB scalar is the weighting-profile-weighted
                // average; for channel-0 fallback it's wb.R. The per-channel rendering
                // scales stats by the same factor inside ComputeStretchUniforms, so
                // convergence and rendering operate in matched coordinate spaces.
                var wbScalar = ColorCalibration is { } wb
                    ? (luma is not null ? weights.R * wb.R + weights.G * wb.G + weights.B * wb.B : wb.R)
                    : 1f;

                (factor, _) = Image.ConvergeStretchFactor(
                    hist, convStats.Pedestal, convStats.Median, convStats.Mad,
                    factor, clipping, ConvergenceTarget, whiteBalance: wbScalar);
            }
        }

        var uniforms = ComputeStretchUniforms(mode, new StretchParameters(factor, clipping), stats, luma, UnstretchedImage.MaxValue, ColorCalibration, weights);
        if (BackgroundNeutralization is { } bn)
        {
            uniforms = uniforms with { BackgroundNeutralization = bn };
        }
        if (lumaBlend != 1f)
        {
            uniforms = uniforms with { LumaBlend = System.Math.Clamp(lumaBlend, 0f, 1f) };
        }
        if (normalize)
        {
            var scale = Image.PredictPostStretchMaxScale(
                uniforms,
                ChannelStatistics.AsSpan(),
                curvesMode: curvesMode,
                curveLut: curveLut,
                curvesBoost: curvesBoost,
                curvesMidpoint: curvesMidpoint,
                hdrAmount: hdrAmount,
                hdrKnee: hdrKnee);
            uniforms = uniforms with { NormalizeScale = scale };
        }
        return uniforms;
    }

    /// <summary>
    /// Computes stretch shader uniforms from stats directly — no <see cref="AstroImageDocument"/> needed.
    /// When <paramref name="whiteBalance"/> is non-null, per-channel stats are scaled by the WB
    /// multipliers before deriving shadows/midtones/rescale, so the shadow clip lands in the
    /// same coordinate space as the post-WB norm in the GLSL stretch loop. Without this
    /// adjustment, channels reduced by WB (e.g. B with wb=0.94) would have their post-WB norm
    /// fall below the un-adjusted shadow and clamp to zero, tinting the bg toward the boosted
    /// channels.
    /// </summary>
    public static StretchUniforms ComputeStretchUniforms(
        StretchMode mode,
        StretchParameters parameters,
        ChannelStretchStats[] perChannelStats,
        ChannelStretchStats? lumaStats,
        float imageMaxValue,
        (float R, float G, float B)? whiteBalance = null,
        (float R, float G, float B)? lumaWeights = null)
    {
        // Default luma weighting is Rec.709 — matches the previous hardcoded constants
        // and keeps existing callers (no lumaWeights argument) on the same numerical path.
        var weights = lumaWeights ?? (0.2126f, 0.7152f, 0.0722f);

        if (mode is StretchMode.None)
        {
            return new StretchUniforms(StretchMode.None, 1f, default, default, default, default, default)
            { LumaWeights = weights };
        }

        var normFactor = imageMaxValue > 1.0f + float.Epsilon ? 1f / imageMaxValue : 1f;
        var factor = parameters.Factor;
        var clipping = parameters.ShadowsClipping;
        var wb = whiteBalance ?? (1f, 1f, 1f);

        if (mode is StretchMode.Luma && lumaStats is { } luma)
        {
            // Luma stretch uses the chosen luminance weighting over the WB-adjusted channels;
            // scale luma median/mad by the weighted WB so the luma stretch aligns with the
            // actual luminance values produced post-WB.
            var lumaWb = weights.R * wb.R + weights.G * wb.G + weights.B * wb.B;
            var (s, m, h, r) = Image.ComputeStretchParameters(luma.Median * lumaWb, luma.Mad * lumaWb, factor, clipping);

            // Use per-channel pedestals for background subtraction (avoids green cast from RGGB);
            // luma-derived midtone/shadows/rescale live in LumaStretch.
            var chStats = perChannelStats;
            var ped0 = chStats.Length > 0 ? chStats[0].Pedestal : luma.Pedestal;
            var ped1 = chStats.Length > 1 ? chStats[1].Pedestal : ped0;
            var ped2 = chStats.Length > 2 ? chStats[2].Pedestal : ped0;

            // Always compute per-channel linked params alongside the luma scalar so that a
            // LumaBlend < 1 caller has the linked branch ready in the shader without a UBO
            // re-upload. Falls back to channel 0 if a stat slot is missing.
            var lch0 = chStats.Length > 0 ? chStats[0] : new ChannelStretchStats(0f, luma.Median, luma.Mad);
            var lch1 = chStats.Length > 1 ? chStats[1] : lch0;
            var lch2 = chStats.Length > 2 ? chStats[2] : lch0;
            var lp0 = Image.ComputeStretchParameters(lch0.Median * wb.R, lch0.Mad * wb.R, factor, clipping);
            var lp1 = Image.ComputeStretchParameters(lch1.Median * wb.G, lch1.Mad * wb.G, factor, clipping);
            var lp2 = Image.ComputeStretchParameters(lch2.Median * wb.B, lch2.Mad * wb.B, factor, clipping);

            return new StretchUniforms(
                Mode: StretchMode.Luma,
                NormFactor: normFactor,
                Pedestal: (ped0, ped1, ped2),
                Shadows: ((float)lp0.Shadows, (float)lp1.Shadows, (float)lp2.Shadows),
                Midtones: ((float)lp0.Midtones, (float)lp1.Midtones, (float)lp2.Midtones),
                Highlights: ((float)lp0.Highlights, (float)lp1.Highlights, (float)lp2.Highlights),
                Rescale: ((float)lp0.Rescale, (float)lp1.Rescale, (float)lp2.Rescale))
            {
                WhiteBalance = wb,
                LumaWeights = weights,
                LumaStretch = ((float)s, (float)m, (float)r),
            };
        }

        // Linked or unlinked
        var stats = perChannelStats;
        var ch0 = stats.Length > 0 ? stats[0] : default;
        var ch1 = stats.Length > 1 ? stats[1] : ch0;
        var ch2 = stats.Length > 2 ? stats[2] : ch0;

        if (mode is StretchMode.Linked)
        {
            ch1 = ch0;
            ch2 = ch0;
        }

        // WB scales each channel's value range linearly: post-WB_median = wb * pre-WB_median,
        // post-WB_mad = wb * pre-WB_mad. Compute stretch params from those scaled stats so
        // shadows/rescale/midtones are consistent with the post-WB norm the shader sees.
        var p0 = Image.ComputeStretchParameters(ch0.Median * wb.R, ch0.Mad * wb.R, factor, clipping);
        var p1 = Image.ComputeStretchParameters(ch1.Median * wb.G, ch1.Mad * wb.G, factor, clipping);
        var p2 = Image.ComputeStretchParameters(ch2.Median * wb.B, ch2.Mad * wb.B, factor, clipping);

        return new StretchUniforms(
            Mode: mode,
            NormFactor: normFactor,
            Pedestal: (ch0.Pedestal, ch1.Pedestal, ch2.Pedestal),
            Shadows: ((float)p0.Shadows, (float)p1.Shadows, (float)p2.Shadows),
            Midtones: ((float)p0.Midtones, (float)p1.Midtones, (float)p2.Midtones),
            Highlights: ((float)p0.Highlights, (float)p1.Highlights, (float)p2.Highlights),
            Rescale: ((float)p0.Rescale, (float)p1.Rescale, (float)p2.Rescale))
        { WhiteBalance = wb, LumaWeights = weights };
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

            // Recompute stretch stats with star mask exclusion
            if (stars.StarMask is { } mask)
            {
                var imageChannelCount = UnstretchedImage.ChannelCount;
                var maskedStats = new ChannelStretchStats[PerChannelStats.Length];
                for (var c = 0; c < maskedStats.Length; c++)
                {
                    if (c < imageChannelCount)
                    {
                        var (p, m, madd) = UnstretchedImage.GetStarMaskedMedianAndMADScaledToUnit(c, mask);
                        maskedStats[c] = new ChannelStretchStats(p, m, madd);
                    }
                    else
                    {
                        // Bayer images replicate channel 0 stats to all 3 RGB slots
                        maskedStats[c] = maskedStats[0];
                    }
                }
                StarMaskedStats = maskedStats;

                if (LumaStats is not null)
                {
                    StarMaskedLumaStats = maskedStats[0];
                }
            }
        }
    }

    /// <summary>
    /// Computes Tycho-2 photometric color calibration. Requires plate-solved WCS and detected stars.
    /// Returns the number of matched stars (0 if calibration failed or wasn't attempted).
    /// </summary>
    public async Task<(int MatchCount, string? Diag)> ComputeColorCalibrationAsync(ICelestialObjectDB db, CancellationToken cancellationToken = default)
    {
        if (ColorCalibration.HasValue) return (0, null);
        if (Stars is not { Count: >= 5 } starList) return (0, "Need ≥5 stars");
        if (starList.StarMask is not { } mask) return (0, "No star mask");

        var calibrateImage = UnstretchedImage;
        if (calibrateImage.ChannelCount < 3 && calibrateImage.ImageMeta.SensorType is SensorType.RGGB)
            calibrateImage = await calibrateImage.DebayerAsync(DebayerAlgorithm, cancellationToken: cancellationToken);

        if (calibrateImage.ChannelCount < 3) return (0, "Need color image");

        var wb = await Task.Run(() => ComputeSkyBackgroundWB(calibrateImage, mask), cancellationToken);

        if (wb is not { } w)
            return (0, "No valid bg samples");

        ColorCalibration = (w.R, w.G, w.B);
        var diag = $"skyBg R={w.R:F3} B={w.B:F3}";
        return (1, diag);
    }

    /// <summary>
    /// Computes background neutralization gains from the darkest spatial region.
    /// Results flow through the GPU shader as <see cref="StretchUniforms.BackgroundNeutralization"/>.
    /// </summary>
    public (float R, float G, float B)? ComputeBackgroundNeutralization()
    {
        if (PerChannelBackground is not { Length: >= 3 } bg)
            return null;

        var gains = Lib.Imaging.BackgroundNeutralization.ComputeGains(bg);
        if (Math.Abs(gains.R - 1f) < 0.001f && Math.Abs(gains.G - 1f) < 0.001f && Math.Abs(gains.B - 1f) < 0.001f)
            return null; // effectively no neutralization needed

        BackgroundNeutralization = gains;
        return gains;
    }

    /// <summary>
    /// Computes spectrophotometric color calibration via Pickles SEDs + system throughput.
    /// Falls back to sky-background method if SPCC can't run (no plate solve, no filter data).
    /// </summary>
    public async Task<(int MatchCount, string? Diag)> ComputeSpccColorCalibrationAsync(
        ICelestialObjectDB db, CancellationToken cancellationToken = default)
    {
        if (ColorCalibration.HasValue) return (0, null);
        if (Stars is not { Count: >= 3 } starList) return (0, "Need ≥3 stars");
        if (Wcs is not { HasCDMatrix: true } wcs) return (0, "Need plate-solved WCS");
        if (!FilterCurveDatabase.IsLoaded) return (0, "Filter curve DB not loaded");

        var calibrateImage = UnstretchedImage;
        if (calibrateImage.ChannelCount < 3 && calibrateImage.ImageMeta.SensorType is SensorType.RGGB)
            calibrateImage = await calibrateImage.DebayerAsync(DebayerAlgorithm, cancellationToken: cancellationToken);

        if (calibrateImage.ChannelCount < 3) return (0, "Need color image");

        var meta = calibrateImage.ImageMeta;
        var channels = await Task.Run(() => FilterCurveDatabase.BuildChannelThroughputs(meta), cancellationToken);
        if (channels is null)
            return (0, $"No throughput for {meta.Instrument}/{meta.SensorModel}/{meta.Filter.FilterNameForFits}");
        var (tsysR, tsysG, tsysB) = channels.Value;

        var result = await Task.Run(() =>
            Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance(
                calibrateImage, starList, wcs, db, tsysR, tsysG, tsysB, minStars: 3),
            cancellationToken);

        if (result is not { } r)
            return (0, "Insufficient SPCC matches");

        ColorCalibration = (r.R, r.G, r.B);
        var diag = $"SPCC R={r.R:F3} B={r.B:F3} ({r.MatchCount} stars)";
        return (r.MatchCount, diag);
    }

    /// <summary>
    /// Samples the darkest 10% of non-star pixels to find the true sky background color.
    /// Stars and nebulae are brighter and get excluded by the percentile threshold,
    /// so only true sky background contributes to the color estimate.
    /// </summary>
    private static (float R, float G, float B)? ComputeSkyBackgroundWB(Image image, BitMatrix starMask)
    {
        var (_, width, height) = image.Shape;
        var x0 = width / 20; var x1 = width - x0;   // skip 5% border
        var y0 = height / 20; var y1 = height - y0;
        var maxSamples = width * height / 16;
        var sr = new float[maxSamples]; var sg = new float[maxSamples]; var sb = new float[maxSamples];
        var yBuf = new float[maxSamples];
        var n = 0;

        for (var y = y0; y < y1 && n < maxSamples; y += 4)
            for (var x = x0; x < x1 && n < maxSamples; x += 4)
            {
                if (starMask[y, x]) continue;
                var r = image[0, y, x]; var g = image[1, y, x]; var b = image[2, y, x];
                if (float.IsNaN(r) || float.IsNaN(g) || float.IsNaN(b)) continue;
                yBuf[n] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                sr[n] = r; sg[n] = g; sb[n] = b;
                n++;
            }

        if (n < 100) return null;

        // Sort by luminance to find darkest 10% pixel indices
        var idx = new int[n]; for (var i = 0; i < n; i++) idx[i] = i;
        Array.Sort(yBuf, idx, 0, n);
        var k = Math.Max(n / 10, 10);

        // Collect RGB values of darkest k pixels and median-filter
        var darkR = new float[k]; var darkG = new float[k]; var darkB = new float[k];
        for (var i = 0; i < k; i++) { darkR[i] = sr[idx[i]]; darkG[i] = sg[idx[i]]; darkB[i] = sb[idx[i]]; }
        Array.Sort(darkR); Array.Sort(darkG); Array.Sort(darkB);

        var medR = darkR[k / 2]; var medG = darkG[k / 2]; var medB = darkB[k / 2];
        if (medG <= 1e-7f) return null;

        return (Math.Clamp(medG / medR, 0.5f, 2f), 1f, Math.Clamp(medG / medB, 0.5f, 2f));
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
