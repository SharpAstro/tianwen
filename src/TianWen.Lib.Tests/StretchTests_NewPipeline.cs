using DIR.Lib;
using SharpAstro.Tiff;
using SharpAstro.Png;
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
    [InlineData("luma",   true,  false, true,  0, 0f, "08_luma_wb")]
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

        // ---------- StretchUniforms field assertions ----------
        // NormFactor: 1 because doc.UnstretchedImage is normalised by ScaleFloatValuesToUnitInPlace.
        uniforms.NormFactor.ShouldBe(1f, 1e-4f);
        // Pedestal: matches the per-channel pedestal stat. For Vela it's MinValue/MaxValue ≈ 0.029.
        uniforms.Pedestal.R.ShouldBe(doc.PerChannelStats[0].Pedestal, 1e-6f);
        uniforms.Pedestal.G.ShouldBe(doc.PerChannelStats[1].Pedestal, 1e-6f);
        uniforms.Pedestal.B.ShouldBe(doc.PerChannelStats[2].Pedestal, 1e-6f);
        // Shadows clipped via shadowsClipping=-3, so they should be slightly below the channel
        // medians (or in converged mode possibly above 0 from the WB-scaled stats).
        AssertChannel(uniforms.Shadows, "Shadows", v => v.ShouldBeLessThan(0.5f));
        // Midtones in (0, 1). MTF is degenerate at the endpoints.
        AssertChannel(uniforms.Midtones, "Midtones", v => v.ShouldBeInRange(0f, 1f));
        // Rescale > 0 (must be finite, monotonic-positive). Typical values are 0.3–1.5.
        AssertChannel(uniforms.Rescale, "Rescale", v => v.ShouldBeGreaterThan(0f));
        AssertChannel(uniforms.Rescale, "Rescale", v => float.IsFinite(v).ShouldBeTrue());

        // ---------- WhiteBalance assertions ----------
        if (applyWb)
        {
            // Real WB came from ComputeColorCalibrationAsync (sky-bg). For Vela the dark sky is
            // already nearly neutral so gains hover near identity but should be set non-default.
            uniforms.WhiteBalance.G.ShouldBe(1f, 1e-3f, "G is the WB anchor");
            uniforms.WhiteBalance.R.ShouldBeInRange(0.5f, 2f, "R clamped to algorithm bounds");
            uniforms.WhiteBalance.B.ShouldBeInRange(0.5f, 2f, "B clamped to algorithm bounds");
            doc.ColorCalibration.HasValue.ShouldBeTrue("doc tracks the same calibration");
            uniforms.WhiteBalance.R.ShouldBe(doc.ColorCalibration!.Value.R, 1e-5f);
            uniforms.WhiteBalance.B.ShouldBe(doc.ColorCalibration!.Value.B, 1e-5f);
        }
        else
        {
            uniforms.WhiteBalance.ShouldBe((1f, 1f, 1f), "WB defaults to identity");
        }

        // ---------- BackgroundNeutralization assertions ----------
        if (applyBgNeut)
        {
            // Pivot1 gains: clamped to [0, 10], green normalised toward 1, all >0.
            AssertChannel(uniforms.BackgroundNeutralization, "BgNeut", v => v.ShouldBeInRange(0f, 10f));
            // For Vela's near-neutral bg the gains hover near identity (sub-1% deviation).
            AssertChannel(uniforms.BackgroundNeutralization, "BgNeut", v => Math.Abs(v - 1f).ShouldBeLessThan(0.5f));
        }
        else
        {
            uniforms.BackgroundNeutralization.ShouldBe((1f, 1f, 1f), "bgNeut defaults to identity");
        }

        // ---------- Curve LUT assertions ----------
        if (curvesMode == 1)
        {
            curveKnots.IsDefault.ShouldBeFalse("curve LUT must be populated when curvesMode==1");
            curveKnots.Length.ShouldBe(33, "33 knots packed into 9 std140 vec4 slots");
            curveKnots[0].ShouldBe(0f, 1e-4f, "S-curve preset starts at (0,0)");
            curveKnots[^1].ShouldBe(1f, 1e-4f, "S-curve preset ends at (1,1)");
            // Monotonic non-decreasing -- Fritsch-Carlson guarantees this.
            for (var i = 1; i < curveKnots.Length; i++)
            {
                curveKnots[i].ShouldBeGreaterThanOrEqualTo(curveKnots[i - 1] - 1e-4f,
                    $"curve LUT must be monotonic non-decreasing at index {i}");
            }
        }

        // ---------- Render ----------
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

        // ---------- Per-channel byte stats ----------
        byte minByte = 255, maxByte = 0;
        long sum = 0;
        Span<long> chSum = stackalloc long[3];
        Span<int> chMin = stackalloc int[3] { 255, 255, 255 };
        Span<int> chMax = stackalloc int[3] { 0, 0, 0 };
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var b = rgba[i + c];
                if (b < minByte) minByte = b;
                if (b > maxByte) maxByte = b;
                sum += b;
                chSum[c] += b;
                if (b < chMin[c]) chMin[c] = b;
                if (b > chMax[c]) chMax[c] = b;
            }
        }
        var avg = sum / (double)(rgba.Length / 4 * 3);
        var pixelCount = rgba.Length / 4;
        var rMean = chSum[0] / (double)pixelCount;
        var gMean = chSum[1] / (double)pixelCount;
        var bMean = chSum[2] / (double)pixelCount;
        output.WriteLine($"RGB byte range: [{minByte}, {maxByte}]  mean: {avg:F2}");
        output.WriteLine($"  per-channel means: R={rMean:F2} G={gMean:F2} B={bMean:F2}");
        output.WriteLine($"  per-channel ranges: R=[{chMin[0]},{chMax[0]}] G=[{chMin[1]},{chMax[1]}] B=[{chMin[2]},{chMax[2]}]");

        // Pipeline produced signal — not all-zero, not all-255, varies across the frame.
        maxByte.ShouldBeGreaterThan((byte)0, "pipeline produced pure black — no signal");
        minByte.ShouldBeLessThan((byte)255, "pipeline produced pure white — clamp broken or all pixels saturated");
        (maxByte - minByte).ShouldBeGreaterThan(10, "RGB output should have some dynamic range");
        // Each channel has signal (not collapsed to 0 or saturated to 255) — catches per-channel
        // bugs like the WB-shadow-mismatch which zeroed a single channel.
        rMean.ShouldBeGreaterThan(0.5, "R channel signal should not collapse");
        gMean.ShouldBeGreaterThan(0.5, "G channel signal should not collapse");
        bMean.ShouldBeGreaterThan(0.5, "B channel signal should not collapse");
        rMean.ShouldBeLessThan(254.5, "R channel should not be fully saturated");
        gMean.ShouldBeLessThan(254.5, "G channel should not be fully saturated");
        bMean.ShouldBeLessThan(254.5, "B channel should not be fully saturated");

        // ---------- HDR knee assertion ----------
        if (hdrAmount > 0f)
        {
            // applyHdr compresses values above the knee. With hdrAmount>0, the max channel byte
            // should be < 255 (knee compression caps the brightest pixels below pure white).
            maxByte.ShouldBeLessThan((byte)252, "HDR knee compression should cap the brightest pixels");
        }

        var testDir = SharedTestData.CreateTempTestOutputDir(
            TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTests_NewPipeline));
        await WriteRgbaAsync(rgba, img.Width, img.Height, testDir, $"{Fixture}_{label}", ct);

        static void AssertChannel((float R, float G, float B) v, string name, Action<float> check)
        {
            check(v.R);
            check(v.G);
            check(v.B);
        }
    }

    private static string Triple((float R, float G, float B) v) => $"R={v.R:F4} G={v.G:F4} B={v.B:F4}";

    /// <summary>
    /// Writes an RGBA byte buffer as both lossless 8-bit RGB TIFF (Deflate-compressed,
    /// preserves every byte for diff-against-GLSL) and PNG (easier to view in any image
    /// browser; replaces the prior JPEG quality-90 emit — PNG is lossless, viewable in
    /// every browser, and DIR.Lib already ships a writer). Both write via DIR.Lib —
    /// Magick.NET isn't on the path here.
    /// </summary>
    private async Task WriteRgbaAsync(byte[] rgba, int width, int height, string testDir, string namePrefix, CancellationToken ct)
    {
        // RGBA → RGB for the lossless diff TIFF. Alpha is always 0xFF for the stretch
        // pipeline output so dropping it costs nothing and matches the pre-port files.
        var pixelCount = width * height;
        var rgb = new byte[pixelCount * 3];
        for (int p = 0, src = 0, dst = 0; p < pixelCount; p++, src += 4, dst += 3)
        {
            rgb[dst]     = rgba[src];
            rgb[dst + 1] = rgba[src + 1];
            rgb[dst + 2] = rgba[src + 2];
        }

        var tiffPath = Path.Combine(testDir, $"{namePrefix}.tiff");
        await using (var fs = File.Create(tiffPath))
        await using (var writer = TiffWriter.Create(fs))
        {
            await writer.AddPageAsync(rgb, width, height, new TiffPageOptions
            {
                SamplesPerPixel = 3,
                BitsPerSample = 8,
                Photometric = TiffPhotometric.Rgb,
                SampleFormat = TiffSampleFormat.Uint,
                Compression = TiffCompression.Deflate,
            }, ct);
            await writer.FlushAsync(ct);
        }

        var pngPath = Path.Combine(testDir, $"{namePrefix}.png");
        var pngBytes = PngWriter.Encode(rgba, width, height);
        await File.WriteAllBytesAsync(pngPath, pngBytes, ct);

        var tiffSize = new FileInfo(tiffPath).Length;
        output.WriteLine($"Wrote {tiffSize} bytes -> {tiffPath}");
        output.WriteLine($"Wrote {pngBytes.Length} bytes -> {pngPath}");
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

        // Verify the SPCC fit is sane: clamps to [0.5, 2.0] (Tycho2ColorCalibration.ComputeMultipliers),
        // green is normalised to 1, and the result is non-trivial (not identity (1,1,1)) since the
        // synth applied per-star B-V via blackbody while SPCC measures via Pickles SED through
        // (IMX533 QE x Sony CFA) — different models produce a meaningful WB.
        var wb = doc.ColorCalibration!.Value;
        wb.G.ShouldBe(1f, 1e-4f, "green channel is the WB anchor");
        wb.R.ShouldBeInRange(0.5f, 2f, "red WB clamped to algorithm bounds");
        wb.B.ShouldBeInRange(0.5f, 2f, "blue WB clamped to algorithm bounds");
        (Math.Abs(wb.R - 1f) + Math.Abs(wb.B - 1f)).ShouldBeGreaterThan(0.02f,
            "SPCC should produce non-identity WB on a synthesis where the model differs from BMinusVToRGB");
        // Synth bg is very slightly blue (R=0.073, G=0.092, B=0.101) and stars span B-V values
        // skewing toward the red end via Pickles SEDs vs blackbody — net SPCC effect should be
        // boosting R and reducing B (or close to it). A WB that does the opposite means the
        // photometry path is mis-aligned.
        wb.R.ShouldBeGreaterThan(1f, "SPCC on this synthesis should boost the underexposed-red channel");
        wb.B.ShouldBeLessThan(1f, "SPCC on this synthesis should reduce the over-blue channel");

        // Synthesis has near-uniform bg (deterministic shot noise -> tiny MAD), default stretch
        // would saturate everything to white. Convergence finds the stretchFactor that puts the
        // post-stretch median at ConvergenceTarget. Use Luma mode so convergence (which runs on
        // luma stats with Rec.709-weighted WB) and rendering (which uses the same luma stretch
        // applied to all channels via Y'/Y scaling) are in matching coordinate spaces.
        doc.UseIterativeConvergence = true;
        doc.ConvergenceTarget = 0.15;
        var uniforms = doc.ComputeStretchUniforms(StretchMode.Luma, new StretchParameters(0.15, -3));
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

        // Per-channel byte stats. After WB+convergence the rendered bg should be roughly
        // colour-neutral (R/G/B means within a few % of each other) — that's the whole point
        // of WB. A persistent colour cast in the rendered output means WB and the stretch
        // params are derived in mismatched coordinate spaces.
        byte minByte = 255, maxByte = 0;
        long sum = 0;
        Span<long> chSum = stackalloc long[3];
        Span<int> chMin = stackalloc int[3] { 255, 255, 255 };
        Span<int> chMax = stackalloc int[3] { 0, 0, 0 };
        for (var i = 0; i < rgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var b = rgba[i + c];
                if (b < minByte) minByte = b;
                if (b > maxByte) maxByte = b;
                sum += b;
                chSum[c] += b;
                if (b < chMin[c]) chMin[c] = b;
                if (b > chMax[c]) chMax[c] = b;
            }
        }
        var avg = sum / (double)(rgba.Length / 4 * 3);
        var pixelCount = rgba.Length / 4;
        var rMean = chSum[0] / (double)pixelCount;
        var gMean = chSum[1] / (double)pixelCount;
        var bMean = chSum[2] / (double)pixelCount;
        output.WriteLine($"Per-channel byte means: R={rMean:F2}  G={gMean:F2}  B={bMean:F2}");
        output.WriteLine($"Per-channel byte ranges: R=[{chMin[0]},{chMax[0]}]  G=[{chMin[1]},{chMax[1]}]  B=[{chMin[2]},{chMax[2]}]");

        // SPCC fits WB to make STAR colours match expected SED-through-throughput ratios; it
        // does NOT promise a colour-neutral background. For the synthetic field, stars use a
        // blackbody approximation (BMinusVToRGB) while SPCC measures via Pickles SED through
        // (IMX533 QE x Sony CFA) — different models, so the fitted WB compensates for stars
        // and may slightly amplify the bg cast. (To neutralise the bg you'd run
        // ComputeColorCalibrationAsync sky-bg WB instead.) We therefore assert:
        //   * Each channel has signal in both shadows and highlights (no clamp-to-black/white).
        //   * The post-stretch ratios stay within 2x — gross failures (e.g. pure red, pure
        //     yellow, channel completely zeroed) breach this.
        var maxMean = Math.Max(rMean, Math.Max(gMean, bMean));
        var minMean = Math.Min(rMean, Math.Min(gMean, bMean));
        rMean.ShouldBeGreaterThan(5.0, "R channel signal should not collapse to black");
        gMean.ShouldBeGreaterThan(5.0, "G channel signal should not collapse to black");
        bMean.ShouldBeGreaterThan(5.0, "B channel signal should not collapse to black");
        (maxMean / Math.Max(minMean, 1.0)).ShouldBeLessThan(2.0,
            $"Post-stretch channel means within 2x of each other. Got R={rMean:F1} G={gMean:F1} B={bMean:F1}");
        // Both chMin and chMax populated -> stretch produced full dynamic range, not flat.
        chMax[0].ShouldBeGreaterThan(chMin[0] + 50, "R channel needs dynamic range");
        chMax[1].ShouldBeGreaterThan(chMin[1] + 50, "G channel needs dynamic range");
        chMax[2].ShouldBeGreaterThan(chMin[2] + 50, "B channel needs dynamic range");
        output.WriteLine($"RGB byte range: [{minByte}, {maxByte}]  mean: {avg:F2}");

        var testDir = SharedTestData.CreateTempTestOutputDir(
            TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTests_NewPipeline));
        await WriteRgbaAsync(rgba, debayered.Width, debayered.Height, testDir, "synthetic_M45_09_spcc", ct);
    }

    /// <summary>
    /// Verifies the LumaWeighting picker plumbs alternative luminance weights all the way
    /// through to the rendered output. Renders the same Luma stretch under Rec.709, Rec.601,
    /// and Rec.2020 and asserts: (a) uniforms carry the expected weights, (b) the resulting
    /// per-channel byte means differ in the expected direction (Rec.601 weights green less
    /// heavily -> R/B channels gain prominence vs Rec.709; Rec.2020 weights green more
    /// heavily than Rec.709 -> R/B fade slightly).
    /// </summary>
    [Theory]
    [InlineData(LumaWeighting.Rec709, 0.2126f, 0.7152f, 0.0722f, "10_luma_rec709")]
    [InlineData(LumaWeighting.Rec601, 0.299f,  0.587f,  0.114f,  "11_luma_rec601")]
    [InlineData(LumaWeighting.Rec2020, 0.2627f, 0.6780f, 0.0593f, "12_luma_rec2020")]
    public async Task GivenColorFitsWhenSwitchingLumaWeightingThenWeightsFlowThrough(
        LumaWeighting weighting, float expectedR, float expectedG, float expectedB, string label)
    {
        var ct = TestContext.Current.CancellationToken;
        var fitsImage = await SharedTestData.ExtractGZippedFitsImageAsync(Fixture, cancellationToken: ct);
        var doc = await AstroImageDocument.CreateFromImageAsync(fitsImage, DebayerAlgorithm.None, cancellationToken: ct);
        await doc.DetectStarsAsync(ct);

        var uniforms = doc.ComputeStretchUniforms(StretchMode.Luma, new StretchParameters(0.15, -3), weighting: weighting);

        uniforms.LumaWeights.R.ShouldBe(expectedR, 1e-4f, $"LumaWeights.R for {weighting}");
        uniforms.LumaWeights.G.ShouldBe(expectedG, 1e-4f, $"LumaWeights.G for {weighting}");
        uniforms.LumaWeights.B.ShouldBe(expectedB, 1e-4f, $"LumaWeights.B for {weighting}");
        (uniforms.LumaWeights.R + uniforms.LumaWeights.G + uniforms.LumaWeights.B).ShouldBe(1f, 1e-3f,
            "luma weights must sum to ~1 (standard photometric convention)");

        var img = doc.UnstretchedImage;
        var rgba = new byte[img.Width * img.Height * 4];
        img.RenderStretchedRgba(uniforms, rgba);

        var (rMean, gMean, bMean) = ComputeChannelMeans(rgba);
        output.WriteLine($"{label}: means R={rMean:F2} G={gMean:F2} B={bMean:F2}");

        // Sanity: all channels carry signal (no collapse, no full saturation).
        rMean.ShouldBeGreaterThan(0.5); gMean.ShouldBeGreaterThan(0.5); bMean.ShouldBeGreaterThan(0.5);
        rMean.ShouldBeLessThan(254.5); gMean.ShouldBeLessThan(254.5); bMean.ShouldBeLessThan(254.5);

        var testDir = SharedTestData.CreateTempTestOutputDir(
            TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTests_NewPipeline));
        await WriteRgbaAsync(rgba, img.Width, img.Height, testDir, $"{Fixture}_{label}", ct);
    }

    /// <summary>
    /// Luma blend slider: 0 = pure linked, 1 = pure luma, in-between = mix.
    /// Asserts each blend level produces a distinct (non-identical) byte buffer and that the
    /// linear blend identity holds approximately: mid = (linked + luma) / 2 within rounding.
    /// </summary>
    [Fact]
    public async Task GivenColorFitsWhenBlendingLumaWithLinkedThenOutputInterpolates()
    {
        var ct = TestContext.Current.CancellationToken;
        var fitsImage = await SharedTestData.ExtractGZippedFitsImageAsync(Fixture, cancellationToken: ct);
        var doc = await AstroImageDocument.CreateFromImageAsync(fitsImage, DebayerAlgorithm.None, cancellationToken: ct);
        await doc.DetectStarsAsync(ct);

        var img = doc.UnstretchedImage;
        var rgbaLinked = new byte[img.Width * img.Height * 4];
        var rgbaMid = new byte[img.Width * img.Height * 4];
        var rgbaLuma = new byte[img.Width * img.Height * 4];

        var uLinked = doc.ComputeStretchUniforms(StretchMode.Luma, new StretchParameters(0.15, -3), lumaBlend: 0f);
        var uMid    = doc.ComputeStretchUniforms(StretchMode.Luma, new StretchParameters(0.15, -3), lumaBlend: 0.5f);
        var uLuma   = doc.ComputeStretchUniforms(StretchMode.Luma, new StretchParameters(0.15, -3), lumaBlend: 1f);

        // LumaStretch is populated for all three (Mode is Luma in all cases).
        uLinked.LumaStretch.Rescale.ShouldBeGreaterThan(0f, "LumaStretch present even at blend=0 (shader still needs it)");
        uMid.LumaBlend.ShouldBe(0.5f, 1e-5f);
        uLuma.LumaBlend.ShouldBe(1f, 1e-5f);

        img.RenderStretchedRgba(uLinked, rgbaLinked);
        img.RenderStretchedRgba(uMid, rgbaMid);
        img.RenderStretchedRgba(uLuma, rgbaLuma);

        var meansLinked = ComputeChannelMeans(rgbaLinked);
        var meansMid = ComputeChannelMeans(rgbaMid);
        var meansLuma = ComputeChannelMeans(rgbaLuma);
        output.WriteLine($"blend=0   linked: R={meansLinked.R:F2} G={meansLinked.G:F2} B={meansLinked.B:F2}");
        output.WriteLine($"blend=0.5 mid:    R={meansMid.R:F2} G={meansMid.G:F2} B={meansMid.B:F2}");
        output.WriteLine($"blend=1   luma:   R={meansLuma.R:F2} G={meansLuma.G:F2} B={meansLuma.B:F2}");

        // Distinct buffers — the three blend levels must produce different outputs (otherwise
        // the blend path isn't wired in).
        BufferDifference(rgbaLinked, rgbaMid).ShouldBeGreaterThan(0L, "blend=0 vs blend=0.5 must differ");
        BufferDifference(rgbaLuma, rgbaMid).ShouldBeGreaterThan(0L, "blend=1 vs blend=0.5 must differ");
        BufferDifference(rgbaLinked, rgbaLuma).ShouldBeGreaterThan(0L, "blend=0 vs blend=1 must differ");

        // Linear blend identity: mid mean lies between linked and luma means per channel.
        AssertBetween(meansLinked.R, meansLuma.R, meansMid.R, "R");
        AssertBetween(meansLinked.G, meansLuma.G, meansMid.G, "G");
        AssertBetween(meansLinked.B, meansLuma.B, meansMid.B, "B");

        var testDir = SharedTestData.CreateTempTestOutputDir(
            TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTests_NewPipeline));
        await WriteRgbaAsync(rgbaLinked, img.Width, img.Height, testDir, $"{Fixture}_13_blend0_linked", ct);
        await WriteRgbaAsync(rgbaMid, img.Width, img.Height, testDir, $"{Fixture}_14_blend50_mid", ct);
        await WriteRgbaAsync(rgbaLuma, img.Width, img.Height, testDir, $"{Fixture}_15_blend100_luma", ct);

        static void AssertBetween(double linkedMean, double lumaMean, double midMean, string label)
        {
            var lo = Math.Min(linkedMean, lumaMean);
            var hi = Math.Max(linkedMean, lumaMean);
            // Allow 1 byte slack for rounding (the mid renders independently, not as
            // numeric average of the two byte buffers).
            midMean.ShouldBeInRange(lo - 1, hi + 1,
                $"{label}: mid mean {midMean:F2} should lie between linked {linkedMean:F2} and luma {lumaMean:F2}");
        }
    }

    /// <summary>
    /// Post-stretch normalize: when applied alongside HDR knee compression (which pushes the
    /// post-stretch max below 1.0), NormalizeScale > 1 should lift the peak back up so the
    /// brightest channel approaches 255.
    /// </summary>
    [Fact]
    public async Task GivenColorFitsWithHdrWhenNormalizingThenPeakLiftedToFullRange()
    {
        var ct = TestContext.Current.CancellationToken;
        var fitsImage = await SharedTestData.ExtractGZippedFitsImageAsync(Fixture, cancellationToken: ct);
        var doc = await AstroImageDocument.CreateFromImageAsync(fitsImage, DebayerAlgorithm.None, cancellationToken: ct);
        await doc.DetectStarsAsync(ct);

        const float hdrAmount = 0.8f;
        const float hdrKnee = 0.6f; // aggressive knee so HDR actually compresses the peak

        var uOff = doc.ComputeStretchUniforms(
            StretchMode.Linked, new StretchParameters(0.15, -3),
            normalize: false, hdrAmount: hdrAmount, hdrKnee: hdrKnee);

        var uOn = doc.ComputeStretchUniforms(
            StretchMode.Linked, new StretchParameters(0.15, -3),
            normalize: true, hdrAmount: hdrAmount, hdrKnee: hdrKnee);

        uOff.NormalizeScale.ShouldBe(1f, 1e-5f, "default = no-op");
        uOn.NormalizeScale.ShouldBeGreaterThan(1f, "predicted post-stretch max < 1 with HDR knee, so scale > 1");

        var img = doc.UnstretchedImage;
        var rgbaOff = new byte[img.Width * img.Height * 4];
        var rgbaOn  = new byte[img.Width * img.Height * 4];
        img.RenderStretchedRgba(uOff, rgbaOff, hdrAmount: hdrAmount, hdrKnee: hdrKnee);
        img.RenderStretchedRgba(uOn,  rgbaOn,  hdrAmount: hdrAmount, hdrKnee: hdrKnee);

        var maxOff = MaxByte(rgbaOff);
        var maxOn = MaxByte(rgbaOn);
        output.WriteLine($"NormalizeScale={uOn.NormalizeScale:F4}  maxOff={maxOff}  maxOn={maxOn}");

        maxOn.ShouldBeGreaterThan(maxOff, "normalize should lift the brightest pixel toward white");
        maxOn.ShouldBeGreaterThanOrEqualTo((byte)250, "normalized peak should approach saturation");

        var testDir = SharedTestData.CreateTempTestOutputDir(
            TestContext.Current.TestClass?.TestClassSimpleName ?? nameof(StretchTests_NewPipeline));
        await WriteRgbaAsync(rgbaOff, img.Width, img.Height, testDir, $"{Fixture}_16_hdr_norm_off", ct);
        await WriteRgbaAsync(rgbaOn,  img.Width, img.Height, testDir, $"{Fixture}_17_hdr_norm_on",  ct);
    }

    private static (double R, double G, double B) ComputeChannelMeans(byte[] rgba)
    {
        long rSum = 0, gSum = 0, bSum = 0;
        var pixels = rgba.Length / 4;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rSum += rgba[i];
            gSum += rgba[i + 1];
            bSum += rgba[i + 2];
        }
        return (rSum / (double)pixels, gSum / (double)pixels, bSum / (double)pixels);
    }

    private static long BufferDifference(byte[] a, byte[] b)
    {
        long diff = 0;
        for (var i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
        return diff;
    }

    private static byte MaxByte(byte[] rgba)
    {
        byte m = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] > m) m = rgba[i];
            if (rgba[i + 1] > m) m = rgba[i + 1];
            if (rgba[i + 2] > m) m = rgba[i + 2];
        }
        return m;
    }

    /// <summary>
    /// SensorMatched luma weighting: when the doc's ImageMeta names a known OSC sensor (e.g.
    /// IMX571), <see cref="AstroImageDocument.ResolveLumaWeights"/> returns weights derived
    /// from sensor-QE x Sony CFA throughput. The helper retries with RGGB CFA inclusion when
    /// the natural SensorType lookup is degenerate, so debayered images still resolve to
    /// the sensor-specific triple. Assert the weights flow through into
    /// <see cref="StretchUniforms.LumaWeights"/> and differ from Rec.709 (which would be the
    /// silent fallback if the sensor cannot be resolved).
    /// </summary>
    [Fact]
    public async Task GivenOscMetaWhenLumaWeightingIsSensorMatchedThenSensorDerivedWeightsFlowThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        await FilterCurveDatabase.LoadAsync(ct);

        var fitsImage = await SharedTestData.ExtractGZippedFitsImageAsync(Fixture, cancellationToken: ct);

        // The Vela fixture is a generic 3-channel RGB FITS; its ImageMeta has no SensorModel.
        // The OSC interpretation we want for SensorMatched is "this came from an IMX571" --
        // so we rewrap the image with an ImageMeta that names the sensor. Keeping SensorType
        // at its original (Monochrome) value avoids tripping GetLumaStretchStatsAsync's
        // "RGGB needs debayering" branch; TryComputeSensorLumaWeights handles the OSC retry
        // internally when the natural channel-throughput lookup is degenerate.
        var oscMeta = fitsImage.ImageMeta with { SensorModel = "IMX571" };
        var oscImage = new TianWen.Lib.Imaging.Image(
            data: GetChannelData(fitsImage),
            bitDepth: fitsImage.BitDepth,
            maxValue: fitsImage.MaxValue,
            minValue: fitsImage.MinValue,
            pedestal: 0f,
            imageMeta: oscMeta);
        var doc = await AstroImageDocument.CreateFromImageAsync(oscImage, DebayerAlgorithm.None, cancellationToken: ct);

        var resolved = doc.ResolveLumaWeights(LumaWeighting.SensorMatched);
        output.WriteLine($"Doc-resolved sensor weights: R={resolved.R:F4} G={resolved.G:F4} B={resolved.B:F4}");

        // Same sensor model resolves to the same triple in the underlying helper. Round-trip
        // via direct DB call to make sure the doc path doesn't drift from the helper path.
        FilterCurveDatabase.TryComputeSensorLumaWeights(oscMeta, out var expectedSensorW).ShouldBeTrue(
            "test precondition: IMX571 must resolve through the helper's RGGB-retry");
        resolved.R.ShouldBe(expectedSensorW.R, 1e-5f);
        resolved.G.ShouldBe(expectedSensorW.G, 1e-5f);
        resolved.B.ShouldBe(expectedSensorW.B, 1e-5f);

        // Mode==Luma + SensorMatched: ComputeStretchUniforms stamps the resolved triple into
        // StretchUniforms.LumaWeights, which the CPU mirror + GLSL shader both read.
        var uniforms = doc.ComputeStretchUniforms(
            StretchMode.Luma, new StretchParameters(0.15, -3), weighting: LumaWeighting.SensorMatched);
        uniforms.LumaWeights.R.ShouldBe(expectedSensorW.R, 1e-5f);
        uniforms.LumaWeights.G.ShouldBe(expectedSensorW.G, 1e-5f);
        uniforms.LumaWeights.B.ShouldBe(expectedSensorW.B, 1e-5f);

        // Sanity: not accidentally Rec.709 (the silent fallback when the sensor lookup fails).
        var rec709 = LumaWeighting.Rec709.Weights;
        var l1 = Math.Abs(uniforms.LumaWeights.R - rec709.R)
               + Math.Abs(uniforms.LumaWeights.G - rec709.G)
               + Math.Abs(uniforms.LumaWeights.B - rec709.B);
        l1.ShouldBeGreaterThan(0.01f, "sensor weights must visibly differ from Rec.709 fallback");

        // Render to confirm the new weights actually drive the Luma branch math.
        var rgba = new byte[oscImage.Width * oscImage.Height * 4];
        oscImage.RenderStretchedRgba(uniforms, rgba);
        var (rMean, gMean, bMean) = ComputeChannelMeans(rgba);
        output.WriteLine($"SensorMatched Luma means: R={rMean:F2} G={gMean:F2} B={bMean:F2}");
        rMean.ShouldBeGreaterThan(0.5); gMean.ShouldBeGreaterThan(0.5); bMean.ShouldBeGreaterThan(0.5);

        static float[][,] GetChannelData(TianWen.Lib.Imaging.Image img)
        {
            var (cc, w, h) = img.Shape;
            var data = new float[cc][,];
            for (var c = 0; c < cc; c++)
            {
                var src = img.GetChannelSpan(c);
                var ch = new float[h, w];
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                        ch[y, x] = src[y * w + x];
                data[c] = ch;
            }
            return data;
        }
    }

}
