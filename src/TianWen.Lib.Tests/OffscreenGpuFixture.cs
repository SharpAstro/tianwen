using System;
using SdlVulkan.Renderer;
using TianWen.UI.Shared;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace TianWen.Lib.Tests;

/// <summary>
/// xUnit class fixture that owns a single offscreen Vulkan stack (VkInstance +
/// <see cref="VulkanContext"/> + <see cref="VkRenderer"/> + <see cref="VkFitsImagePipeline"/>)
/// shared across every test in <see cref="GpuStretchPipelineTests"/>. Created once, disposed
/// once. Mirrors docs/plans/gpu-stretch-tests.md Phase 1's "Class fixture so all theory cases share
/// one context" recommendation.
///
/// Why: without this, every [Theory] case (and every [Fact]) used to call vkInitialize +
/// vkCreateInstance + vkCreateDebugUtilsMessengerEXT and tear it all down again. Mesa lavapipe
/// + the Khronos validation layer + libvulkan loader accumulate enough TLS / process-global
/// state across repeated init/destroy that the runtime SIGSEGVs during process exit (xUnit
/// reports "Catastrophic failure: Test process crashed with exit code 139"). The accumulation
/// is the antipattern; this fixture removes it.
///
/// The framebuffer is sized to the largest expected test image (Vela_SNR_Panel = 1310x1291).
/// Smaller tests (Phase 1 synthetic SPCC field, 1280x1024) render into the top-left
/// sub-rectangle and the helper extracts the meaningful slice from the readback.
///
/// Channel textures inside <see cref="VkFitsImagePipeline"/> resize automatically per upload
/// (<see cref="VkFitsImagePipeline.UploadChannelTexture"/> calls DestroyChannelTexture +
/// CreateChannelTexture when dimensions change), so the shared pipeline handles arbitrary
/// per-test image dimensions transparently.
/// </summary>
public sealed unsafe class OffscreenGpuFixture : IDisposable
{
    public const int Width = 1310;
    public const int Height = 1291;

    public bool VulkanAvailable { get; }
    public string? UnavailableReason { get; }

    // Tests must guard on VulkanAvailable before dereferencing -- when Vulkan init failed all
    // three remain null and Assert.Skip is the only legitimate response.
    public VulkanContext? Ctx { get; }
    public VkRenderer? Renderer { get; }
    public VkFitsImagePipeline? Pipeline { get; }

    public OffscreenGpuFixture()
    {
        try
        {
            vkInitialize().CheckResult();
            VkInstanceCreateInfo ici = new();
            vkCreateInstance(&ici, null, out var instance).CheckResult();

            // VulkanContext.Dispose() destroys the instance at teardown, so the fixture
            // doesn't separately track it -- Ctx.Dispose() in the fixture's Dispose covers
            // both the device + instance lifecycle.
            Ctx = VulkanContext.CreateOffscreen(instance, Width, Height);
            Renderer = new VkRenderer(Ctx, Width, Height);
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
