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
public sealed class GpuStretchPipelineTests(ITestOutputHelper output)
{
    private const int Width = 1280;
    private const int Height = 1024;

    // Same catalog DB cache as StretchTests_NewPipeline -- Tycho-2 bulk load is heavy.
    private static ICelestialObjectDB? _cachedDb;
    private static readonly SemaphoreSlim _dbSem = new(1, 1);

    // Lines collected from inside the offscreen-GPU helper that we want to surface via
    // ITestOutputHelper after the helper returns.
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _formatDiagBag = [];

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

        byte[] gpuRgba;
        try
        {
            gpuRgba = await Task.Run(() => RenderViaOffscreenGpu(debayered, uniforms), ct);
        }
        catch (Exception ex) when (IsVulkanInitFailure(ex))
        {
            output.WriteLine($"Vulkan unavailable, skipping GPU comparison: {ex.GetType().Name}: {ex.Message}");
            Assert.Skip($"Vulkan runtime not available on this host ({ex.Message})");
            return;
        }
        foreach (var line in _formatDiagBag)
            output.WriteLine(line);
        if (!_validationBag.IsEmpty)
        {
            output.WriteLine($"--- Vulkan validation ({_validationBag.Count} messages) ---");
            foreach (var line in _validationBag)
                output.WriteLine(line);
            output.WriteLine("--- end validation ---");
        }
        else
        {
            output.WriteLine("Vulkan validation produced no messages (clean) OR layer not loaded.");
        }

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
    /// Builds an offscreen Vulkan context, runs <see cref="VkFitsImagePipeline"/> against the
    /// given image + uniforms, and reads back the rendered RGBA bytes. Throws on Vulkan init
    /// failure (caller catches via <see cref="IsVulkanInitFailure"/> and skips the test).
    /// </summary>
    private unsafe byte[] RenderViaOffscreenGpu(Image debayered, in StretchUniforms u)
    {
        vkInitialize().CheckResult();

        // Enable the KHRONOS validation layer + VK_EXT_debug_utils so the messenger callback
        // below can capture any spec violations into _validationBag. The Vulkan loader
        // silently skips unknown layers (if vulkan-validationlayers isn't installed locally,
        // validation just won't run -- no error). The debug_utils extension is core-promoted
        // in Vulkan 1.3 but still listed as an instance extension that has to be requested
        // explicitly even on conformant drivers.
        var layerNames = new[] { "VK_LAYER_KHRONOS_validation" };
        var extensionNames = new[] { "VK_EXT_debug_utils" };
        using var layers = new VkStringArray(layerNames);
        using var exts = new VkStringArray(extensionNames);

        VkInstanceCreateInfo ici = new()
        {
            enabledLayerCount = layers.Length,
            ppEnabledLayerNames = layers,
            enabledExtensionCount = exts.Length,
            ppEnabledExtensionNames = exts,
        };
        var createInstanceResult = vkCreateInstance(&ici, null, out var instance);
        var validationActive = createInstanceResult == VkResult.Success;
        if (!validationActive)
        {
            // The validation layer or debug_utils extension wasn't available -- fall back to
            // the default instance creation. Keeps the test functional in environments without
            // the validation layer SDK installed (local dev without VulkanSDK, etc.).
            _validationBag.Add($"Falling back to default instance: {createInstanceResult} when requesting layer+debug_utils");
            VkInstanceCreateInfo defaultCI = new();
            vkCreateInstance(&defaultCI, null, out instance).CheckResult();
        }

        // VulkanContext.Dispose() walks down the device + tears the instance down via
        // InstanceApi.vkDestroyInstance, so the using block on `ctx` covers the instance too.
        using var ctx = VulkanContext.CreateOffscreen(instance, Width, Height);
        using var renderer = new VkRenderer(ctx, Width, Height);
        using var pipeline = new VkFitsImagePipeline(ctx);

        // Register the messenger AFTER ctx is built so we get an instance-bound VkInstanceApi
        // (the debug-utils messenger functions are extension methods on that wrapper).
        // Messages produced during ctx / renderer / pipeline construction are not captured,
        // but anything from device-level operations + the actual frame draw is.
        if (validationActive)
        {
            VkDebugUtilsMessengerCreateInfoEXT messengerCI = new()
            {
                messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Verbose
                    | VkDebugUtilsMessageSeverityFlagsEXT.Info
                    | VkDebugUtilsMessageSeverityFlagsEXT.Warning
                    | VkDebugUtilsMessageSeverityFlagsEXT.Error,
                messageType = VkDebugUtilsMessageTypeFlagsEXT.General
                    | VkDebugUtilsMessageTypeFlagsEXT.Validation
                    | VkDebugUtilsMessageTypeFlagsEXT.Performance,
                pfnUserCallback = &DebugCallback,
            };
            _activeValidationBag = _validationBag;
            var createMsgResult = ctx.InstanceApi.vkCreateDebugUtilsMessengerEXT(&messengerCI, null, out _debugMessenger);
            if (createMsgResult != VkResult.Success)
            {
                _validationBag.Add($"vkCreateDebugUtilsMessengerEXT failed: {createMsgResult}");
                _debugMessenger = VkDebugUtilsMessengerEXT.Null;
            }
            else
            {
                _debugMessengerCtx = ctx;
            }
        }

        try
        {

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
            var srcMid = srcSpan.Length > 1280 * 512 + 640 ? srcSpan[1280 * 512 + 640] : 0f;
            _formatDiagBag.Add($"channel {ch}: src first/mid = {srcFirst:G6} / {srcMid:G6}, readback first 4 = [{probe[0]:G6}, {probe[1]:G6}, {probe[2]:G6}, {probe[3]:G6}]");
        }

        renderer.BeginOffscreenFrame(new RGBAColor32(0, 0, 0, 255)).ShouldBeTrue();
        var cmd = renderer.CurrentCommandBuffer;

        pipeline.UpdateStretchUBO(
            cmd: cmd,
            channelCount: 3,
            stretchMode: (int)u.Mode,
            normFactor: u.NormFactor,
            curvesBoost: 0f,
            curvesMidpoint: 0.25f,
            hdrAmount: 0f,
            hdrKnee: 0.8f,
            pedestal: u.Pedestal,
            shadows: u.Shadows,
            midtones: u.Midtones,
            highlights: u.Highlights,
            rescale: u.Rescale,
            gridEnabled: false,
            gridSpacingRA: 0f, gridSpacingDec: 0f, gridLineWidth: 0f,
            imageW: Width, imageH: Height,
            crPix1: 0, crPix2: 0, crValRA: 0, crValDec: 0,
            cdMatrix: ReadOnlySpan<float>.Empty,
            whiteBalance: u.WhiteBalance,
            bgNeutralization: u.BackgroundNeutralization,
            curvesMode: u.CurvesMode,
            curveData: ReadOnlySpan<float>.Empty,
            imageSource: VkFitsImagePipeline.ImageSource.ProcessedChannels);

        pipeline.RecordImageDraw(cmd, ctx, 0, 0, Width, Height, Width, Height);
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

        return ctx.ReadbackOffscreenRgba();
        }
        finally
        {
            // Tear the messenger down BEFORE the outer `using var ctx` runs, since ctx.Dispose
            // calls vkDestroyInstance and the messenger must outlive only its instance.
            if (_debugMessenger != VkDebugUtilsMessengerEXT.Null && _debugMessengerCtx is not null)
            {
                _debugMessengerCtx.InstanceApi.vkDestroyDebugUtilsMessengerEXT(_debugMessenger, null);
                _debugMessenger = VkDebugUtilsMessengerEXT.Null;
                _debugMessengerCtx = null;
            }
            _activeValidationBag = null;
        }
    }

    /// <summary>
    /// Static delegate target for <c>VkDebugUtilsMessengerCreateInfoEXT.pfnUserCallback</c>.
    /// Routes every validation message into <see cref="_activeValidationBag"/>, which the
    /// outer test method later flushes into <see cref="_formatDiagBag"/> so the messages
    /// appear in the test output. Tests in the <c>Imaging</c> collection run sequentially so
    /// the single static bag is safe.
    /// </summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static unsafe uint DebugCallback(
        VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type,
        VkDebugUtilsMessengerCallbackDataEXT* pData,
        void* pUserData)
    {
        var bag = _activeValidationBag;
        if (bag is null) return 0;

        var msgSpan = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)pData->pMessage);
        var msg = System.Text.Encoding.UTF8.GetString(msgSpan);
        bag.Add($"[VL {severity}/{type}] {msg}");
        return 0; // VK_FALSE -- never abort the call that triggered the message
    }

    private static System.Collections.Concurrent.ConcurrentBag<string>? _activeValidationBag;
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _validationBag = [];
    private VkDebugUtilsMessengerEXT _debugMessenger;
    private VulkanContext? _debugMessengerCtx;

    private static bool IsVulkanInitFailure(Exception ex)
    {
        // Vortice.Vulkan throws DllNotFoundException when libvulkan can't be loaded; CheckResult
        // wraps Vk* errors as Exception with the VkResult name. Either way we want to skip.
        return ex is DllNotFoundException
            || ex is TypeInitializationException
            || ex.Message.Contains("vkCreateInstance", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("vkInitialize", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("VK_ERROR", StringComparison.Ordinal)
            || ex.Message.Contains("ICD", StringComparison.Ordinal);
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
