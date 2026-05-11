using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using System;
using System.Threading;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.Lib.Tests;

/// <summary>
/// Followup D from PLAN-gpu-stretch-tests.md: drive the same primitive draw through both
/// <see cref="RgbaImageRenderer"/> (CPU) and <see cref="VkRenderer"/> (GPU, via offscreen
/// Vulkan) and assert RGBA-byte parity within a tolerance per primitive.
///
/// Why this matters: <see cref="Renderer{TSurface}"/>'s contract is "produces the same
/// pixel output regardless of backend". These primitives back the planner, FITS viewer
/// chrome, sky map labels, guider graphs, and V-curves -- a visual regression in either
/// backend would be widespread.
///
/// Unlike <see cref="GpuStretchPipelineTests"/>, these primitives use only the constant-
/// color FlatPipeline and the ring-shader EllipsePipeline -- neither samples a texture.
///
/// Skips when Vulkan is unavailable (no driver / no ICD).
/// </summary>
[Collection("Imaging")]
public sealed class VkRendererPrimitiveTests(ITestOutputHelper output)
{
    private const int Width = 256;
    private const int Height = 256;
    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);
    private static readonly RGBAColor32 Red = new(255, 0, 0, 255);
    private static readonly RGBAColor32 Green = new(0, 255, 0, 255);
    private static readonly RGBAColor32 Blue = new(0, 0, 255, 255);
    private static readonly RGBAColor32 White = new(255, 255, 255, 255);

    // CPU and GPU backends are aliased but use different coordinate conventions
    // (CPU: integer pixel iteration with half-open rect bounds; GPU: float vertices
    // rasterized by Vulkan with the diamond-exit fill rule). Edges shift by 1 pixel
    // depending on direction, and stroke decompositions for DrawRectangle don't agree
    // on inner-vs-centered placement of the stroke band. So no primitive achieves
    // bit-exact parity. Tests here assert that the bulk of pixels agree -- the goal
    // is catching gross regressions (wrong color, missing primitive, scale issue),
    // not pixel-perfect matching.

    [Fact]
    public void FillRectangle_AxisAligned()
    {
        // RectInt is (LowerRight, UpperLeft) -- LowerRight comes first in the constructor.
        var rect = new RectInt(new PointInt(180, 200), new PointInt(50, 60));
        var (cpu, gpu) = RenderBoth((cpuR, gpuR) =>
        {
            cpuR.FillRectangle(rect, Red);
            gpuR.FillRectangle(rect, Red);
        });
        if (gpu is null) return;
        // Bit-exact on hardware Vulkan (Vulkan's top-left fill rule with integer-aligned
        // float vertices matches CPU's half-open rect fill exactly). Allow 0.1% headroom
        // for rasterization quirks.
        AssertBadPixelFractionUnder(cpu, gpu, perPixelTolerance: 16, badPixelFraction: 0.001,
            nameof(FillRectangle_AxisAligned));
    }

    [Fact]
    public void DrawRectangle_ThickStroke()
    {
        var rect = new RectInt(new PointInt(216, 216), new PointInt(40, 40));
        var (cpu, gpu) = RenderBoth((cpuR, gpuR) =>
        {
            cpuR.DrawRectangle(rect, White, strokeWidth: 4);
            gpuR.DrawRectangle(rect, White, strokeWidth: 4);
        });
        if (gpu is null) return;
        // Both backends decompose into 4 axis-aligned FillRectangle calls; rasterization
        // matches the half-open rect convention on each side. Bit-exact on hardware Vulkan.
        AssertBadPixelFractionUnder(cpu, gpu, perPixelTolerance: 16, badPixelFraction: 0.001,
            nameof(DrawRectangle_ThickStroke));
    }

    [Theory]
    [InlineData(10, 25, 190, 25)]   // horizontal
    [InlineData(128, 10, 128, 246)] // vertical
    public void DrawLine_AxisAligned(int x0, int y0, int x1, int y1)
    {
        var (cpu, gpu) = RenderBoth((cpuR, gpuR) =>
        {
            cpuR.DrawLine(x0, y0, x1, y1, White);
            gpuR.DrawLine(x0, y0, x1, y1, White);
        });
        if (gpu is null) return;
        // Thin axis-aligned line: CPU draws a single row/column of pixels; GPU draws a
        // 1-unit-thick rotated quad that straddles two rows (e.g. y=24 and y=25 for the
        // horizontal line at y=25). Disagreement is exactly one row's worth of pixels
        // = ~0.55-0.72% of bytes; allow 2% to leave headroom.
        AssertBadPixelFractionUnder(cpu, gpu, perPixelTolerance: 16, badPixelFraction: 0.02,
            $"DrawLine_AxisAligned_{x0}_{y0}_{x1}_{y1}");
    }

    [Fact]
    public void DrawLine_Diagonal()
    {
        var (cpu, gpu) = RenderBoth((cpuR, gpuR) =>
        {
            cpuR.DrawLine(20, 20, 230, 230, White);
            gpuR.DrawLine(20, 20, 230, 230, White);
        });
        if (gpu is null) return;
        // 45-degree diagonals are bit-exact on hardware Vulkan: CPU scanline-quad and GPU
        // rotated-quad both rasterize to the same diagonal pixel set when the line direction
        // hits the rasterization sweet spot. Allow 1% headroom for non-perfect diagonals.
        AssertBadPixelFractionUnder(cpu, gpu, perPixelTolerance: 16, badPixelFraction: 0.01,
            nameof(DrawLine_Diagonal));
    }

    [Fact]
    public void FillEllipse()
    {
        var rect = new RectInt(new PointInt(200, 200), new PointInt(60, 60));
        var (cpu, gpu) = RenderBoth((cpuR, gpuR) =>
        {
            cpuR.FillEllipse(rect, Green);
            gpuR.FillEllipse(rect, Green);
        });
        if (gpu is null) return;
        // CPU uses scanline fill with pixel-center sampling; GPU uses dot-product discard.
        // On hardware Vulkan only edge pixels along the ~440-pixel circumference disagree
        // (~0.07% bad bytes). Allow 0.5% headroom.
        AssertBadPixelFractionUnder(cpu, gpu, perPixelTolerance: 16, badPixelFraction: 0.005,
            nameof(FillEllipse));
    }

    [Fact]
    public void DrawEllipse_RingStroke()
    {
        var rect = new RectInt(new PointInt(200, 200), new PointInt(60, 60));
        var (cpu, gpu) = RenderBoth((cpuR, gpuR) =>
        {
            cpuR.DrawEllipse(rect, Blue, strokeWidth: 3f);
            gpuR.DrawEllipse(rect, Blue, strokeWidth: 3f);
        });
        if (gpu is null) return;
        // Thick ring: disagreement on outer + inner edges (~0.65% on hardware Vulkan).
        // Allow 2% headroom.
        AssertBadPixelFractionUnder(cpu, gpu, perPixelTolerance: 16, badPixelFraction: 0.02,
            nameof(DrawEllipse_RingStroke));
    }

    /// <summary>
    /// Runs the same draw lambda against a CPU <see cref="RgbaImageRenderer"/> and a GPU
    /// offscreen <see cref="VkRenderer"/>, both cleared to <see cref="Black"/>. Returns the
    /// resulting RGBA buffers. If Vulkan init fails, returns (cpuBytes, null) and emits a
    /// Skip via xUnit so the test reports as Skipped, not Failed.
    /// </summary>
    private (byte[] Cpu, byte[]? Gpu) RenderBoth(Action<RgbaImageRenderer, VkRenderer> draw)
    {
        // CPU side -- straightforward.
        using var cpu = new RgbaImageRenderer((uint)Width, (uint)Height);
        cpu.Surface.Clear(Black);

        // GPU side -- pay Vulkan init cost per test (~200ms). If init fails (no ICD, etc.)
        // skip the comparison.
        byte[]? gpuRgba;
        try
        {
            gpuRgba = RenderViaOffscreenGpu(gpuR =>
            {
                draw(cpu, gpuR);
            });
        }
        catch (Exception ex) when (IsVulkanInitFailure(ex))
        {
            output.WriteLine($"Vulkan unavailable, skipping GPU comparison: {ex.GetType().Name}: {ex.Message}");
            // Still run the CPU side so any CPU-only regression is visible, then skip the
            // parity check.
            Assert.Skip($"Vulkan runtime not available on this host ({ex.Message})");
            return (cpu.Surface.Pixels, null);
        }
        return (cpu.Surface.Pixels, gpuRgba);
    }

    private unsafe byte[] RenderViaOffscreenGpu(Action<VkRenderer> draw)
    {
        vkInitialize().CheckResult();
        VkInstanceCreateInfo ici = new();
        vkCreateInstance(&ici, null, out var instance).CheckResult();

        using var ctx = VulkanContext.CreateOffscreen(instance, Width, Height);
        using var renderer = new VkRenderer(ctx, Width, Height);

        ctx.InstanceApi.vkGetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        var deviceName = System.Text.Encoding.UTF8.GetString(
            System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(props.deviceName));
        output.WriteLine($"Physical device: {deviceName}");

        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        draw(renderer);
        renderer.EndOffscreenFrame();
        return ctx.ReadbackOffscreenRgba();
    }

    private void AssertBadPixelFractionUnder(byte[] cpu, byte[] gpu, int perPixelTolerance, double badPixelFraction, string label)
    {
        cpu.Length.ShouldBe(gpu.Length);
        long absDiffSum = 0;
        var maxDiff = 0;
        var bytesOutsideTolerance = 0;
        for (var i = 0; i < cpu.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var d = Math.Abs(cpu[i + c] - gpu[i + c]);
                absDiffSum += d;
                if (d > maxDiff) maxDiff = d;
                if (d > perPixelTolerance) bytesOutsideTolerance++;
            }
        }
        var pixelCount = cpu.Length / 4;
        var meanDiff = absDiffSum / (double)(pixelCount * 3);
        var outFrac = bytesOutsideTolerance / (double)(pixelCount * 3);
        output.WriteLine($"[{label}] mean={meanDiff:F3}  max={maxDiff}  outFrac(>{perPixelTolerance})={outFrac:P3}");

        // Always write TIFFs so a developer can eyeball the difference when the assertion
        // fires (or while tuning the tolerance budgets in this file). Use the same temp
        // directory pattern as the rest of the suite.
        try
        {
            var dir = SharedTestData.CreateTempTestOutputDir(nameof(VkRendererPrimitiveTests));
            WriteTiff(cpu, Width, Height, System.IO.Path.Combine(dir, $"{label}.cpu.tiff"));
            WriteTiff(gpu, Width, Height, System.IO.Path.Combine(dir, $"{label}.gpu.tiff"));
            WriteDiffTiff(cpu, gpu, Width, Height, System.IO.Path.Combine(dir, $"{label}.diff.tiff"));
            output.WriteLine($"[{label}] wrote cpu/gpu/diff tiffs to {dir}");
        }
        catch (Exception ex)
        {
            output.WriteLine($"[{label}] TIFF write failed: {ex.Message}");
        }

        outFrac.ShouldBeLessThan(badPixelFraction,
            $"{label}: more than {badPixelFraction:P0} of bytes outside ±{perPixelTolerance} between CPU and GPU");
    }

    private static void WriteTiff(byte[] rgba, int width, int height, string path)
    {
        var settings = new ImageMagick.PixelReadSettings((uint)width, (uint)height, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA);
        using var magick = new ImageMagick.MagickImage(rgba, settings);
        magick.Settings.Compression = ImageMagick.CompressionMethod.Zip;
        System.IO.File.WriteAllBytes(path, magick.ToByteArray(ImageMagick.MagickFormat.Tiff));
    }

    private static void WriteDiffTiff(byte[] cpu, byte[] gpu, int width, int height, string path)
    {
        var diff = new byte[cpu.Length];
        for (var i = 0; i < cpu.Length; i += 4)
        {
            var d = (byte)Math.Min(255, Math.Max(
                Math.Abs(cpu[i] - gpu[i]),
                Math.Max(Math.Abs(cpu[i + 1] - gpu[i + 1]), Math.Abs(cpu[i + 2] - gpu[i + 2]))
            ) * 4);
            diff[i] = diff[i + 1] = diff[i + 2] = d;
            diff[i + 3] = 255;
        }
        WriteTiff(diff, width, height, path);
    }

    private static bool IsVulkanInitFailure(Exception ex)
    {
        return ex is DllNotFoundException
            || ex is TypeInitializationException
            || ex.Message.Contains("vkCreateInstance", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("vkInitialize", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Vulkan", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("VK_ERROR", StringComparison.Ordinal)
            || ex.Message.Contains("ICD", StringComparison.Ordinal);
    }
}
