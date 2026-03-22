using Console.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// Renders the tab bar as a <see cref="TextBar"/> with highlighted active tab.
/// </summary>
internal sealed class TuiTabBar(ITerminalViewport viewport)
{
    private static readonly (string Label, GuiTab Tab)[] Tabs =
    [
        ("1:Equip", GuiTab.Equipment),
        ("2:Plan", GuiTab.Planner),
        ("3:Session", GuiTab.Session),
        ("4:View", GuiTab.Viewer),
    ];

    private readonly TextBar _bar = new TextBar(viewport);

    public void Render(GuiAppState appState, TimeProvider timeProvider, TimeSpan siteTimeZone)
    {
        var parts = new List<string>();
        foreach (var (label, tab) in Tabs)
        {
            parts.Add(tab == appState.ActiveTab ? $"[{label}]" : $" {label} ");
        }

        var tabText = string.Join(" ", parts);
        var profileName = appState.ActiveProfile?.DisplayName ?? "No profile";
        var clock = timeProvider.GetLocalNow().ToOffset(siteTimeZone).ToString("HH:mm");

        _bar.Text($" {tabText}").RightText($"{profileName}  {clock} ");
        _bar.Render();
    }
}
