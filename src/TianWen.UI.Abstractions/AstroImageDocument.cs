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
public sealed class AstroImageDocument : IPreviewSource
{
    /// <summary>Supported file extensions for the image viewer. <c>.ser</c> is a multi-frame planetary
    /// video handled by <see cref="SerPreviewSource"/> (not this document's file loader), but it is listed
    /// here so folder scan / drag-drop / the file dialog accept it; <see cref="ViewerController"/> routes it.
    /// <c>.fz</c> is an fpack tile-compressed FITS and loads through the same FITS path as the rest.</summary>
    public static readonly ImmutableArray<string> SupportedExtensions = [".fits", ".fit", ".fts", ".fz", ".tif", ".tiff", ".cr2", ".cr3", ".ser"];

    /// <summary>Glob patterns matching all supported file extensions (for folder scanning).</summary>
    public static readonly ImmutableArray<string> SupportedPatterns = [.. SupportedExtensions.Select(ext => "*" + ext)];

    /// <summary>File dialog filter definitions.</summary>
    public static readonly (string Name, string[] Extensions)[] FileDialogFilters =
    [
        ("FITS files", [".fits", ".fit", ".fts", ".fz"]),
        ("TIFF files", [".tif", ".tiff"]),
        ("Canon RAW", [".cr2", ".cr3"]),
        ("SER video", [".ser"]),
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
    /// Per-channel background values the DISPLAY is solved from (pedestal-subtracted): the average
    /// values of the darkest spatial region, ready to feed into <see cref="Image.StretchValue"/> to get
    /// the post-stretch background level -- or the anchor's, while one is held (see
    /// <see cref="DisplayAnchor"/>).
    /// </summary>
    public float[] PerChannelBackground => Basis._perChannelBackground;

    /// <summary>Luminance background the display is solved from (pedestal-subtracted).</summary>
    public float LumaBackground => Basis._lumaBackground;

    /// <summary>
    /// This frame's OWN measured background, whatever it is being displayed with. Read by the info
    /// panel: a readout is a measurement of the frame in front of the user, and quietly reporting the
    /// anchor's numbers there would be a lie -- the display consistency the carry buys is worth having
    /// only because the readout stays honest about what differs.
    /// </summary>
    public float[] MeasuredPerChannelBackground => _perChannelBackground;

    /// <summary>This frame's own measured luminance background. See <see cref="MeasuredPerChannelBackground"/>.</summary>
    public float MeasuredLumaBackground => _lumaBackground;

    private float[] _perChannelBackground;
    private float _lumaBackground;

    /// <summary>
    /// Another document whose display statistics this one is rendered with, or <c>null</c> to use its
    /// own. Set by the viewer while stepping through comparable frames of one folder; see
    /// <see cref="DisplayCarry"/> for what it buys and why the anchor is a document rather than a
    /// snapshot of its numbers.
    /// </summary>
    /// <remarks>
    /// It is a display concern only: the pixels, the star list, the WCS and
    /// <see cref="MeasuredPerChannelBackground"/> are always this frame's own.
    /// </remarks>
    public AstroImageDocument? DisplayAnchor
    {
        get => _displayAnchor;
        // Never itself. Every accessor below takes exactly ONE hop through the anchor, which is what
        // keeps a cycle unrepresentable rather than merely unlikely; a self-reference would defeat the
        // hop silently instead of looping, and is what an over-eager reconcile would write.
        set => _displayAnchor = ReferenceEquals(value, this) ? null : value;
    }

    private AstroImageDocument? _displayAnchor;

    /// <summary>
    /// The document the DISPLAY numbers come from: the anchor when one is held, else this frame. Every
    /// read through it is a field access, so the single hop can never become a chain.
    /// </summary>
    private AstroImageDocument Basis => _displayAnchor ?? this;

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

    /// <summary>
    /// White balance multipliers from Tycho-2 / SPCC color calibration; null until computed. While a
    /// <see cref="DisplayAnchor"/> is held its triple wins, so the fit runs ONCE per run of comparable
    /// frames instead of once per file -- which is the half of P19 the user asked for as "so that they
    /// load faster". The frame's own measurement, if it has one, stays as the fallback.
    /// </summary>
    public (float R, float G, float B)? ColorCalibration => Basis._colorCalibration ?? _colorCalibration;

    private (float R, float G, float B)? _colorCalibration;

    /// <summary>
    /// Provenance for <see cref="ColorCalibration"/>: which method produced it, how many stars it
    /// stood on, and what it declared white. Null until a calibration has run.
    /// <para>
    /// Set on the SAME assignments as the triple, so the two can never describe different runs. The
    /// numbers used to exist only inside a formatted diagnostic string that went to the log and was
    /// then dropped, which left the UI able to show a multiplier but not a single thing about where
    /// it came from.
    /// </para>
    /// </summary>
    public ColorCalibrationSummary? ColorCalibrationSummary => Basis._colorCalibrationSummary ?? _colorCalibrationSummary;

    private ColorCalibrationSummary? _colorCalibrationSummary;

    /// <summary>
    /// Carries <paramref name="from"/>'s colour calibration and its provenance over to this document,
    /// for a document derived from it by a spatial operation (the AI enhance). The enhanced raster is a
    /// different image with its own star list, so the calibration auto-retrigger would otherwise re-fit
    /// SPCC on deconvolved, denoised and recombined pixels and land on a different triple: the frame
    /// rendered right for a moment under the inherited nothing and then took on a new cast when the
    /// re-fit arrived. The measurement made on the original stars is the trustworthy one, and a spatial
    /// operation does not change what white is. Only the WB triple travels; background neutralisation is
    /// re-solved per document, because an enhanced background IS different (the same "share only the WB"
    /// rule the stacking pipeline's plates follow).
    /// </summary>
    public void InheritColorCalibration(AstroImageDocument from)
    {
        if (from.ColorCalibration is not { } wb)
        {
            return;
        }
        InheritColorCalibration(wb, from.ColorCalibrationSummary);
    }

    /// <summary>
    /// Installs a colour calibration measured on another frame, with the provenance that goes with it.
    /// The document overload above is the caller for it; it is separate so a triple that never came
    /// from a loaded document -- a test's, or a future sidecar's -- has one way in rather than a second
    /// assignment path that could set the triple without its summary.
    /// </summary>
    internal void InheritColorCalibration((float R, float G, float B) whiteBalance, ColorCalibrationSummary? summary)
    {
        _colorCalibration = whiteBalance;
        _colorCalibrationSummary = summary;
    }

    // SPCC in-flight gate. The compute task runs on a thread-pool thread and
    // writes 0 from its finally; Render reads + tries to set 1 from the UI
    // thread. Plain bool would give no memory-ordering guarantee across the
    // boundary AND would race the check-then-set on the UI side, so we use
    // Interlocked + an int (per the project's "Single-flag in-flight gate"
    // convention).
    private int _colorCalibrationInFlight;

    /// <summary>True while an SPCC / Tycho-2 calibration task is in flight for
    /// this document.</summary>
    public bool ColorCalibrationInFlight => Volatile.Read(ref _colorCalibrationInFlight) != 0;

    /// <summary>Atomically claims the in-flight slot. Returns true if the caller
    /// won the race and should start the task; false if another caller is
    /// already running it.</summary>
    public bool TryBeginColorCalibration() =>
        Interlocked.CompareExchange(ref _colorCalibrationInFlight, 1, 0) == 0;

    /// <summary>Marks the SPCC task as no longer in flight. Always called from
    /// the task's finally so a faulted compute still releases the slot. Also records
    /// that an attempt HAPPENED (<see cref="ColorCalibrationAttempted"/>), so the
    /// per-frame auto-retrigger fires once per document rather than every frame when
    /// the fit cannot succeed.</summary>
    public void EndColorCalibration()
    {
        ColorCalibrationAttempted = true;
        Volatile.Write(ref _colorCalibrationInFlight, 0);
    }

    /// <summary>
    /// True once a calibration has been attempted on this document, whether it landed a triple or not.
    /// The auto-retrigger checks it so a document the fit cannot calibrate -- a starless comet layer has
    /// too few stars, and SPCC can decline -- is not re-attempted on every frame, which flickered the
    /// SPCC button's in-flight state. An explicit press (button / W) does not consult it, so the user can
    /// always retry; it resets naturally because a new document is a new instance.
    /// </summary>
    public bool ColorCalibrationAttempted { get; private set; }

    /// <summary>Background neutralization gains from pivot1 sampling (1,1,1) = no neutralization.</summary>
    public (float R, float G, float B)? BackgroundNeutralization { get; set; }

    // --- IPreviewSource (a still image is a single frame) ---
    // ChannelStatistics / PerChannelBackground / LumaBackground / ComputeStretchUniforms above already
    // satisfy the interface implicitly; the geometry, per-frame data, and frame-nav members are explicit
    // so they don't widen this class's public API (the renderer reaches them via the IPreviewSource ref).
    int IPreviewSource.Width => UnstretchedImage.Width;
    int IPreviewSource.Height => UnstretchedImage.Height;
    int IPreviewSource.ChannelCount => UnstretchedImage.ChannelCount;
    SensorType IPreviewSource.SensorType => UnstretchedImage.ImageMeta.SensorType;
    int IPreviewSource.BayerOffsetX => UnstretchedImage.ImageMeta.BayerOffsetX;
    int IPreviewSource.BayerOffsetY => UnstretchedImage.ImageMeta.BayerOffsetY;
    ReadOnlySpan<float> IPreviewSource.GetChannelData(int channel) => UnstretchedImage.GetChannelSpan(channel);
    int IPreviewSource.FrameCount => 1;
    int IPreviewSource.FrameIndex => 0;
    bool IPreviewSource.SelectFrame(int index) => false;
    bool IPreviewSource.HasTimestamps => false;
    DateTimeOffset IPreviewSource.TimestampOf(int index) => DateTimeOffset.MinValue;

    /// <summary>Per-method cache of computed background-neutralization gains. Switching
    /// method on a loaded document is a dict lookup + uniform write, not a recompute.
    /// Populated lazily by <see cref="ComputeBackgroundNeutralization"/>.</summary>
    private readonly System.Collections.Generic.Dictionary<(Lib.Imaging.BackgroundNeutralizationMethod Method, (float R, float G, float B) WhiteBalance, float[] Background), (float R, float G, float B)> _bnGainsByMethod = new();

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
        _perChannelBackground = perChannelBackground;
        _lumaBackground = lumaBackground;
        Wcs = wcs;
        IsPreStretched = isPreStretched;

        var stats = new ImageHistogram[image.ChannelCount];
        for (var c = 0; c < image.ChannelCount; c++)
        {
            stats[c] = image.Statistics(c);
        }
        ChannelStatistics = stats;

        // D1: the stats pass was the last thing that needed the float planes at load, and for a
        // document whose source was 8-bit they are pure duplication of a raster that can rebuild them
        // in memory. Declined for anything without one, so a float master is untouched.
        image.TryEvictFloatPlanes();
    }

    /// <summary>
    /// Creates a document from an in-memory <see cref="Image"/> (e.g. from the live
    /// session capture). The document <b>adopts</b> the image: its pixel arrays are
    /// rescaled in place to <c>[0, 1]</c> via <see cref="Image.ScaleFloatValuesToUnitInPlace"/>
    /// to feed the histogram-based stretch stats without re-allocating the canvas.
    /// </summary>
    /// <remarks>
    /// The caller must not retain or use <paramref name="image"/> after this call; 
    /// its pixel data has been mutated to a normalised <c>[0, 1]</c> range while the
    /// original <see cref="Image.MaxValue"/> field is no longer consistent with the
    /// underlying samples on the source instance. Pass a freshly constructed image
    /// you own, or build a document via the file-loading overload instead.
    /// <para><b>Frame ownership: convention 4, a CONSUMED input</b> -- which is what the
    /// <c>Adopt</c> in the name states, per the rule that ownership transfer is visible in the name.
    /// See the frame-ownership notes on <see cref="Image"/>.</para>
    /// </remarks>
    public static async Task<AstroImageDocument> AdoptImageAsync(Image image, DebayerAlgorithm algorithm = DebayerAlgorithm.AHD, WCS? wcs = null, string filePath = "", CancellationToken cancellationToken = default)
    {
        // For Bayer images: skip CPU debayer but normalize to [0,1] so stretch stats
        // match the existing histogram-based computation. The GPU shader does bilinear debayer.
        Image viewImage;
        DebayerAlgorithm actualAlgorithm;
        if (image.ImageMeta.SensorType is SensorType.RGGB && algorithm is not DebayerAlgorithm.None)
        {
            // Normalize to [0,1] but don't debayer: GPU shader handles debayer. Asks Image for the
            // verdict rather than re-spelling it, so this and the histogram's float path cannot
            // disagree about what counts as already-normalised.
            viewImage = image.HasUnitScalePeak
                ? image
                : image.ScaleFloatValuesToUnitInPlace();
            actualAlgorithm = algorithm;
        }
        else
        {
            // Mono/color: normalize in place (no extra allocation)
            viewImage = image.HasUnitScalePeak
                ? image
                : image.ScaleFloatValuesToUnitInPlace();
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
            DetectPreStretched(viewImage, perChannelStats));
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
            || ext.Equals(".fts", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fz", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenFitsAsync(filePath, algorithm, cancellationToken);
        }

        // TIFF, CR2, CR3: pure-managed: DIR.Lib TiffReader for TIFF,
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

        return await AdoptImageAsync(rawImage, algorithm, fileWcs, filePath, cancellationToken);
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

    /// <summary>
    /// Whether the pixels have already been through a transfer function, decided from the
    /// WHOLE-IMAGE median this method is handed rather than from a sample.
    ///
    /// <para>Every FITS reaches the viewer through <see cref="AdoptImageAsync"/>, which passed a
    /// hardcoded <c>false</c> here -- so for the entire FITS path this was never detected at all, and
    /// only TIFF/PNG/raw (which run <see cref="Image.DetectPreStretched"/> in OpenImageFileAsync)
    /// ever got it right. A scanned photographic plate, or any already-stretched FITS, therefore
    /// opened with the screen stretch applied ON TOP of a stretch and rendered nearly black.</para>
    ///
    /// <para>The threshold matches <see cref="Image.DetectPreStretched"/> deliberately -- a linear
    /// frame sits near its black point, so a median above 0.2 is not something a linear sub does --
    /// but the INPUT is better. That method medians 1024 contiguous samples from the middle of the
    /// buffer, i.e. one strip across the frame centre, which a bright object dead centre can drag
    /// over the threshold on a perfectly linear image. The median here is the real one, already
    /// computed for the stretch and free to reuse.</para>
    ///
    /// <para>Channel 0 alone, and only for a Bayer mosaic would that be a mosaic channel -- where it
    /// is still the right answer, since a CFA plane has the same distribution as the frame.</para>
    /// </summary>
    private static bool DetectPreStretched(Image image, ChannelStretchStats[] perChannelStats)
        // Bit depth first, for the reason given on CarriesDisplayDataOnly: it is a fact about the
        // container, where the median is an inference from content. Same predicate the TIFF/PNG path
        // consults through Image.DetectPreStretched, so the two cannot answer differently for the
        // same file.
        => image.BitDepth.CarriesDisplayDataOnly
            || (perChannelStats.Length > 0 && perChannelStats[0].Median > 0.2f);

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
        var perChannelStats = StretchSolver.CollectPerChannelStats(processedRawImage, channelCount);

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
    /// This gives a good stretch approximation: the GPU shader handles the actual per-pixel
    /// color separation during bilinear debayer.
    /// </summary>
    private static Task<(ChannelStretchStats[] PerChannelStats, ChannelStretchStats? LumaStats, float[] PerChannelBg, float LumaBg)> ComputeBayerStretchStatsAsync(
        Image rawImage, CancellationToken cancellationToken)
    {
        // Use the existing robust histogram-based stats on channel 0 (the raw mosaic).
        // The histogram naturally mixes R/G/G/B pixels, since the background level is similar
        // for all channels, the blended median/MAD gives a good stretch baseline.
        var (ped, med, mad) = rawImage.GetPedestralMedianAndMADScaledToUnit(0);
        var stats = new ChannelStretchStats(ped, med, mad);

        // Replicate to all 3 channels: the GPU debayer will produce slightly different
        // R/G/B values but the stretch parameters are close enough for a good result.
        var perChannelStats = new[] { stats, stats, stats };
        var lumaStats = stats;

        Span<float> pedestals = stackalloc float[1];
        pedestals[0] = ped;
        var (perChannelBg, lumaBg) = rawImage.ScanBackgroundRegion(pedestals);
        // ScanBackgroundRegion already demosaics 1-channel RGGB into proper
        // per-channel (R, G, B) medians for Bayer images. Use them directly --
        // replicating bg[0] (just R) here would discard G and B, giving
        // ComputeGains an all-identical input and producing (1,1,1) gains so
        // bg neutralization had no visible effect on raw Bayer lights.
        var bg3 = perChannelBg.Length >= 3
            ? new[] { perChannelBg[0], perChannelBg[1], perChannelBg[2] }
            : new[] { perChannelBg[0], perChannelBg[0], perChannelBg[0] };

        return Task.FromResult((perChannelStats, (ChannelStretchStats?)lumaStats, bg3, lumaBg));
    }

    /// <summary>
    /// Resolves a <see cref="LumaWeighting"/> profile to the concrete (R,G,B) triple stored
    /// in <see cref="StretchUniforms.LumaWeights"/>. For <see cref="LumaWeighting.SensorMatched"/>
    /// queries <see cref="FilterCurveDatabase"/> for the sensor's broadband response;
    /// falls back to Rec.709 if the database is not loaded or the sensor name cannot be matched.
    /// </summary>
    public (float R, float G, float B) ResolveLumaWeights(LumaWeighting weighting)
        => StretchSolver.ResolveLumaWeights(weighting, UnstretchedImage.ImageMeta);

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
        float hdrKnee = 0.8f,
        float bgNeutralizationStrength = 1f,
        (float R, float G, float B)? manualWhiteBalance = null,
        bool applyColorCalibration = true)
    {
        // Through Basis, not `this`: while a DisplayAnchor is held every frame of the run is solved
        // from ONE set of statistics, which is what stops a step between two subs of the same field
        // from re-solving the auto-stretch and flickering. Basis is `this` when nothing is held, so a
        // single open is unchanged.
        var stats = Basis.PerChannelStats;
        var luma = Basis.LumaStats;
        var factor = parameters.Factor;
        var clipping = parameters.ShadowsClipping;
        var weights = ResolveLumaWeights(weighting);

        // White balance enters the pipeline two ways, and manual vs auto WB use them differently:
        //   * stat scaling  -- the AUTO calibration (ColorCalibration) scales the per-channel stats so the
        //     shadow clip stays in the post-WB coordinate space (keeps the background neutral; see the
        //     static overload's doc). This is what makes auto calibration a colour *correction*.
        //   * shader multiply -- uniforms.WhiteBalance, applied per channel in the GLSL/CPU stretch.
        // If the MANUAL WB also scaled the stats, a per-channel auto-normalised stretch (Unlinked / the
        // SER linear default) would re-derive each channel's curve from the scaled stats and *cancel* the
        // multiplier -- the slider would appear to do nothing. So manual WB is applied ONLY as the shader
        // multiply (autoWb scales the stats; autoWb x manual is the multiply), giving a direct, always-
        // visible colour shift. A neutral/null manual triple leaves shaderWb == autoWb, so the existing
        // auto-only numeric path is bit-identical.
        // The toggle gates the RENDER here, not the measurement: switching SPCC off must show the frame
        // as if no calibration existed, and switching it on again must bring the same triple back
        // without a re-fit. It used to gate only the toolbar highlight and the manual-WB stash, so
        // "turning SPCC off" changed nothing on screen.
        var autoWb = applyColorCalibration ? ColorCalibration : null;
        var shaderWb = StretchSolver.ComposeWhiteBalance(autoWb, manualWhiteBalance);

        // Resolve the Auto intent here, where both inputs are known: whether this frame is colour, and
        // whether a calibration is actually being applied to it. A calibrated colour frame renders
        // Linked so the WB shows; an uncalibrated one Unlinked so each channel's background neutralises.
        var isColour = UnstretchedImage.ChannelCount >= 3
            || UnstretchedImage.ImageMeta.SensorType is SensorType.RGGB;
        mode = mode.ResolveAuto(isColour, autoWb is not null);

        if (UseIterativeConvergence && Basis.StarMaskedStats is { } masked)
        {
            // Star-masked stats have a lower median (stars excluded), so a fixed
            // stretchFactor under-stretches. Convergence compensates by adjusting
            // the factor to hit the target median regardless of which stats are used.
            stats = masked;
            luma = Basis.StarMaskedLumaStats ?? luma;

            var convStats = luma ?? stats[0];
            var hist = Basis.ChannelStatistics.Length > 0 ? Basis.ChannelStatistics[0] : null;
            if (hist is not null)
            {
                // For luma convergence the WB scalar is the weighting-profile-weighted
                // average; for channel-0 fallback it's wb.R. Convergence operates in the
                // stat-scaled coordinate space, which only the AUTO WB scales, so this
                // uses autoWb (manual WB is a pure shader multiply and never scales stats).
                var wbScalar = autoWb is { } wb
                    ? (luma is not null ? weights.R * wb.R + weights.G * wb.G + weights.B * wb.B : wb.R)
                    : 1f;

                (factor, _) = Image.ConvergeStretchFactor(
                    hist, convStats.Pedestal, convStats.Median, convStats.Mad,
                    factor, clipping, ConvergenceTarget, whiteBalance: wbScalar);
            }
        }

        // The anchor's observed peak too: NormFactor is 1/MaxValue, so two frames whose brightest
        // pixel differs would otherwise normalise differently before the curve ever ran.
        var uniforms = ComputeStretchUniforms(mode, new StretchParameters(factor, clipping), stats, luma, Basis.UnstretchedImage.MaxValue, autoWb, weights, shaderWb);
        if (BackgroundNeutralization is { } bn)
        {
            // Lerp the gain toward identity by `strength`. Cheap: no recompute,
            // no extra uniform, no shader change. effective = (1-s)*1 + s*gain.
            var s = Math.Clamp(bgNeutralizationStrength, 0f, 1f);
            var effective = s >= 0.9999f
                ? bn
                : (
                    R: 1f + s * (bn.R - 1f),
                    G: 1f + s * (bn.G - 1f),
                    B: 1f + s * (bn.B - 1f));
            uniforms = uniforms with { BackgroundNeutralization = effective };
        }
        if (lumaBlend != 1f)
        {
            uniforms = uniforms with { LumaBlend = System.Math.Clamp(lumaBlend, 0f, 1f) };
        }
        if (normalize)
        {
            var scale = Image.PredictPostStretchMaxScale(
                uniforms,
                Basis.ChannelStatistics.AsSpan(),
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
    /// Computes stretch shader uniforms from stats directly; no <see cref="AstroImageDocument"/> needed.
    /// When <paramref name="whiteBalance"/> is non-null, per-channel stats are scaled by the WB
    /// multipliers before deriving shadows/midtones/rescale, so the shadow clip lands in the
    /// same coordinate space as the post-WB norm in the GLSL stretch loop. Without this
    /// adjustment, channels reduced by WB (e.g. B with wb=0.94) would have their post-WB norm
    /// fall below the un-adjusted shadow and clamp to zero, tinting the bg toward the boosted
    /// channels.
    /// <para>
    /// <paramref name="shaderWhiteBalance"/> is the triple written to <see cref="StretchUniforms.WhiteBalance"/>
    /// (the per-channel shader multiply). It defaults to <paramref name="whiteBalance"/>, so a single-WB
    /// caller is unchanged. Passing a <i>different</i> value decouples the shader multiply from the
    /// stat scaling: that is how a MANUAL WB slider stays visible even in a per-channel auto-normalised
    /// stretch (the manual portion multiplies but does not scale the stats, so the curve can't re-absorb
    /// it), while the AUTO calibration keeps scaling the stats to preserve a neutral background.
    /// </para>
    /// </summary>
    public static StretchUniforms ComputeStretchUniforms(
        StretchMode mode,
        StretchParameters parameters,
        ChannelStretchStats[] perChannelStats,
        ChannelStretchStats? lumaStats,
        float imageMaxValue,
        (float R, float G, float B)? whiteBalance = null,
        (float R, float G, float B)? lumaWeights = null,
        (float R, float G, float B)? shaderWhiteBalance = null)
    {
        return StretchSolver.ComputeStretchUniforms(
            mode, parameters, perChannelStats, lumaStats, imageMaxValue,
            whiteBalance, lumaWeights, shaderWhiteBalance);
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
        var stars = await UnstretchedImage.FindStarsAsync(
            channel: UnstretchedImage.ReferenceStarChannel, snrMin: 10f, maxStars: 2000, cancellationToken: cancellationToken);
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
            _perChannelBackground = perChannelBg;
            _lumaBackground = lumaBg;

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

        _colorCalibration = (w.R, w.G, w.B);
        // No white reference: a sky-background estimate declares the SKY neutral, which is an
        // assumption about the frame rather than a spectrum, so there is nothing honest to name.
        var summary = new ColorCalibrationSummary(
            "Sky background", w.R, w.G, w.B, StarCount: 0, WhiteReference: null);
        _colorCalibrationSummary = summary;
        return (1, summary.Describe());
    }

    /// <summary>
    /// Computes background neutralization gains from the darkest spatial region.
    /// Results flow through the GPU shader as <see cref="StretchUniforms.BackgroundNeutralization"/>.
    /// Method results are cached per document: switching methods on the same
    /// document hits the cache, never re-walks pixels.
    /// </summary>
    public (float R, float G, float B)? ComputeBackgroundNeutralization(
        Lib.Imaging.BackgroundNeutralizationMethod method = Lib.Imaging.BackgroundNeutralizationMethod.Mean,
        bool applyColorCalibration = true)
    {
        if (PerChannelBackground is not { Length: >= 3 } bg)
            return null;

        // The gains depend on the CALIBRATION as well as the method, because they are solved so the
        // background is neutral AFTER the shader's WB multiply -- so the white balance belongs in the
        // cache key. Keyed on method alone, a calibration landing after the first neutralisation
        // would keep serving gains computed for the previous (or absent) triple: the same
        // stale-cached-projection shape as a palette-derived texture that outlives a theme switch.
        var wb = (applyColorCalibration ? ColorCalibration : null) ?? (1f, 1f, 1f);
        // The BACKGROUND array is part of the key too, by reference, because it is replaced rather than
        // mutated -- by star detection, which re-scans it behind a mask, and by a DisplayAnchor being
        // taken up or dropped. Keyed on method and WB alone, gains solved for a background this frame
        // no longer displays with would go on being served: the same stale-cached-projection shape the
        // WB was already in the key to prevent.
        var key = (method, wb, bg);
        if (!_bnGainsByMethod.TryGetValue(key, out var gains))
        {
            gains = Lib.Imaging.BackgroundNeutralization.ComputeGains(bg, method, wb);
            _bnGainsByMethod[key] = gains;
        }

        // Always pin the chosen method's gain onto the document, even when it
        // happens to be near-identity for this image, so subsequent renders
        // reflect the user's explicit choice rather than a stale value left
        // over from a previously-selected method.
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

        // SELF-INIT, the same contract CatalogPlateSolver has for the object DB: any caller works
        // without having remembered to load this upstream. LoadAsync is idempotent and the fast path
        // is one field read, so a warm process pays nothing.
        //
        // This was a bare `if (!IsLoaded) return` guard, and it meant SPCC could never run in the
        // FITS VIEWER at all: only the stacking renderer and the session loop call LoadAsync, so in
        // tianwen-fits the database was always cold, the guard always returned 0, and the caller
        // always fell through to the sky-background estimate. On an already-background-neutralised
        // master that fallback returns ~(1.00, 1.00, 1.00), which is indistinguishable from a
        // successful calibration -- so the failure was invisible from the UI and from the log.
        await FilterCurveDatabase.LoadAsync(cancellationToken);

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

        _colorCalibration = (r.R, r.G, r.B);
        var summary = new ColorCalibrationSummary(
            "SPCC", r.R, r.G, r.B, r.MatchCount, r.WhiteReferenceName);
        _colorCalibrationSummary = summary;
        return (r.MatchCount, summary.Describe());
    }

    /// <summary>
    /// Samples the darkest 10% of non-star pixels to find the true sky background color.
    /// Stars and nebulae are brighter and get excluded by the percentile threshold,
    /// so only true sky background contributes to the color estimate.
    /// </summary>
    /// <summary>
    /// Derives an (R, G, B) white-balance triple from the median colour of the
    /// darkest 10% of star-masked sky pixels in a 3-channel image. The
    /// "sky should be grey" assumption: divide G by R and G by B, clamp the
    /// ratios to [0.5, 2], and return them as the multipliers that, applied
    /// to the channels, neutralise the sky cast.
    /// <para>Pure function -- safe to call from headless pipelines (the
    /// stacking E2E uses it as a fallback when SPCC can't run because no
    /// plate-solve / sensor throughput is available). Returns <c>null</c>
    /// when too few clean samples remain after the star-mask exclusion
    /// (&lt; 100) or green collapses to ~0.</para>
    /// </summary>
    public static (float R, float G, float B)? ComputeSkyBackgroundWB(Image image, BitMatrix starMask)
    {
        return StretchSolver.ComputeSkyBackgroundWB(image, starMask);
    }

    /// <summary>
    /// Gets pixel information at the given display coordinates, including sky coordinates if plate-solved.
    /// Returns raw (unstretched) values from the processedRawImage image.
    /// </summary>
    /// <param name="channel">
    /// The single source channel on screen, or <c>null</c> to read every channel (a composite view).
    /// Resolve it with <c>ChannelView.DisplayedSourceChannel</c> rather than by hand.
    /// </param>
    /// <remarks>
    /// <b>This runs on every mouse move</b> (<c>ViewerActions.UpdateCursorInfo</c>), so on a large
    /// master the channel argument is the difference between touching one float plane per move and
    /// touching all of them. It is also what the user is looking at: reporting R, G and B while the
    /// display is a single channel names two channels that are not on screen.
    /// </remarks>
    public PixelInfo GetPixelInfo(int x, int y, int? channel = null)
    {
        var image = UnstretchedImage;
        if (x < 0 || x >= image.Width || y < 0 || y >= image.Height)
        {
            return new PixelInfo(x, y, [], null, null);
        }

        // Read from the source raster where there is one. Not an optimisation: this runs on every
        // mouse move, and the indexer restores evicted float planes on access -- so reading it here
        // would rebuild 8-bit planes on the first hover and quietly undo D1 for the whole session.
        // The values are identical rather than close: the plane was normalised by the sample-format
        // maximum from these very bytes, so this is the same division over the same data.
        float[] values;
        if (channel is { } single && (uint)single < (uint)image.ChannelCount)
        {
            values = [SampleAt(image, single, x, y)];
        }
        else
        {
            values = new float[image.ChannelCount];
            for (var c = 0; c < image.ChannelCount; c++)
            {
                values[c] = SampleAt(image, c, x, y);
            }
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
    /// One sample, from the source raster when the image carries one and from the float plane
    /// otherwise.
    /// </summary>
    private static float SampleAt(Image image, int channel, int x, int y)
        => image.TryGetSourceRaster(channel, out var raster)
            ? raster[y * image.Width + x] / 255f
            : image[channel, y, x];
}
