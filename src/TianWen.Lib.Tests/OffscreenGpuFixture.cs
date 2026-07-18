using System;
using SdlVulkan.Renderer;
using TianWen.UI.Shared;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.Lib.Tests;

/// <summary>
/// Base xUnit fixture that owns a single offscreen Vulkan stack (VkInstance +
/// <see cref="VulkanContext"/> + <see cref="VkRenderer"/> + <see cref="VkFitsImagePipeline"/>)
/// at a fixed size, created once and disposed once. Concrete size-specific fixtures derive from
/// it (see <see cref="OffscreenGpuFixture"/>, <see cref="VkPrimitiveGpuFixture"/>,
/// <see cref="VkHistogramGpuFixture"/>); each is wired as an <c>IClassFixture&lt;T&gt;</c> so a
/// GPU test class creates its Vulkan stack ONCE, not per test method.
///
/// Why this must be a fixture, not per-test setup: xUnit constructs a fresh test-class instance
/// per test method, so any Vulkan init in a test method's body (or the class ctor) runs on EVERY
/// method. Repeated vkInitialize + vkCreateInstance + vkCreateDebugUtilsMessengerEXT + teardown
/// makes Mesa lavapipe + the Khronos validation layer + the libvulkan loader accumulate enough
/// TLS / process-global state that the runtime SIGSEGVs during process exit -- xUnit reports
/// "Catastrophic failure: Test process crashed with exit code 139". Hoisting the stack into a
/// class fixture removes the churn (one init/destroy per class instead of per method).
///
/// Channel/histogram textures inside <see cref="VkFitsImagePipeline"/> resize automatically per
/// upload, so the shared pipeline handles arbitrary per-test image dimensions transparently.
/// </summary>
public abstract unsafe class OffscreenGpuFixtureBase : IDisposable
{
    public bool VulkanAvailable { get; }
    public string? UnavailableReason { get; }

    // Tests must guard on VulkanAvailable before dereferencing -- when Vulkan init failed all
    // three remain null and Assert.Skip is the only legitimate response.
    public VulkanContext? Ctx { get; }
    public VkRenderer? Renderer { get; }
    public VkFitsImagePipeline? Pipeline { get; }

    protected OffscreenGpuFixtureBase(int width, int height)
    {
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out var instance).CheckResult();

            // VulkanContext.Dispose() destroys the instance at teardown, so the fixture
            // doesn't separately track it -- Ctx.Dispose() in the fixture's Dispose covers
            // both the device + instance lifecycle.
            Ctx = VulkanContext.CreateOffscreen(instance, (uint)width, (uint)height);
            Renderer = new VkRenderer(Ctx, (uint)width, (uint)height);
            Pipeline = new VkFitsImagePipeline(Ctx);
            VulkanAvailable = true;
        }
        catch (Exception ex)
        {
            VulkanAvailable = false;
            UnavailableReason = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (!VulkanAvailable) return;
        Pipeline?.Dispose();
        Renderer?.Dispose();
        Ctx?.Dispose();
    }
}

/// <summary>
/// Offscreen Vulkan stack sized to the largest expected stacking/stretch test image
/// (Vela_SNR_Panel = 1310x1291); shared across every test in <see cref="GpuStretchPipelineTests"/>.
/// Smaller tests render into the top-left sub-rectangle and the helper extracts the meaningful
/// slice from the readback. The <see cref="Width"/> / <see cref="Height"/> consts stay so callers
/// keep referencing <c>OffscreenGpuFixture.Width</c> unchanged.
/// </summary>
public sealed class OffscreenGpuFixture : OffscreenGpuFixtureBase
{
    public const int Width = 1310;
    public const int Height = 1291;

    public OffscreenGpuFixture() : base(Width, Height) { }
}

/// <summary>Offscreen Vulkan stack sized for <see cref="VkRendererPrimitiveTests"/> (256x256).</summary>
public sealed class VkPrimitiveGpuFixture : OffscreenGpuFixtureBase
{
    public VkPrimitiveGpuFixture() : base(256, 256) { }
}

/// <summary>Offscreen Vulkan stack sized for <see cref="VkHistogramPipelineTests"/> (512x64).</summary>
public sealed class VkHistogramGpuFixture : OffscreenGpuFixtureBase
{
    public VkHistogramGpuFixture() : base(512, 64) { }
}
