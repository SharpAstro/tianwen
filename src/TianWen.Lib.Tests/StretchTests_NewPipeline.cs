using ImageMagick;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// End-to-end tests for the new stretch pipeline (star-masked stats + iterative convergence
/// + Tycho-2/SPCC WB + background neutralization + Fritsch-Carlson curve LUT + HDR knee).
///
/// Drives the pipeline through <see cref="AstroImageDocument.ComputeStretchUniforms"/> and
/// <see cref="Image.RenderStretchedRgba"/> — the CPU mirror of the GLSL fragment shader.
/// Writes a TIFF per case to the temp test output dir so the visual result of each
/// pipeline feature can be inspected.
/// </summary>
[Collection("Imaging")]
public class StretchTests_NewPipeline(ITestOutputHelper output)
{
    private const string Fixture = "Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop";

    [Theory]
    // mode    , wb   , bgNeut, conv , curvesMode, hdrAmount, label
    [InlineData("linked", false, false, false, 0, 0f, "01_baseline")]
    [InlineData("linked", true,  false, false, 0, 0f, "02_wb")]
    [InlineData("linked", false, true,  false, 0, 0f, "03_bgneut")]
    [InlineData("linked", true,  true,  false, 0, 0f, "04_wb_bgneut")]
    [InlineData("linked", true,  true,  true,  0, 0f, "05_wb_bgneut_converged")]
    [InlineData("linked", true,  true,  true,  1, 0f, "06_wb_bgneut_converged_curveLut")]
    [InlineData("linked", true,  true,  true,  1, 0.8f, "07_wb_bgneut_converged_curveLut_hdr")]
    [InlineData("luma",   true,  false, false, 0, 0f, "08_luma_wb")]
    public async Task GivenColorFitsWhenRenderingThroughCpuPipelineThenWritesTiff(
        string mode, bool applyWb, bool applyBgNeut, bool useConvergence,
        int curvesMode, float hdrAmount, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var fitsImage = await SharedTestData.ExtractGZippedFitsImageAsync(Fixture, cancellationToken: ct);
        var doc = await AstroImageDocument.CreateFromImageAsync(fitsImage, DebayerAlgorithm.None, cancellationToken: ct);

        // Star detection populates StarMaskedStats and a star-masked PerChannelBackground —
        // both required for convergence + background neutralization to behave realistically.
        await doc.DetectStarsAsync(ct);

        var img = doc.UnstretchedImage;
        output.WriteLine($"Image: {img.Width}x{img.Height}x{img.ChannelCount}  stars={doc.Stars?.Count ?? 0}  HFR={doc.AverageHFR:F2}");
        output.WriteLine($"PerChannelBg: R={doc.PerChannelBackground[0]:F4} G={doc.PerChannelBackground[1]:F4} B={doc.PerChannelBackground[2]:F4}");

        if (useConvergence)
        {
            doc.UseIterativeConvergence = true;
        }

        // Set ColorCalibration on the doc BEFORE computing uniforms so the static method can
        // scale the per-channel stats by WB and derive shadows/midtones/rescale in the
        // post-WB coordinate space. Setting WhiteBalance via `with` after the fact would only
        // change the shader uniform but leave shadows un-adjusted, clamping post-WB-reduced
        // channels (e.g. B with wb<1) below shadow and tinting the output.
        if (applyWb)
        {
            // Production sky-background WB algorithm (AstroImageDocument.
            // ComputeColorCalibrationAsync -> ComputeSkyBackgroundWB): samples the darkest 10%
            // of non-star pixels and returns (medG/medR, 1, medG/medB) clamped to [0.5, 2.0].
            // The `db` parameter is unused on the sky-bg path so we pass null.
            var (matchCount, diag) = await doc.ComputeColorCalibrationAsync(null!, ct);
            output.WriteLine($"WB (sky-bg): matchCount={matchCount} diag={diag}  ColorCalibration={doc.ColorCalibration}");
        }

        var stretchMode = mode == "luma" ? StretchMode.Luma : StretchMode.Linked;
        // shadowsClipping=-3 matches the production default (StretchParameters.Default).
        var uniforms = doc.ComputeStretchUniforms(stretchMode, new StretchParameters(0.15, -3));

        if (applyBgNeut)
        {
            // Real gains for this fixture are near-identity (~1, 1, 1) because Vela's
            // per-channel pedestal-subtracted backgrounds are nearly equal — that's what bg
            // neutralization should look like on a clean fixture. Inventing larger non-identity
            // gains here is a trap: the pivot1 formula `out = norm * g + (1 - g)` breaks when g
            // doesn't come from the actual bg level (drives bg pixels negative -> clamped to 0
            // -> green channel zeroed -> magenta output). We use real gains so the output is
            // visually correct; the bgneut code path still runs end-to-end.
            var gains = BackgroundNeutralization.ComputeGains(doc.PerChannelBackground);
            uniforms = uniforms with { BackgroundNeutralization = gains };
            output.WriteLine($"BG-neut gains: R={gains.R:F4} G={gains.G:F4} B={gains.B:F4}");
        }

        ImmutableArray<float> curveKnots = default;
        if (curvesMode == 1)
        {
            // Same S-curve preset the viewer uses (Shift+B)
            var spline = new FritschCarlsonSpline(
                [(0f, 0f), (0.15f, 0.22f), (0.4f, 0.5f), (0.7f, 0.72f), (1f, 1f)]);
            curveKnots = spline.ComputeKnots33();
        }

        output.WriteLine($"Mode={uniforms.Mode}  NormFactor={uniforms.NormFactor:F4}");
        output.WriteLine($"  WB       = {Triple(uniforms.WhiteBalance)}");
        output.WriteLine($"  BgNeut   = {Triple(uniforms.BackgroundNeutralization)}");
        output.WriteLine($"  Pedestal = {Triple(uniforms.Pedestal)}");
        output.WriteLine($"  Shadows  = {Triple(uniforms.Shadows)}");
        output.WriteLine($"  Midtones = {Triple(uniforms.Midtones)}");
        output.WriteLine($"  Rescale  = {Triple(uniforms.Rescale)}");

        var rgba = new byte[img.Width * img.Height * 4];
        var sw = Stopwatch.StartNew();
        img.RenderStretchedRgba(
            uniforms,
            rgba,
            curvesMode: curvesMode,
            curveLut: curveKnots.IsDefault ? default : curveKnots.AsSpan(),
            hdrAmount: hdrAmount);
        sw.Stop();
        output.WriteLine($"RenderStretchedRgba ({img.Width}x{img.Height}): {sw.Elapsed}");

        // Sanity: bytes should vary across the image. Not all-zero (broken pipeline produces no
        // signal) and not all-255 (broken clamp). Brightness varies wildly across legitimate
        // stretches (convergence on this fixture is dark by design — midtones -> 0.9996),
        // so the assertion can't be "must be bright"; it's "must have signal".
        byte minByte = 255, maxByte = 0;
        long sum = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var b = rgba[i + c];
                if (b < minByte) minByte = b;
                if (b > maxByte) maxByte = b;
                sum += b;
            }
        }
        var avg = sum / (double)(rgba.Length / 4 * 3);
        output.WriteLine($"RGB byte range: [{minByte}, {maxByte}]  mean: {avg:F2}");
        maxByte.ShouldBeGreaterThan((byte)0, "pipeline produced pure black — no signal");
        minByte.ShouldBeLessThan((byte)255, "pipeline produced pure white — clamp broken or all pixels saturated");
        (maxByte - minByte).ShouldBeGreaterThan(10, "RGB output should have some dynamic range");

        var testDir = SharedTestData.CreateTempTestOutputDir(
            TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTests_NewPipeline));
        await WriteRgbaAsync(rgba, img.Width, img.Height, testDir, $"{Fixture}_{label}", ct);
    }

    private static string Triple((float R, float G, float B) v) => $"R={v.R:F4} G={v.G:F4} B={v.B:F4}";

    /// <summary>
    /// Writes an RGBA byte buffer as both lossless ZIP-compressed TIFF (archival; preserves
    /// every byte for diff-against-GLSL) and JPEG quality-90 (easier to view in any image
    /// browser and shows the visible result of the pipeline).
    /// </summary>
    private async Task WriteRgbaAsync(byte[] rgba, int width, int height, string testDir, string namePrefix, CancellationToken ct)
    {
        var settings = new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGBA);
        using var magick = new MagickImage(rgba, settings);

        var tiffPath = Path.Combine(testDir, $"{namePrefix}.tiff");
        magick.Settings.Compression = CompressionMethod.Zip;
        var tiffBytes = magick.ToByteArray(MagickFormat.Tiff);
        await File.WriteAllBytesAsync(tiffPath, tiffBytes, ct);

        var jpegPath = Path.Combine(testDir, $"{namePrefix}.jpg");
        magick.Quality = 90;
        var jpegBytes = magick.ToByteArray(MagickFormat.Jpeg);
        await File.WriteAllBytesAsync(jpegPath, jpegBytes, ct);

        output.WriteLine($"Wrote {tiffBytes.Length} bytes -> {tiffPath}");
        output.WriteLine($"Wrote {jpegBytes.Length} bytes -> {jpegPath}");
    }

    // Cache the catalog DB across all SPCC runs in this assembly — InitDBAsync waits for the
    // Tycho-2 bulk load (~seconds) which dominates wall-clock when run cold.
    private static ICelestialObjectDB? _cachedDb;
    private static readonly SemaphoreSlim _dbSem = new(1, 1);

    private static async Task<ICelestialObjectDB> InitDbAsync(CancellationToken ct)
    {
        if (_cachedDb is { } cached) return cached;
        await _dbSem.WaitAsync(ct);
        try
        {
            if (_cachedDb is { } cached2) return cached2;
            var db = new CelestialObjectDB();
            // SPCC + plate-solving both query CoordinateGrid which needs Tycho-2 fully loaded.
            await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            _cachedDb = db;
            return db;
        }
        finally
        {
            _dbSem.Release();
        }
    }

    /// <summary>
    /// End-to-end SPCC (spectrophotometric color calibration): synthesise a star field at a
    /// known sky position by projecting Tycho-2 stars and rendering them through Sony CFA
    /// per-pixel colouring, construct a matching WCS, run star detection, then drive SPCC
    /// through <see cref="AstroImageDocument.ComputeSpccColorCalibrationAsync"/> which integrates
    /// Pickles SEDs through (IMX533 QE x Sony CFA) per channel and fits per-channel WB from
    /// real catalog photometry. The resulting WB is applied to <see cref="StretchUniforms"/>
    /// and rendered through <see cref="Image.RenderStretchedRgba"/> to a TIFF.
    ///
    /// Synthesis route is used rather than the Vela fixture because Vela has no FITS-embedded
    /// WCS and no pixel-scale headers; SPCC genuinely needs an accurate WCS to match catalog
    /// stars within ~5 arcsec.
    /// </summary>
    [Fact]
    public async Task GivenSyntheticStarFieldWhenSpccCalibratedThenWritesTiff()
    {
        var ct = TestContext.Current.CancellationToken;

        // Heavyweight init: catalog DB + filter/SED database. Both are cached process-wide.
        var loadFilterDb = FilterCurveDatabase.LoadAsync(ct);
        var dbTask = InitDbAsync(ct);
        await loadFilterDb;
        var db = await dbTask;

        // M45 (Pleiades) — open cluster with a few hundred Tycho-2 stars in a 2-deg field.
        // Plenty of bright matches for SPCC photometry.
        const double targetRA = 3.79f;          // hours
        const double targetDec = 24.10f;        // degrees
        const int focalLengthMm = 200;          // wider FOV -> more stars per frame
        const float pixelSizeUm = 3.76f;        // IMX533 native pixel
        const int width = 1280;
        const int height = 1024;
        const int gain = 100;
        const float exposureSeconds = 60f;

        // Project real Tycho-2 stars onto the sensor. ProjectCatalogStars uses the same gnomonic
        // (TAN) maths the WCS will invert below, so star pixel positions are pixel-accurate.
        var projected = SyntheticStarFieldRenderer.ProjectCatalogStars(
            targetRA, targetDec, focalLengthMm, pixelSizeUm, width, height, db, magnitudeCutoff: 12.0);
        output.WriteLine($"Projected {projected.Count} catalog stars into the synthetic field");
        projected.Count.ShouldBeGreaterThan(15, "M45 field should yield enough Tycho-2 stars (default cutoff mag 12) for SPCC");

        // RenderBayer applies BMinusVToRGB (blackbody approximation) per star and modulates the
        // CFA pattern accordingly, so debayering produces stars whose per-channel flux ratios
        // genuinely vary by spectral type. SPCC measures those ratios and compares against
        // Pickles SEDs integrated through (IMX533 QE x Sony CFA) — different model, different
        // throughput shape, so the fitted WB compensates for the mismatch (i.e., is non-trivial).
        // hyperbolaA=4 gives an ~4-pixel FWHM PSF — bigger stars are more visible at thumbnail
        // resolution and easier to inspect than 1.5-pixel pinpoints.
        var bayerData = SyntheticStarFieldRenderer.RenderBayer(
            width, height,
            defocusSteps: 0,
            stars: projected.ToArray().AsSpan(),
            exposureSeconds: exposureSeconds,
            hyperbolaA: 4.0,
            apertureScaleFactor: (130.0 / 50.0) * (130.0 / 50.0));   // 130mm aperture vs 50mm reference

        // Wrap the float[height,width] mosaic as a normalized Image with full Sony OSC metadata.
        var maxAdu = 65535f;
        var imageMeta = new ImageMeta(
            Instrument: "Synthetic",
            ExposureStartTime: DateTimeOffset.UtcNow,
            ExposureDuration: TimeSpan.FromSeconds(exposureSeconds),
            FrameType: FrameType.Light,
            Telescope: "Synthetic 130mm APO",
            PixelSizeX: pixelSizeUm,
            PixelSizeY: pixelSizeUm,
            FocalLength: focalLengthMm,
            FocusPos: 0,
            Filter: Filter.None,
            BinX: 1, BinY: 1,
            CCDTemperature: -10,
            SensorType: SensorType.RGGB,
            BayerOffsetX: 0, BayerOffsetY: 0,
            RowOrder: RowOrder.TopDown,
            Latitude: 0f, Longitude: 0f,
            Gain: (short)gain,
            Aperture: 130,
            SensorModel: "IMX533");
        var bayerImage = new Image([bayerData], BitDepth.Int16, maxValue: maxAdu, minValue: 0f, pedestal: 0f, imageMeta);

        // Construct a WCS that exactly inverts the projection used to render. CD matrix is in
        // degrees-per-pixel; both diagonal entries are negative because the synth uses the
        // standard astronomical convention (East left, North up = pixel Y decreasing northwards).
        var pixelScaleArcsec = (pixelSizeUm * 1e-3) / focalLengthMm * 206264.806;
        var pixelScaleDeg = pixelScaleArcsec / 3600.0;
        var wcs = new WCS(targetRA, targetDec)
        {
            CRPix1 = width / 2.0 + 1,    // 1-based FITS pixel of the projection centre
            CRPix2 = height / 2.0 + 1,
            CD1_1 = -pixelScaleDeg,
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = -pixelScaleDeg,
        };
        wcs.HasCDMatrix.ShouldBeTrue();
        output.WriteLine($"Synthetic WCS: center=({wcs.CenterRA:F4}h, {wcs.CenterDec:F3}°)  scale={pixelScaleArcsec:F3}\"/px");

        var doc = await AstroImageDocument.CreateFromImageAsync(bayerImage, DebayerAlgorithm.AHD, wcs, filePath: "synthetic.fits", ct);
        doc.IsPlateSolved.ShouldBeTrue();

        var sw = Stopwatch.StartNew();
        await doc.DetectStarsAsync(ct);
        sw.Stop();
        output.WriteLine($"Star detection: {doc.Stars?.Count ?? 0} stars in {sw.Elapsed.TotalMilliseconds:F0}ms");
        (doc.Stars?.Count ?? 0).ShouldBeGreaterThan(20, "synthetic field should yield enough detections for SPCC");

        // Drive the SPCC pipeline through the document — uses BuildChannelThroughputs (IMX533 QE x
        // Sony CFA per channel) + Tycho2ColorCalibration.ComputeSpectrophotometricWhiteBalance.
        var (matchCount, diag) = await doc.ComputeSpccColorCalibrationAsync(db, ct);
        output.WriteLine($"SPCC: matchCount={matchCount}  diag={diag}  ColorCalibration={(doc.ColorCalibration is { } cc ? $"({cc.R:F4},{cc.G:F4},{cc.B:F4})" : "null")}");
        doc.ColorCalibration.ShouldNotBeNull("SPCC should fit non-trivial WB on the synthetic Sony OSC field");

        // Synthesis bg is highly uniform (deterministic shot noise) so MAD is tiny -> default
        // stretch sets midtones near 0 -> MTF saturates everything to white. Enable iterative
        // convergence so the post-stretch median lands at the target. ConvergenceTarget=0.15
        // (vs default 0.25) gives a darker sky with brighter relative stars — better visual
        // contrast for a sparse-star synthesis where most pixels are bg.
        doc.UseIterativeConvergence = true;
        doc.ConvergenceTarget = 0.15;
        var uniforms = doc.ComputeStretchUniforms(StretchMode.Linked, new StretchParameters(0.15, -1));
        output.WriteLine($"Stretch uniforms: WB={Triple(uniforms.WhiteBalance)}");

        // RenderStretchedRgba needs 3-channel input; debayer the Bayer mosaic before rendering.
        // (The doc keeps the raw Bayer for SPCC photometry; the rendered TIFF reflects the
        // GPU's bilinear-debayer-then-stretch path via an AHD CPU debayer for parity.)
        var debayered = await doc.UnstretchedImage.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);
        output.WriteLine($"Debayered image: {debayered.Width}x{debayered.Height}x{debayered.ChannelCount}  MaxValue={debayered.MaxValue:F4}  MinValue={debayered.MinValue:F4}");
        for (var c = 0; c < debayered.ChannelCount; c++)
        {
            var (ped, med, mad) = debayered.GetPedestralMedianAndMADScaledToUnit(c);
            output.WriteLine($"  Ch{c}: pedestal={ped:F6} median={med:F6} mad={mad:F6}");
        }

        var rgba = new byte[debayered.Width * debayered.Height * 4];
        debayered.RenderStretchedRgba(uniforms, rgba);

        // Diagnostic: byte range. Star fields render mostly dark with bright peaks; uniform-bright
        // would indicate the stretch saturated everything.
        byte minByte = 255, maxByte = 0;
        long sum = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var b = rgba[i + c];
                if (b < minByte) minByte = b;
                if (b > maxByte) maxByte = b;
                sum += b;
            }
        }
        var avg = sum / (double)(rgba.Length / 4 * 3);
        output.WriteLine($"RGB byte range: [{minByte}, {maxByte}]  mean: {avg:F2}");

        var testDir = SharedTestData.CreateTempTestOutputDir(
            TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTests_NewPipeline));
        await WriteRgbaAsync(rgba, debayered.Width, debayered.Height, testDir, "synthetic_M45_09_spcc", ct);
    }

}
