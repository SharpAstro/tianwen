using DIR.Lib;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

public enum GuiTab
{
    Planner,
    Viewer,
    Session,
    Equipment
}

public class GuiAppState
{
    public GuiTab ActiveTab { get; set; } = GuiTab.Planner;
    public Profile? ActiveProfile { get; set; }
    public bool NeedsRedraw { get; set; } = true;
    public (float X, float Y) MouseScreenPosition { get; set; }
    public InputModifier LastClickModifiers { get; set; }
    public string? StatusMessage { get; set; }

    /// <summary>The currently focused text input across all tabs. Single source of truth.</summary>
    public TextInputState? ActiveTextInput { get; set; }
}
