using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Live Session tab. All logic lives in <see cref="LiveSessionTab{TSurface}"/>.
/// </summary>
public sealed class VkLiveSessionTab(VkRenderer renderer) : LiveSessionTab<VulkanContext>(renderer)
{
}
