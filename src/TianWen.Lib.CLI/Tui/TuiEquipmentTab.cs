using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI equipment/profile tab. Shows profile summary as markdown
/// with keyboard commands for device discovery and management.
/// </summary>
internal sealed class TuiEquipmentTab(
    IConsoleHost consoleHost,
    GuiAppState appState,
    EquipmentContent equipmentContent) : ITuiTab
{
    private Panel? _panel;
    private MarkdownWidget? _profileWidget;
    private TextBar? _statusBar;

    public bool NeedsRedraw { get; set; } = true;

    public void BuildPanel(IVirtualTerminal terminal, int topRows = 1, int bottomRows = 1)
    {
        _panel = new Panel(terminal);
        _panel.Dock(DockStyle.Top, topRows);
        _panel.Dock(DockStyle.Bottom, bottomRows);
        var bottomVp = _panel.Dock(DockStyle.Bottom, 1);
        var fillVp = _panel.Fill();

        _statusBar = new TextBar(bottomVp);
        _profileWidget = new MarkdownWidget(fillVp);

        _panel.Add(_statusBar).Add(_profileWidget);
        NeedsRedraw = true;
    }

    public void Render()
    {
        if (_panel is null || _profileWidget is null || _statusBar is null)
        {
            return;
        }

        NeedsRedraw = false;

        if (appState.ActiveProfile is { } profile)
        {
            var md = equipmentContent.FormatProfileMarkdown(profile);
            _profileWidget.Markdown(md);
        }
        else
        {
            _profileWidget.Markdown("## No profile selected");
        }

        _statusBar.Text(" D:discover  R:refresh  Q:quit");
        _statusBar.RightText(appState.StatusMessage ?? "");

        _panel.RenderAll();
    }

    public bool HandleInput(InputEvent evt)
    {
        if (evt is not InputEvent.KeyDown(var key, _))
        {
            return false;
        }

        switch (key)
        {
            case InputKey.D:
                // Device discovery runs synchronously for now — signal bus would handle async
                appState.StatusMessage = "Discovering...";
                NeedsRedraw = true;
                return false;

            case InputKey.R:
                NeedsRedraw = true;
                return false;
        }

        return false;
    }
}
