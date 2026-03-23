using DIR.Lib;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions;

public enum GuiTab
{
    Planner,
    Viewer,
    Session,
    Equipment,
    LiveSession
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

    /// <summary>
    /// True when the user has requested exit but background tasks (session Finalise) are still running.
    /// The render loop continues during this phase, showing shutdown progress.
    /// </summary>
    public bool ShuttingDown { get; set; }

    /// <summary>
    /// True when the user pressed X/Escape to quit while a session was running.
    /// After abort is confirmed, shutdown proceeds automatically.
    /// </summary>
    public bool QuitRequested { get; set; }

    /// <summary>
    /// Set by CheckNeedsRedraw when ShuttingDown and no pending tasks remain.
    /// The loop checks this to call Stop() (since CheckNeedsRedraw can't reference loop).
    /// </summary>
    public bool ShutdownComplete { get; set; }
}
