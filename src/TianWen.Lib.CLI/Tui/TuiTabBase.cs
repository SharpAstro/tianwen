using Console.Lib;
using DIR.Lib;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// Base class for TUI tabs. Manages the <see cref="Panel"/> lifecycle,
/// <see cref="NeedsRedraw"/> flag, and <see cref="ClickableRegionTracker"/>.
/// Subclasses override <see cref="CreateWidgets"/> and <see cref="RenderContent"/>.
/// </summary>
internal abstract class TuiTabBase : ITuiTab
{
    protected readonly ClickableRegionTracker Tracker = new();

    protected Panel? Panel { get; private set; }

    public bool NeedsRedraw { get; set; } = true;

    public void BuildPanel(IVirtualTerminal terminal, int topRows = 1, int bottomRows = 0)
    {
        Panel = new Panel(terminal);
        Panel.Dock(DockStyle.Top, topRows);
        Panel.Dock(DockStyle.Bottom, bottomRows);
        CreateWidgets(Panel);
        NeedsRedraw = true;
    }

    public void Render()
    {
        if (Panel is null || !IsReady)
        {
            return;
        }

        NeedsRedraw = false;
        Tracker.BeginFrame();
        RenderContent();
        Panel.RenderAll();
        RegisterClickableRegions();
    }

    public abstract bool HandleInput(InputEvent evt);

    /// <summary>
    /// Creates tab-specific widgets from the panel. Called from <see cref="BuildPanel"/>.
    /// The panel already has top/bottom rows reserved.
    /// </summary>
    protected abstract void CreateWidgets(Panel panel);

    /// <summary>Whether all required widgets have been created.</summary>
    protected abstract bool IsReady { get; }

    /// <summary>
    /// Fills widget data for the current frame. Called from <see cref="Render"/>
    /// after <see cref="NeedsRedraw"/> is cleared and before <see cref="Panel.RenderAll"/>.
    /// </summary>
    protected abstract void RenderContent();

    /// <summary>
    /// Registers clickable regions after <see cref="Panel.RenderAll"/> using widget viewport geometry.
    /// Override to register regions; default is no-op.
    /// </summary>
    protected virtual void RegisterClickableRegions() { }
}
