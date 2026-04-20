using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Notifications tab. All logic lives in <see cref="NotificationsTab{TSurface}"/>.
/// </summary>
public sealed class VkNotificationsTab(VkRenderer renderer) : NotificationsTab<VulkanContext>(renderer)
{
}
