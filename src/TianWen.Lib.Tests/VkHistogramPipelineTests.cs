using DIR.Lib;
using SdlVulkan.Renderer;
using Shouldly;
using System;
using TianWen.UI.Shared;
using Vortice.Vulkan;
using Xunit;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.Lib.Tests;

/// <summary>
/// Followup B from docs/plans/gpu-stretch-tests.md: comp test for
/// <see cref="VkFitsImagePipeline.RecordHistogramDraw"/>. The histogram pipeline renders
/// R/G/B histograms as overlapping coloured bars; the fragment shader samples the per-bin
/// values from R32_SFLOAT textures and outputs <c>vec4(color, alpha)</c> which is then
/// alpha-blended onto the framebuffer.
///
/// This test uploads a known synthetic histogram (single spike per channel at distinct
/// x positions), renders via the offscreen pipeline, and asserts the column structure:
/// the spike column contains the channel-coloured bar; columns without spikes stay at the
/// clear color.
/// </summary>
[Collection("Imaging")]
public sealed class VkHistogramPipelineTests(ITestOutputHelper output)
{
    // Match the pipeline's HistogramBins constant so each pixel column corresponds to one
    // bin (no linear-interpolation noise from sub-texel sampling).
    private const int Width = 512;
    private const int Height = 64;
    // Mirrors the private VkFitsImagePipeline.HistogramBins constant.
    private const int HistogramBins = 512;
    private static readonly RGBAColor32 Black = new(0, 0, 0, 255);

    [Fact]
    public void Histogram_SpikeAt_R100_G250_B400_ProducesChannelBarsAtThoseColumns()
    {
        // Per-channel histograms: one spike at a distinct bin per channel. Different
        // heights so we can also assert each bar's vertical extent on the framebuffer.
        var hist0 = new float[HistogramBins];
        var hist1 = new float[HistogramBins];
        var hist2 = new float[HistogramBins];
        hist0[100] = 1.0f;  // R: full-height bar
        hist1[250] = 0.5f;  // G: half-height bar
        hist2[400] = 0.25f; // B: quarter-height bar

        byte[] gpu;
        try
        {
            gpu = RenderHistogramGpu(hist0, hist1, hist2);
        }
        catch (Exception ex) when (IsVulkanInitFailure(ex))
        {
            output.WriteLine($"Vulkan unavailable, skipping: {ex.GetType().Name}: {ex.Message}");
            Assert.Skip($"Vulkan runtime not available ({ex.Message})");
            return;
        }

        // Spike columns: the column containing the spike should have a colored bar of the
        // right height (h * H rows tall, anchored at the bottom of the framebuffer because
        // the shader flips Y so 1 = top -> bar appears at the bottom).
        AssertColumnHasBar(gpu, x: 100, expectedHeight: 1.00f, channel: 0, label: "R spike col");
        AssertColumnHasBar(gpu, x: 250, expectedHeight: 0.50f, channel: 1, label: "G spike col");
        AssertColumnHasBar(gpu, x: 400, expectedHeight: 0.25f, channel: 2, label: "B spike col");

        // Empty columns: pick a few bin indexes far from any spike and assert the framebuffer
        // is the clear color (black with alpha 255) -- no spurious bars.
        AssertColumnIsClear(gpu, x: 0, label: "col 0");
        AssertColumnIsClear(gpu, x: 175, label: "col 175");
        AssertColumnIsClear(gpu, x: 511, label: "col 511");
    }

    [Fact]
    public void Histogram_EmptyChannels_ProducesBlankFramebuffer()
    {
        // All zeros -> shader sees scaled=0 for every column -> no fragment is coloured ->
        // framebuffer stays at the clear color everywhere.
        var empty = new float[HistogramBins];
        byte[] gpu;
        try
        {
            gpu = RenderHistogramGpu(empty, empty, empty);
        }
        catch (Exception ex) when (IsVulkanInitFailure(ex))
        {
            Assert.Skip($"Vulkan runtime not available ({ex.Message})");
            return;
        }

        // Every pixel should be the clear color (0, 0, 0, 255).
        var firstDiff = -1;
        for (var i = 0; i < gpu.Length; i += 4)
        {
            if (gpu[i] != 0 || gpu[i + 1] != 0 || gpu[i + 2] != 0 || gpu[i + 3] != 255)
            {
                firstDiff = i;
                break;
            }
        }
        if (firstDiff >= 0)
        {
            var px = firstDiff / 4;
            output.WriteLine($"Empty histogram leaked into pixel ({px % Width}, {px / Width}): ({gpu[firstDiff]}, {gpu[firstDiff + 1]}, {gpu[firstDiff + 2]}, {gpu[firstDiff + 3]})");
        }
        firstDiff.ShouldBe(-1, "empty histogram should leave the framebuffer at the clear color");
    }

    private unsafe byte[] RenderHistogramGpu(float[] hist0, float[] hist1, float[] hist2)
    {
        vkInitialize().CheckResult();
        VkInstanceCreateInfo ici = new();
        vkCreateInstance(&ici, null, out var instance).CheckResult();

        using var ctx = VulkanContext.CreateOffscreen(instance, Width, Height);
        using var renderer = new VkRenderer(ctx, Width, Height);
        using var pipeline = new VkFitsImagePipeline(ctx);

        ctx.InstanceApi.vkGetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        var deviceName = System.Text.Encoding.UTF8.GetString(
            System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(props.deviceName));
        output.WriteLine($"Physical device: {deviceName}");

        pipeline.UploadHistogramTexture(hist0, 0);
        pipeline.UploadHistogramTexture(hist1, 1);
        pipeline.UploadHistogramTexture(hist2, 2);

        renderer.BeginOffscreenFrame(Black).ShouldBeTrue();
        var cmd = renderer.CurrentCommandBuffer;
        // logPeak/linearPeak both = 1 so a bin value of 1.0 maps to a full-height bar.
        pipeline.UpdateHistogramUBO(cmd, channelCount: 3, logPeak: 1.0f, linearPeak: 1.0f, logScale: false);
        pipeline.RecordHistogramDraw(cmd, ctx, 0, 0, Width, Height, Width, Height);
        renderer.EndOffscreenFrame();
        return ctx.ReadbackOffscreenRgba();
    }

    /// <summary>
    /// Asserts that pixel column <paramref name="x"/> contains a colored bar of approximately
    /// <paramref name="expectedHeight"/> in the channel byte indexed by <paramref name="channel"/>
    /// (0 = R, 1 = G, 2 = B). The shader flips Y so bars grow from the bottom of the framebuffer
    /// upward: a bar of height h occupies framebuffer rows py in [H - round(H * h), H - 1].
    /// </summary>
    private void AssertColumnHasBar(byte[] rgba, int x, float expectedHeight, int channel, string label)
    {
        var barRowCount = (int)MathF.Round(Height * expectedHeight);
        if (barRowCount <= 0)
            throw new InvalidOperationException($"{label}: expectedHeight={expectedHeight} maps to zero rows");
        var pyTopOfBar = Height - barRowCount;
        var pyBottomOfBar = Height - 1;
        var pyMidOfBar = (pyTopOfBar + pyBottomOfBar) / 2;

        // Bar interior: channel byte must be nonzero at the top, middle, and bottom of the bar.
        Span<int> samplePys = stackalloc int[] { pyTopOfBar, pyMidOfBar, pyBottomOfBar };
        for (var k = 0; k < samplePys.Length; k++)
        {
            var py = samplePys[k];
            var i = (py * Width + x) * 4;
            rgba[i + channel].ShouldBeGreaterThan((byte)0,
                $"{label}: py={py} should be colored on channel {channel}");
        }

        // Just above the top of the bar, the channel byte should be 0 (no spill-over). Skip
        // when expectedHeight == 1 (bar fills the whole framebuffer).
        if (pyTopOfBar > 0)
        {
            var aboveI = ((pyTopOfBar - 1) * Width + x) * 4;
            rgba[aboveI + channel].ShouldBe((byte)0,
                $"{label}: py={pyTopOfBar - 1} (one above bar) should not be colored on channel {channel}");
        }
    }

    private void AssertColumnIsClear(byte[] rgba, int x, string label)
    {
        // Sample the top, middle, and bottom of the column. Without a spike, every row should
        // be the clear color (R=G=B=0, A=255).
        Span<int> samplePys = stackalloc int[] { 0, Height / 2, Height - 1 };
        for (var k = 0; k < samplePys.Length; k++)
        {
            var py = samplePys[k];
            var i = (py * Width + x) * 4;
            rgba[i + 0].ShouldBe((byte)0, $"{label} py={py}: R should be clear");
            rgba[i + 1].ShouldBe((byte)0, $"{label} py={py}: G should be clear");
            rgba[i + 2].ShouldBe((byte)0, $"{label} py={py}: B should be clear");
            rgba[i + 3].ShouldBe((byte)255, $"{label} py={py}: A should stay opaque");
        }
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
