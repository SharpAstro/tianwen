using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Equipment tab. All logic lives in <see cref="EquipmentTab{TSurface}"/>.
/// </summary>
public sealed class VkEquipmentTab(VkRenderer renderer) : EquipmentTab<VulkanContext>(renderer)
{
}
