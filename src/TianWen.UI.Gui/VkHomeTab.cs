using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Home tab. All logic lives in <see cref="HomeTab{TSurface}"/>.
/// </summary>
public sealed class VkHomeTab(VkRenderer renderer) : HomeTab<VulkanContext>(renderer)
{
}
