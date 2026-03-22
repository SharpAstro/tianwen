using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.UI.Gui;

/// <summary>
/// Vulkan-pinned GUI event handler. All logic lives in <see cref="GuiEventHandlerBase"/>.
/// </summary>
public sealed class GuiEventHandlers(
    IServiceProvider sp,
    GuiAppState appState,
    PlannerState plannerState,
    VkGuiRenderer guiRenderer,
    CancellationTokenSource cts,
    IExternal external,
    BackgroundTaskTracker tracker)
    : GuiEventHandlerBase(sp, appState, plannerState, guiRenderer, cts, external, tracker)
{
}
