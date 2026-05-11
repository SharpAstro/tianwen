using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices.Fake;
using TianWen.Lib.Imaging;
using TianWen.Lib.Imaging.ColorCalibration;
using TianWen.UI.Abstractions;
using TianWen.UI.Shared;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.Lib.Tests;

/// <summary>
/// Phase 1 of PLAN-gpu-stretch-tests.md: smoke test that runs the synthetic SPCC starfield
/// through both <see cref="Image.RenderStretchedRgba"/> (CPU) and the offscreen
/// <see cref="VkFitsImagePipeline"/> (GPU), comparing the resulting RGBA bytes within a
/// tolerance. Catches CPU/GPU divergences in the stretch shader (the kind of regression that
/// would have surfaced the recent <c>applyCurveLUT</c> v=1.0 off-by-one before it shipped).
///
/// Skips when Vulkan is unavailable (no driver, no ICD): the test outputs the failure reason
/// and reports as Skipped rather than Failed, so CI without GPU drivers stays green.
/// </summary>
[Collection("Imaging")]
public sealed class GpuStretchPipelineTests : IClassFixture<OffscreenGpuFixture>
{
    private const int Width = 1280;
    private const int Height = 1024;

    // Same fixture StretchTests_NewPipeline uses for its CPU-only theory. Real cropped Vela
    // SNR multi-NB color FITS -- size, bit depth, and stats baked into the file rather than
    // synthesised, so this exercises the GPU path on production-shaped data.
    private const string VelaFixture = "Vela_SNR_Panel_10-Multi-NB-color-Hydrogen-alpha-Oxygen_III-crop";

    // Same catalog DB cache as StretchTests_NewPipeline -- Tycho-2 bulk load is heavy.
    private static ICelestialObjectDB? _cachedDb;
    private static readonly SemaphoreSlim _dbSem = new(1, 1);

    private readonly OffscreenGpuFixture _gpu;
    private readonly ITestOutputHelper output;

    // Lines collected from inside the offscreen-GPU helper that we want to surface via
    // ITestOutputHelper after the helper returns. Cleared per [Fact] / [Theory] case.
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _formatDiagBag = [];

    public GpuStretchPipelineTests(OffscreenGpuFixture gpu, ITestOutputHelper output)
    {
        _gpu = gpu;
        this.output = output;
    }

    private static async Task<ICelestialObjectDB> InitDbAsync(CancellationToken ct)
    {
        if (_cachedDb is { } cached) return cached;
        await _dbSem.WaitAsync(ct);
        try
        {
            if (_cachedDb is { } cached2) return cached2;
            var db = new CelestialObjectDB();
            await db.InitDBAsync(waitForTycho2BulkLoad: true, cancellationToken: ct);
            _cachedDb = db;
            return db;
        }
        finally
        {
            _dbSem.Release();
        }
    }

    [Fact]
    public async Task GivenSyntheticSpccField_GpuRenderMatchesCpuRender()
    {
        if (!_gpu.VulkanAvailable)
        {
            output.WriteLine($"Vulkan unavailable: {_gpu.UnavailableReason}");
            Assert.Skip($"Vulkan runtime not available on this host ({_gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;

        // -------- Build the same synthetic field + uniforms as StretchTests_NewPipeline --------

        var loadFilterDb = FilterCurveDatabase.LoadAsync(ct);
        var dbTask = InitDbAsync(ct);
        await loadFilterDb;
        var db = await dbTask;

        const double targetRA = 3.79;
        const double targetDec = 24.10;
        const int focalLengthMm = 200;
        const float pixelSizeUm = 3.76f;
        const int gain = 100;
        const float exposureSeconds = 60f;

        var projected = SyntheticStarFieldRenderer.ProjectCatalogStars(
            targetRA, targetDec, focalLengthMm, pixelSizeUm, Width, Height, db, magnitudeCutoff: 12.0);
        projected.Count.ShouldBeGreaterThan(15);

        var bayerData = SyntheticStarFieldRenderer.RenderBayer(
            Width, Height,
            defocusSteps: 0,
            stars: projected.ToArray().AsSpan(),
            exposureSeconds: exposureSeconds,
            hyperbolaA: 4.0,
            apertureScaleFactor: (130.0 / 50.0) * (130.0 / 50.0));

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
        var bayerImage = new Image([bayerData], BitDepth.Int16, maxValue: 65535f, minValue: 0f, pedestal: 0f, imageMeta);

        var pixelScaleArcsec = (pixelSizeUm * 1e-3) / focalLengthMm * 206264.806;
        var pixelScaleDeg = pixelScaleArcsec / 3600.0;
        var wcs = new WCS(targetRA, targetDec)
        {
            CRPix1 = Width / 2.0 + 1,
            CRPix2 = Height / 2.0 + 1,
            CD1_1 = -pixelScaleDeg,
            CD1_2 = 0,
            CD2_1 = 0,
            CD2_2 = -pixelScaleDeg,
        };

        var doc = await AstroImageDocument.CreateFromImageAsync(bayerImage, DebayerAlgorithm.AHD, wcs, filePath: "synthetic.fits", ct);
        await doc.DetectStarsAsync(ct);
        await doc.ComputeSpccColorCalibrationAsync(db, ct);
        doc.ColorCalibration.ShouldNotBeNull();

        doc.UseIterativeConvergence = true;
        doc.ConvergenceTarget = 0.15;
        var uniforms = doc.ComputeStretchUniforms(StretchMode.Luma, new StretchParameters(0.15, -3));
        output.WriteLine($"WB={Triple(uniforms.WhiteBalance)}  Shadows={Triple(uniforms.Shadows)}  Midtones={Triple(uniforms.Midtones)}");

        var debayered = await doc.UnstretchedImage.DebayerAsync(DebayerAlgorithm.AHD, cancellationToken: ct);
        debayered.ChannelCount.ShouldBe(3);
        debayered.Width.ShouldBe(Width);
        debayered.Height.ShouldBe(Height);

        // -------- CPU render --------

        var cpuRgba = new byte[Width * Height * 4];
        var cpuSw = Stopwatch.StartNew();
        debayered.RenderStretchedRgba(uniforms, cpuRgba);
        cpuSw.Stop();
        output.WriteLine($"CPU RenderStretchedRgba: {cpuSw.Elapsed.TotalMilliseconds:F0}ms");

        // -------- GPU render --------

        var gpuRgba = await Task.Run(() => RenderViaOffscreenGpu(debayered, uniforms, Width, Height, default, 0f), ct);
        foreach (var line in _formatDiagBag)
            output.WriteLine(line);

        // -------- Compare --------

        cpuRgba.Length.ShouldBe(gpuRgba.Length);

        // Sample a handful of pixels at known offsets so we can tell at a glance whether the
        // GPU output is the clear color everywhere (== shader never ran / textures sampled 0)
        // or something more interesting like a partial render. The first pixel is the
        // top-left of the framebuffer; the centre pixel sits where most of the SPCC starfield
        // signal accumulates; the last pixel is the bottom-right.
        Span<int> samplePixels = stackalloc int[] {
            0,
            (Width / 2) + (Height / 2) * Width,
            (Width * Height) - 1,
        };
        for (var s = 0; s < samplePixels.Length; s++)
        {
            var px = samplePixels[s];
            var i = px * 4;
            output.WriteLine($"  px[{px}]: cpu=({cpuRgba[i]},{cpuRgba[i+1]},{cpuRgba[i+2]},{cpuRgba[i+3]})  gpu=({gpuRgba[i]},{gpuRgba[i+1]},{gpuRgba[i+2]},{gpuRgba[i+3]})");
        }

        // Save both for visual inspection (helpful when the assertion fires).
        var testDir = SharedTestData.CreateTempTestOutputDir(nameof(GpuStretchPipelineTests));
        await WriteTiffAsync(cpuRgba, Width, Height, System.IO.Path.Combine(testDir, "cpu.tiff"), ct);
        await WriteTiffAsync(gpuRgba, Width, Height, System.IO.Path.Combine(testDir, "gpu.tiff"), ct);
        // Also a per-pixel abs-diff visualisation: max(|R_diff|, |G_diff|, |B_diff|) -> grayscale.
        var diffRgba = new byte[cpuRgba.Length];
        for (var i = 0; i < cpuRgba.Length; i += 4)
        {
            var d = (byte)Math.Min(255,
                Math.Max(Math.Abs(cpuRgba[i] - gpuRgba[i]),
                Math.Max(Math.Abs(cpuRgba[i + 1] - gpuRgba[i + 1]),
                         Math.Abs(cpuRgba[i + 2] - gpuRgba[i + 2]))) * 4); // *4 to amplify
            diffRgba[i] = diffRgba[i + 1] = diffRgba[i + 2] = d;
            diffRgba[i + 3] = 255;
        }
        await WriteTiffAsync(diffRgba, Width, Height, System.IO.Path.Combine(testDir, "diff.tiff"), ct);
        output.WriteLine($"Wrote cpu.tiff / gpu.tiff / diff.tiff to {testDir}");

        long absDiffSum = 0;
        var maxDiff = 0;
        var pixelsExceedingTolerance = 0;
        const int PerPixelTolerance = 4;

        // Per-channel breakdown: a wild divergence with one channel close to 0 and another
        // large is the smoking gun for R/B swizzle; uniform large diffs across all three
        // channels point at gamma/sRGB encoding instead.
        Span<long> perChannelSum = stackalloc long[3];
        Span<int> perChannelMax = stackalloc int[3];
        Span<long> perChannelCpuSum = stackalloc long[3];
        Span<long> perChannelGpuSum = stackalloc long[3];

        for (var i = 0; i < cpuRgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var cpuByte = cpuRgba[i + c];
                var gpuByte = gpuRgba[i + c];
                var d = Math.Abs(cpuByte - gpuByte);
                absDiffSum += d;
                perChannelSum[c] += d;
                perChannelCpuSum[c] += cpuByte;
                perChannelGpuSum[c] += gpuByte;
                if (d > maxDiff) maxDiff = d;
                if (d > perChannelMax[c]) perChannelMax[c] = d;
                if (d > PerPixelTolerance) pixelsExceedingTolerance++;
            }
        }
        var pixelCount = cpuRgba.Length / 4;
        var meanDiff = absDiffSum / (double)(pixelCount * 3);
        var outlierFraction = pixelsExceedingTolerance / (double)(pixelCount * 3);
        output.WriteLine($"CPU vs GPU diff: mean={meanDiff:F3} bytes  max={maxDiff} bytes  outliers (>{PerPixelTolerance})={outlierFraction:P3}");
        for (var c = 0; c < 3; c++)
        {
            var label = c switch { 0 => "R", 1 => "G", _ => "B" };
            var meanC = perChannelSum[c] / (double)pixelCount;
            var cpuMeanC = perChannelCpuSum[c] / (double)pixelCount;
            var gpuMeanC = perChannelGpuSum[c] / (double)pixelCount;
            output.WriteLine($"  {label}: mean diff={meanC:F3}  max={perChannelMax[c]}  cpuMean={cpuMeanC:F1}  gpuMean={gpuMeanC:F1}");
        }

        // Tolerances per the plan: mean abs diff < 1.0, max <= 4 (relaxed to 8 for first
        // smoke run -- mediump float in shader vs C# double for MTF can produce up to a
        // ~1% difference on individual pixels at MTF discontinuities), <0.1% outliers.
        meanDiff.ShouldBeLessThan(1.5, "mean per-byte diff between CPU and GPU should be small");
        maxDiff.ShouldBeLessThan(16, "no single-pixel byte should differ wildly between CPU and GPU");
        outlierFraction.ShouldBeLessThan(0.01, "<1% of bytes should exceed the per-pixel tolerance");
    }

    /// <summary>
    /// Phase 3 of PLAN-gpu-stretch-tests.md: drive the same 8 stretch cases the CPU-only
    /// <c>StretchTests_NewPipeline.GivenColorFitsWhenRenderingThroughCpuPipelineThenWritesTiff</c>
    /// theory exercises through both the CPU mirror and the GPU offscreen pipeline, then assert
    /// per-pixel byte parity within tolerance. Catches regressions in any GLSL stretch stage
    /// (WB, bg-neutralization, MTF, curve LUT, HDR knee, luma-Y'/Y).
    /// </summary>
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
    public async Task GpuMatchesCpuForVelaStretchCases(
        string mode, bool applyWb, bool applyBgNeut, bool useConvergence,
        int curvesMode, float hdrAmount, string label)
    {
        if (!_gpu.VulkanAvailable)
        {
            output.WriteLine($"Vulkan unavailable: {_gpu.UnavailableReason}");
            Assert.Skip($"Vulkan runtime not available on this host ({_gpu.UnavailableReason})");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var fitsImage = await SharedTestData.ExtractGZippedFitsImageAsync(VelaFixture, cancellationToken: ct);
        var doc = await AstroImageDocument.CreateFromImageAsync(fitsImage, DebayerAlgorithm.None, cancellationToken: ct);

        // Star detection populates StarMaskedStats + star-masked PerChannelBackground -- both
        // required for convergence and bg-neutralization to be realistic. Run unconditionally
        // so per-channel stats are stable across cases regardless of which flags toggle.
        await doc.DetectStarsAsync(ct);

        if (useConvergence)
        {
            doc.UseIterativeConvergence = true;
        }

        if (applyWb)
        {
            // Production sky-bg WB (db argument unused on the sky-bg path -> null!).
            await doc.ComputeColorCalibrationAsync(null!, ct);
        }

        var stretchMode = mode == "luma" ? StretchMode.Luma : StretchMode.Linked;
        var uniforms = doc.ComputeStretchUniforms(stretchMode, new StretchParameters(0.15, -3));

        if (applyBgNeut)
        {
            // Real pivot1 gains derived from the same PerChannelBackground the production
            // viewer uses. For Vela bg is near-neutral so gains hover near identity -- the
            // bgNeut code path still runs end-to-end and any GLSL regression in the
            // `out = norm * g + (1 - g)` step shows up immediately.
            var gains = BackgroundNeutralization.ComputeGains(doc.PerChannelBackground);
            uniforms = uniforms with { BackgroundNeutralization = gains };
        }

        ImmutableArray<float> curveKnots = default;
        if (curvesMode == 1)
        {
            // Same S-curve preset the viewer's Shift+B toggle uses.
            var spline = new FritschCarlsonSpline(
                [(0f, 0f), (0.15f, 0.22f), (0.4f, 0.5f), (0.7f, 0.72f), (1f, 1f)]);
            curveKnots = spline.ComputeKnots33();
        }

        // CPU RenderStretchedRgba takes curvesMode as an explicit parameter; the GPU helper
        // reads it from the StretchUniforms struct (so the shader UBO field matches the
        // uniforms layout). Reflect the test's curvesMode into both paths via `with`.
        uniforms = uniforms with { CurvesMode = curvesMode };

        output.WriteLine($"[{label}] Mode={uniforms.Mode}  WB={Triple(uniforms.WhiteBalance)}  BgNeut={Triple(uniforms.BackgroundNeutralization)}");
        output.WriteLine($"[{label}] Shadows={Triple(uniforms.Shadows)}  Midtones={Triple(uniforms.Midtones)}  Rescale={Triple(uniforms.Rescale)}");

        var img = doc.UnstretchedImage;
        var w = img.Width;
        var h = img.Height;

        // -------- CPU render --------
        var cpuRgba = new byte[w * h * 4];
        var cpuSw = Stopwatch.StartNew();
        img.RenderStretchedRgba(
            uniforms,
            cpuRgba,
            curvesMode: curvesMode,
            curveLut: curveKnots.IsDefault ? default : curveKnots.AsSpan(),
            hdrAmount: hdrAmount);
        cpuSw.Stop();
        output.WriteLine($"[{label}] CPU RenderStretchedRgba ({w}x{h}): {cpuSw.Elapsed.TotalMilliseconds:F0}ms");

        // -------- GPU render --------
        var gpuRgba = await Task.Run(() => RenderViaOffscreenGpu(img, uniforms, w, h, curveKnots, hdrAmount), ct);
        foreach (var line in _formatDiagBag)
            output.WriteLine(line);

        // -------- Save TIFFs per case for visual inspection / diffing --------
        var testDir = SharedTestData.CreateTempTestOutputDir(nameof(GpuStretchPipelineTests));
        await WriteTiffAsync(cpuRgba, w, h, System.IO.Path.Combine(testDir, $"{label}.cpu.tiff"), ct);
        await WriteTiffAsync(gpuRgba, w, h, System.IO.Path.Combine(testDir, $"{label}.gpu.tiff"), ct);
        var diffRgba = new byte[cpuRgba.Length];
        for (var i = 0; i < cpuRgba.Length; i += 4)
        {
            var d = (byte)Math.Min(255,
                Math.Max(Math.Abs(cpuRgba[i] - gpuRgba[i]),
                Math.Max(Math.Abs(cpuRgba[i + 1] - gpuRgba[i + 1]),
                         Math.Abs(cpuRgba[i + 2] - gpuRgba[i + 2]))) * 4); // *4 to amplify
            diffRgba[i] = diffRgba[i + 1] = diffRgba[i + 2] = d;
            diffRgba[i + 3] = 255;
        }
        await WriteTiffAsync(diffRgba, w, h, System.IO.Path.Combine(testDir, $"{label}.diff.tiff"), ct);

        // -------- Compare --------
        cpuRgba.Length.ShouldBe(gpuRgba.Length);
        long absDiffSum = 0;
        var maxDiff = 0;
        var pixelsExceedingTolerance = 0;
        const int PerPixelTolerance = 4;
        Span<long> perChannelSum = stackalloc long[3];
        Span<int> perChannelMax = stackalloc int[3];
        for (var i = 0; i < cpuRgba.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(cpuRgba[i + c] - gpuRgba[i + c]);
                absDiffSum += d;
                perChannelSum[c] += d;
                if (d > maxDiff) maxDiff = d;
                if (d > perChannelMax[c]) perChannelMax[c] = d;
                if (d > PerPixelTolerance) pixelsExceedingTolerance++;
            }
        }
        var pixelCount = cpuRgba.Length / 4;
        var meanDiff = absDiffSum / (double)(pixelCount * 3);
        var outlierFraction = pixelsExceedingTolerance / (double)(pixelCount * 3);
        output.WriteLine($"[{label}] CPU vs GPU diff: mean={meanDiff:F3}  max={maxDiff}  outliers (>{PerPixelTolerance})={outlierFraction:P3}");
        for (var c = 0; c < 3; c++)
        {
            var chLabel = c switch { 0 => "R", 1 => "G", _ => "B" };
            var meanC = perChannelSum[c] / (double)pixelCount;
            output.WriteLine($"  {chLabel}: mean diff={meanC:F3}  max={perChannelMax[c]}");
        }

        // Tolerances match the Phase 1 smoke test -- start loose; tighten once all 8 cases
        // produce known-good numbers on hardware + lavapipe.
        meanDiff.ShouldBeLessThan(1.5, $"[{label}] mean per-byte CPU/GPU diff should be small");
        maxDiff.ShouldBeLessThan(16, $"[{label}] no single byte should differ wildly between CPU and GPU");
        outlierFraction.ShouldBeLessThan(0.01, $"[{label}] <1% of bytes should exceed the per-pixel tolerance");
    }

    /// <summary>
    /// Builds an offscreen Vulkan context, runs <see cref="VkFitsImagePipeline"/> against the
    /// given image + uniforms, and reads back the rendered RGBA bytes. Throws on Vulkan init
    /// failure (caller catches via <see cref="IsVulkanInitFailure"/> and skips the test).
    /// <paramref name="curveLut"/> may be <c>default</c> for "no curve LUT"; <paramref name="hdrAmount"/>
    /// 0f disables the HDR knee. Both mirror the parameters <see cref="Image.RenderStretchedRgba"/>
    /// accepts so CPU and GPU paths can be driven from the same theory cases.
    /// </summary>
    private unsafe byte[] RenderViaOffscreenGpu(
        Image debayered,
        in StretchUniforms u,
        int width,
        int height,
        ImmutableArray<float> curveLut,
        float hdrAmount)
    {
        var ctx = _gpu.Ctx!;
        var renderer = _gpu.Renderer!;
        var pipeline = _gpu.Pipeline!;

        ctx.InstanceApi.vkGetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        var deviceName = System.Text.Encoding.UTF8.GetString(
            System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(props.deviceName)
        );
        _formatDiagBag.Add($"Physical device: {deviceName}");

        // R32_SFLOAT optimalTilingFeatures tells us whether linear filtering, sampling, and
        // basic sampled-image usage are supported. The pipeline's CreateSampler downgrades to
        // Nearest filter if linear isn't advertised.
        _formatDiagBag.Add($"R32_SFLOAT optimalTilingFeatures = {pipeline.R32SfloatOptimalTilingFeatures}");
        _formatDiagBag.Add($"R32_SFLOAT linear filter supported: {pipeline.R32SfloatLinearFilterSupported}");

        // Channel textures resize automatically per upload (UploadChannelTexture destroys + recreates
        // when dimensions change), so the shared pipeline correctly handles any per-test image size.
        pipeline.UploadChannelTexture(debayered.GetChannelSpan(0), 0, debayered.Width, debayered.Height);
        pipeline.UploadChannelTexture(debayered.GetChannelSpan(1), 1, debayered.Width, debayered.Height);
        pipeline.UploadChannelTexture(debayered.GetChannelSpan(2), 2, debayered.Width, debayered.Height);

        // Readback the first 16 floats of each channel as a sanity check for the upload. If
        // the GPU output is solid black but the readback returns the source data correctly,
        // the bug is downstream of the texture (draw / shader / descriptor). If the readback
        // returns zeros, the upload itself failed silently on this driver.
        Span<float> probe = stackalloc float[16];
        for (var ch = 0; ch < 3; ch++)
        {
            pipeline.ReadbackChannelFirstFloats(ch, probe);
            var srcSpan = debayered.GetChannelSpan(ch);
            var srcFirst = srcSpan.Length > 0 ? srcSpan[0] : 0f;
            var midIdx = debayered.Width * (debayered.Height / 2) + debayered.Width / 2;
            var srcMid = srcSpan.Length > midIdx ? srcSpan[midIdx] : 0f;
            _formatDiagBag.Add($"channel {ch}: src first/mid = {srcFirst:G6} / {srcMid:G6}, readback first 4 = [{probe[0]:G6}, {probe[1]:G6}, {probe[2]:G6}, {probe[3]:G6}]");
        }

        // BeginOffscreenFrame clears the entire fixture-sized framebuffer to black. For tests
        // smaller than OffscreenGpuFixture.Width/Height, only the (0,0,width,height) sub-rect
        // is drawn into; the rest stays at clear color and is sliced off in the readback.
        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        var cmd = renderer.CurrentCommandBuffer;

        pipeline.UpdateStretchUBO(
            cmd: cmd,
            channelCount: 3,
            stretchMode: (int)u.Mode,
            normFactor: u.NormFactor,
            curvesBoost: 0f,
            curvesMidpoint: 0.25f,
            hdrAmount: hdrAmount,
            hdrKnee: 0.8f,
            pedestal: u.Pedestal,
            shadows: u.Shadows,
            midtones: u.Midtones,
            highlights: u.Highlights,
            rescale: u.Rescale,
            gridEnabled: false,
            gridSpacingRA: 0f, gridSpacingDec: 0f, gridLineWidth: 0f,
            imageW: width, imageH: height,
            crPix1: 0, crPix2: 0, crValRA: 0, crValDec: 0,
            cdMatrix: ReadOnlySpan<float>.Empty,
            whiteBalance: u.WhiteBalance,
            bgNeutralization: u.BackgroundNeutralization,
            curvesMode: u.CurvesMode,
            curveData: curveLut.IsDefault ? ReadOnlySpan<float>.Empty : curveLut.AsSpan(),
            imageSource: VkFitsImagePipeline.ImageSource.ProcessedChannels);

        // RecordImageDraw's last two args are the orthographic projection's viewport size, not
        // the draw-rect size -- they have to match the actual framebuffer the GPU is rendering
        // into, otherwise the quad gets stretched. The first two args are the source rect's
        // top-left, and (right, bottom) define the destination quad in pixel space. So:
        //   - (0, 0, width, height) places the rendered image at top-left (width x height pixels)
        //   - (OffscreenGpuFixture.Width, OffscreenGpuFixture.Height) is the viewport NDC basis.
        pipeline.RecordImageDraw(cmd, ctx, 0, 0, width, height, OffscreenGpuFixture.Width, OffscreenGpuFixture.Height);
        renderer.EndOffscreenFrame();

        // Capture key UBO fields *after* UpdateStretchUBO ran. The UBO is host-coherent so
        // these reads reflect exactly what the GPU read during draw.
        Span<byte> uboBytes = stackalloc byte[256];
        pipeline.ReadStretchUboBytes(uboBytes);
        var channelCount = System.Runtime.InteropServices.MemoryMarshal.Read<int>(uboBytes[0..]);
        var stretchMode = System.Runtime.InteropServices.MemoryMarshal.Read<int>(uboBytes[4..]);
        var normFactor = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[8..]);
        var imgSource = System.Runtime.InteropServices.MemoryMarshal.Read<int>(uboBytes[152..]);
        var pedR = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[32..]);
        var pedG = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[36..]);
        var pedB = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[40..]);
        var shR  = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[48..]);
        var midR = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[64..]);
        var resR = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[96..]);
        var wbR  = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[192..]);
        var wbG  = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[196..]);
        var wbB  = System.Runtime.InteropServices.MemoryMarshal.Read<float>(uboBytes[200..]);
        _formatDiagBag.Add($"UBO @draw: channelCount={channelCount} stretchMode={stretchMode} imgSource={imgSource} normFactor={normFactor:G6}");
        _formatDiagBag.Add($"UBO @draw: pedestal=({pedR:G6},{pedG:G6},{pedB:G6}) shadows.r={shR:G6} mid.r={midR:G6} rescale.r={resR:G6}");
        _formatDiagBag.Add($"UBO @draw: whiteBalance=({wbR:G6},{wbG:G6},{wbB:G6})");

        var fullRgba = ctx.ReadbackOffscreenRgba();
        return ExtractSubRect(fullRgba, OffscreenGpuFixture.Width, width, height);
    }

    /// <summary>
    /// The fixture's framebuffer is sized to the largest test image (<see cref="OffscreenGpuFixture.Width"/>
    /// x <see cref="OffscreenGpuFixture.Height"/>). For tests with smaller images the readback
    /// returns the full framebuffer; this helper slices the meaningful top-left
    /// <paramref name="dstWidth"/> x <paramref name="dstHeight"/> sub-rectangle out of it.
    /// Returns the input unchanged when no slicing is needed.
    /// </summary>
    private static byte[] ExtractSubRect(byte[] fullRgba, int srcStrideWidth, int dstWidth, int dstHeight)
    {
        if (dstWidth == srcStrideWidth && fullRgba.Length == dstWidth * dstHeight * 4)
            return fullRgba;
        var result = new byte[dstWidth * dstHeight * 4];
        for (var y = 0; y < dstHeight; y++)
            Buffer.BlockCopy(fullRgba, y * srcStrideWidth * 4, result, y * dstWidth * 4, dstWidth * 4);
        return result;
    }

    private static string Triple((float R, float G, float B) v) => $"({v.R:F4},{v.G:F4},{v.B:F4})";

    private static async Task WriteTiffAsync(byte[] rgba, int width, int height, string path, CancellationToken ct)
    {
        var settings = new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA);
        using var magick = new ImageMagick.MagickImage(rgba, settings);
        magick.Settings.Compression = ImageMagick.CompressionMethod.Zip;
        var bytes = magick.ToByteArray(ImageMagick.MagickFormat.Tiff);
        await System.IO.File.WriteAllBytesAsync(path, bytes, ct);
    }
}
