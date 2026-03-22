using Console.Lib;
using DIR.Lib;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// Interface for a TUI tab that can be hosted in the tabbed <see cref="TuiSubCommand"/>.
/// Each tab owns its own Console.Lib widget layout within a content viewport.
/// </summary>
internal interface ITuiTab
{
    /// <summary>Whether the tab content needs to be redrawn.</summary>
    bool NeedsRedraw { get; set; }

    /// <summary>
    /// Builds the tab's Panel layout. The tab creates its own Panel from the terminal,
    /// reserving <paramref name="topRows"/> for the tab bar and <paramref name="bottomRows"/> for the status bar.
    /// Called on tab activation and terminal resize.
    /// </summary>
    void BuildPanel(IVirtualTerminal terminal, int topRows = 1, int bottomRows = 1);

    /// <summary>Renders the tab content. Called each frame when <see cref="NeedsRedraw"/> is true.</summary>
    void Render();

    /// <summary>
    /// Handles an input event. Returns true if the TUI should quit.
    /// </summary>
    bool HandleInput(InputEvent evt);
}
