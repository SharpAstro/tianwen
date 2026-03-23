using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Interface for the GUI chrome (sidebar, status bar) that the event handler needs.
/// Implemented by the concrete GUI renderer (e.g. VkGuiRenderer).
/// </summary>
public interface IGuiChrome : IPixelWidget
{
    /// <summary>The currently active tab widget, or null.</summary>
    IPixelWidget? ActiveTab { get; }

    /// <summary>Equipment tab state (for auto-discover on tab switch).</summary>
    EquipmentTabState EquipmentState { get; }

    /// <summary>Session tab state (for persistence).</summary>
    SessionTabState SessionState { get; }

    /// <summary>Chart layout rectangle from the planner tab (for slider dragging).</summary>
    RectF32 PlannerChartRect { get; }

    /// <summary>Scroll the planner target list so that the item at the given index is visible.</summary>
    void PlannerEnsureVisible(int index);

    /// <summary>Live session state (for session monitoring).</summary>
    LiveSessionState LiveSessionState { get; }

    /// <summary>The signal bus.</summary>
    SignalBus? Bus { get; }
}
