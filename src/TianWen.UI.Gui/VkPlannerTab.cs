using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned Planner tab. All logic lives in <see cref="PlannerTab{TSurface}"/>.
/// </summary>
public sealed class VkPlannerTab(VkRenderer renderer) : PlannerTab<VulkanContext>(renderer)
{
}
