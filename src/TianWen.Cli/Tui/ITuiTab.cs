using Console.Lib;
using DIR.Lib;

namespace TianWen.Cli.Tui;

/// <summary>
/// Interface for a TUI tab that can be hosted in the tabbed <see cref="TuiSubCommand"/>.
/// Each tab declares its arrangement as a layout tree and hosts its widgets at that tree's leaves.
/// </summary>
internal interface ITuiTab
{
    /// <summary>Whether the tab content needs to be redrawn.</summary>
    bool NeedsRedraw { get; set; }

    /// <summary>
    /// Binds the tab to a terminal and creates its widgets, reserving <paramref name="topRows"/> for the
    /// tab bar and <paramref name="bottomRows"/> for the status bar so the tab's own tree is arranged in
    /// what is left. Called on tab activation and terminal resize.
    /// </summary>
    void Attach(IVirtualTerminal terminal, int topRows = 1, int bottomRows = 1);

    /// <summary>Renders the tab content. Called each frame when <see cref="NeedsRedraw"/> is true.</summary>
    void Render();

    /// <summary>
    /// Handles an input event. Returns true if the TUI should quit.
    /// </summary>
    bool HandleInput(InputEvent evt);

    /// <summary>
    /// Routes a raw Console.Lib <see cref="MouseEvent"/> to the tab before it's mapped
    /// to a DIR.Lib <see cref="InputEvent"/>. Tabs with a <see cref="ScrollableList{T}"/>
    /// override this to forward the event to the list so drag / click-on-track works;
    /// the default is a no-op. Returns true when the event was consumed.
    /// </summary>
    bool HandleRawMouse(MouseEvent mouse) => false;
}
