using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

/// <summary>
/// The chrome tab bar (terminal row 0), built as a <see cref="Layout.Node"/> tree and hit-tested through
/// <see cref="CellLayout.HitTest"/> -- so a tab's click region IS the rect its label was drawn into.
/// <para>
/// <b>Why it is a tree.</b> It used to be a pre-joined string in a <see cref="TextBar"/> plus a static
/// <c>HitTestTab</c> that re-derived the column ranges from the same label array (<c>label.Length + 2</c>,
/// <c>+1</c> per separator, leading offset 1). The two agreed only as long as nobody changed the separator
/// or the active-tab decoration, and one of them did not know about the other's truncation:
/// <see cref="TextBar"/> gives its right-hand text priority and ellipsizes the LEFT text, so on a narrow
/// terminal the bar stopped drawing the later tabs while the hit test kept reporting them. Clicking the
/// profile name or the clock switched to Notifications. This is the same defect shape as the two the tab
/// migration fixed -- a hit region computed from something other than what was drawn.
/// </para>
/// <para>
/// <b>A narrow bar drops tabs rather than truncating them.</b> Deciding that here, in the tree, is the
/// point: a dropped tab is absent from both the paint and the hit test by construction, whereas a
/// truncated string leaves a region that is hit but not visible.
/// </para>
/// </summary>
internal sealed class TuiTabBar(ITerminalViewport viewport)
{
    // Label carries the Ctrl+letter mnemonic; F-keys (F1..F6) also switch tabs.
    private static readonly (string Label, GuiTab Tab)[] Tabs =
    [
        ("^H Home", GuiTab.Home),
        ("^E Equip", GuiTab.Equipment),
        ("^P Plan", GuiTab.Planner),
        ("^S Session", GuiTab.Session),
        ("^L Live", GuiTab.LiveSession),
        ("^G Guider", GuiTab.Guider),
        ("^N Notif", GuiTab.Notifications),
    ];

    private static readonly CellMeasureContext MeasureContext = new CellMeasureContext();

    private static readonly RGBAColor32 BarBg       = new RGBAColor32(0x3a, 0x3a, 0x3a, 0xff);
    private static readonly RGBAColor32 ActiveTabBg = new RGBAColor32(0x20, 0x30, 0x50, 0xff);
    private static readonly RGBAColor32 TabText     = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
    private static readonly RGBAColor32 StatusText  = new RGBAColor32(0xdd, 0xdd, 0xdd, 0xff);

    private ImmutableArray<Layout.ArrangedNode<int>> _arranged;
    private GuiTab? _clicked;

    public void Render(GuiAppState appState, ITimeProvider timeProvider, TimeSpan siteTimeZone)
    {
        var width = viewport.Size.Width;
        if (width <= 0)
        {
            return;
        }

        var profileName = appState.ActiveProfile?.DisplayName ?? "No profile";
        var clock = timeProvider.GetUtcNow().ToOffset(siteTimeZone).ToString("HH:mm:ss");

        _arranged = Arrange(appState.ActiveTab, $"{profileName}  {clock} ", width);
        CellLayout.Paint(viewport, _arranged);
    }

    /// <summary>
    /// The tab at <paramref name="column"/>, <paramref name="row"/> in terminal cells, or null when the
    /// click was not on a tab.
    /// <para>
    /// The row is part of the test rather than assumed by the caller: the bar's own arranged rect is one row
    /// tall, so a click below it misses without anyone hardcoding "the tab bar is row 0".
    /// </para>
    /// </summary>
    public GuiTab? HitTest(int column, int row)
    {
        if (_arranged.IsDefaultOrEmpty)
        {
            return null;
        }

        _clicked = null;
        CellLayout.HitTest(_arranged, column, row);
        var clicked = _clicked;
        _clicked = null;
        return clicked;
    }

    /// <summary>
    /// Arranges the bar for a given width. Internal so a test can assert the drawn geometry and the hit
    /// regions are the same rects, with no terminal involved.
    /// </summary>
    internal ImmutableArray<Layout.ArrangedNode<int>> Arrange(GuiTab active, string status, int width) =>
        Layout.Engine.Arrange(Build(active, status, width), new Rect<int>(0, 0, width, 1), MeasureContext);

    private Layout.Node Build(GuiTab active, string status, int width)
    {
        var children = ImmutableArray.CreateBuilder<Layout.Node>(Tabs.Length * 2 + 3);

        // Leading space, as before.
        children.Add(Layout.Builder.Spacer().WFixed(1f).HStar());

        // The status text keeps its priority, so reserve it up front and fit tabs into what is left. A tab
        // that does not fit is left out of the tree entirely -- not drawn, and therefore not clickable.
        var used = 1 + status.Length;
        var first = true;
        foreach (var (label, tab) in Tabs)
        {
            var text = tab == active ? $"[{label}]" : $" {label} ";
            var cost = text.Length + (first ? 0 : 1);
            if (used + cost > width)
            {
                break;
            }

            if (!first)
            {
                children.Add(Layout.Builder.Spacer().WFixed(1f).HStar());
            }

            var captured = tab;
            var node = Layout.Builder.Text(text, 1f, TabText).WFixed(text.Length).HStar();
            if (tab == active)
            {
                node = node.Bg(ActiveTabBg);
            }

            children.Add(node.Clickable(
                new HitResult.ButtonHit($"Tab:{captured}"), _ => _clicked = captured));

            used += cost;
            first = false;
        }

        children.Add(Layout.Builder.Spacer().WStar().HStar());
        children.Add(Layout.Builder.Text(status, 1f, StatusText).WFixed(status.Length).HStar());

        return Layout.Builder.HStack([.. children]).HStar().Bg(BarBg);
    }
}
