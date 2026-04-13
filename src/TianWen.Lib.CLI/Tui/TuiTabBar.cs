using Console.Lib;
using TianWen.Lib.Devices;
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
        ("4:Live", GuiTab.LiveSession),
        ("5:Guider", GuiTab.Guider),
    ];

    private readonly TextBar _bar = new TextBar(viewport);

    public void Render(GuiAppState appState, ITimeProvider timeProvider, TimeSpan siteTimeZone)
    {
        var parts = new List<string>();
        foreach (var (label, tab) in Tabs)
        {
            parts.Add(tab == appState.ActiveTab ? $"[{label}]" : $" {label} ");
        }

        var tabText = string.Join(" ", parts);
        var profileName = appState.ActiveProfile?.DisplayName ?? "No profile";
        var clock = timeProvider.System.GetLocalNow().ToOffset(siteTimeZone).ToString("HH:mm:ss");

        _bar.Text($" {tabText}").RightText($"{profileName}  {clock} ");
        _bar.Render();
    }

    /// <summary>
    /// Returns the <see cref="GuiTab"/> at the given column, or null if outside any tab label.
    /// Column ranges are computed from the tab label layout to stay in sync with <see cref="Render"/>.
    /// </summary>
    public static GuiTab? HitTestTab(int column)
    {
        // Layout: " [1:Equip]  2:Plan   3:Session   4:View  ..."
        // Each tab is rendered as "[label]" (active, len+2) or " label " (inactive, len+2),
        // separated by " ". Leading " " offset = 1.
        var col = 1; // leading space
        foreach (var (label, tab) in Tabs)
        {
            var width = label.Length + 2; // brackets or spaces around label
            if (column >= col && column < col + width)
            {
                return tab;
            }
            col += width + 1; // +1 for separator space
        }
        return null;
    }
}
