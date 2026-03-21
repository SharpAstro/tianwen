using DIR.Lib;
using SdlVulkan.Renderer;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Base class for GPU-rendered tabs. Thin wrapper over <see cref="PixelWidgetBase{TSurface}"/>
/// that pins the surface type to <see cref="VulkanContext"/>.
/// </summary>
public abstract class VkTabBase(VkRenderer renderer) : PixelWidgetBase<VulkanContext>(renderer)
{
}
