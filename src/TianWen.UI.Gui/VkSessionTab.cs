using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Session tab. All logic lives in <see cref="SessionTab{TSurface}"/>.
/// </summary>
public sealed class VkSessionTab(VkRenderer renderer) : SessionTab<VulkanContext>(renderer)
{
}
