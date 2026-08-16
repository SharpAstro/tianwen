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

    // Reused per frame: the tab set is fixed, so only the active index moves.
    private readonly List<TabItem<GuiTab>> _items = new(Tabs.Length);

    /// <summary>
    /// Cell metrics, and the two policies where a terminal genuinely differs from a pixel strip.
    /// <see cref="TabStripOverflow.Drop"/> because a clipped tab would leave a region that is hit but not
    /// visible; <see cref="TabLabelDecoration.Brackets"/> because a background colour is not a safe bet on
    /// somebody else's terminal palette. Everything else is shared.
    /// </summary>
    private static readonly TabStripOptions StripOptions = new()
    {
        Metrics = TabStripMetrics.Cells,
        Colors = new TabBarColors
        {
            BarBackground = BarBg,
            InactiveBackground = BarBg,
            ActiveBackground = ActiveTabBg,
            ActiveText = TabText,
            InactiveText = TabText,
        },
        Overflow = TabStripOverflow.Drop,
        Decoration = TabLabelDecoration.Brackets,
        CanCloseTabs = false,
        FillsAvailable = false,
    };

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

    /// <summary>
    /// The bar: the shared tab strip, then the status text on the right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The STRIP is <see cref="TabStripTree"/>'s -- the same description DIR.Lib's <c>TabBar</c> paints on
    /// a GPU surface, given cell metrics instead of pixel ones. What used to be here was a fourth copy of
    /// "a row of tabs, one active, click to switch", and the copies had already disagreed about enough
    /// (decoration, overflow, which edge marks active) that nothing but reading them side by side would
    /// have found it.
    /// </para>
    /// <para>
    /// The STATUS text stays here, and that is deliberate: it is a TianWen composition, not a tab-strip
    /// feature, so the strip is asked to fit what is left rather than being taught about a neighbour. It
    /// keeps its priority -- reserved up front, tabs fit into the remainder -- which is what
    /// <see cref="TabStripOverflow.Drop"/> then acts on.
    /// </para>
    /// </remarks>
    private Layout.Node Build(GuiTab active, string status, int width)
    {
        _items.Clear();
        foreach (var (label, tab) in Tabs)
        {
            _items.Add(new TabItem<GuiTab>(label, tab));
        }

        var activeIndex = _items.FindIndex(item => EqualityComparer<GuiTab>.Default.Equals(item.Value, active));

        // Leading space, as before, and the status reserved out of what the tabs may use.
        var available = width - 1 - status.Length;

        var strip = TabStripTree.Build(
            _items,
            activeIndex,
            pointerFlow: null,         // a terminal reports no hover position
            pointerCross: null,
            available,
            static label => label.Length,
            StripOptions,
            index => _clicked = _items[index].Value);

        return Layout.Builder.HStack(
                Layout.Builder.Spacer().WFixed(1f).HStar(),
                strip.Root.HStar(),
                Layout.Builder.Spacer().WStar().HStar(),
                Layout.Builder.Text(status, 1f, StatusText).WFixed(status.Length).HStar())
            .HStar()
            .Bg(BarBg);
    }
}
