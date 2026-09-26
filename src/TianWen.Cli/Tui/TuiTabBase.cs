using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;

namespace TianWen.Cli.Tui;

/// <summary>
/// Base class for TUI tabs. A tab declares its arrangement as a <see cref="Layout.Node"/> tree, the same
/// surface-agnostic tree the GUI paints, and this class arranges it in cells and paints it via
/// <see cref="CellLayout"/>.
/// <para>
/// <b>The tree owns placement; widgets own behaviour.</b> A <see cref="ScrollableList{T}"/> (scroll state
/// and thumb), a <see cref="Canvas"/> (Sixel dirty regions) and a <see cref="MarkdownWidget"/> (its own
/// wrapping) do things a layout node cannot model, so they stay widgets -- but they no longer place
/// themselves. Each is registered against a key with <see cref="Host"/>, appears in the tree as a
/// <c>Layout.Builder.Fill(key: ...)</c> leaf, and has its viewport re-pointed at that leaf's arranged rect
/// before <see cref="PaintHost"/> draws it. Everything else (bars, labels, backgrounds) is just nodes.
/// </para>
/// <para>
/// The tree is rebuilt every frame, so a tab can branch on terminal size, capability or state in plain C#
/// -- which is how the Sixel and text-fallback arrangements become two expressions rather than two
/// widget-construction paths.
/// </para>
/// </summary>
internal abstract class TuiTabBase : ITuiTab
{
    protected readonly ClickableRegionTracker Tracker = new();

    /// <summary>Stateless (text width is the character count), so one instance serves every tab.</summary>
    /// <summary>
    /// How this tab's design units map to cells. Cell-authored by default -- every hand-written TUI tree
    /// counts in cells, so <c>RowH(1)</c> is one row. A tab that renders a tree SHARED with a pixel surface
    /// overrides this with <see cref="CellMeasureContext.PixelAuthored"/>, because those trees count in
    /// pixel-ish units and the two conventions differ by a different factor on each axis.
    /// </summary>
    protected virtual CellMeasureContext MeasureContext => CellMeasureContext.CellAuthored;

    private readonly Dictionary<string, HostedRegion> _hosts = [];

    /// <summary>The cursor of every list created through <see cref="HostList{T}"/>, by host key.</summary>
    private readonly Dictionary<string, ListCursor> _listCursors = [];

    /// <summary>
    /// Cursor rows captured by <see cref="Attach"/> and not yet put back: the lists it rebuilt are empty
    /// until the first <see cref="RenderContent"/> fills them, so the restore has to wait for that.
    /// </summary>
    private readonly Dictionary<string, int> _pendingCursors = [];

    private IVirtualTerminal? _terminal;
    private int _topRows;
    private int _bottomRows;

    public bool NeedsRedraw { get; set; } = true;

    /// <summary>
    /// The tree as last arranged, for hit-testing via <see cref="CellLayout.HitTest"/> and for tests that
    /// assert placement without a terminal.
    /// </summary>
    protected ImmutableArray<Layout.ArrangedNode<int>> Arranged { get; private set; }

    /// <summary>
    /// The cell rect this tab was last arranged into (chrome rows already removed). Set before
    /// <see cref="BuildLayout"/> runs, so a responsive tree can branch on it.
    /// </summary>
    protected Rect<int> Content { get; private set; }

    /// <summary>
    /// Resolves a MOUSE-PIXEL point against the arranged tree and dispatches the leaf's click, returning
    /// whether anything was hit.
    /// <para>
    /// The pixel-to-cell conversion lives here, once, because a tab doing it itself is the exact shape of
    /// defect the tab-bar fix removed: arithmetic parallel to what was drawn, kept in step by hand.
    /// </para>
    /// </summary>
    protected bool DispatchLayoutHit(float pixelX, float pixelY)
    {
        if (_terminal is not { } terminal || Arranged.IsDefaultOrEmpty)
        {
            return false;
        }

        var cell = terminal.CellSize;
        if (cell.Width <= 0 || cell.Height <= 0)
        {
            return false;
        }

        return CellLayout.HitTest(Arranged, (int)pixelX / cell.Width, (int)pixelY / cell.Height) is not null;
    }

    public void Attach(IVirtualTerminal terminal, int topRows = 1, int bottomRows = 1)
    {
        _terminal = terminal;
        _topRows = topRows;
        _bottomRows = bottomRows;

        // CreateWidgets builds every list anew, cursor at row 0, so a tab switch (and a resize) would drop
        // the user's place. Remember each cursor here and put it back once the rebuilt list has rows. A
        // list that has no rows yet (attached again before it was ever filled) keeps what is pending
        // rather than recording its own meaningless row 0 over it.
        foreach (var (key, cursor) in _listCursors)
        {
            if (cursor.Count() > 0)
            {
                _pendingCursors[key] = cursor.Index();
            }
        }

        _hosts.Clear();
        _listCursors.Clear();
        CreateWidgets();
        NeedsRedraw = true;
    }

    /// <summary>
    /// Puts back the cursors <see cref="Attach"/> remembered, clamped to the rebuilt list's rows. Runs
    /// after <see cref="RenderContent"/>, which is what fills the lists, and before anything paints, so
    /// the first frame after a switch already shows the row the user left on. An empty list is left as
    /// it is, with nothing selected, and the remembered row is dropped either way: it answers "where was
    /// I when I left", not "where should I be whenever this list next has rows".
    /// </summary>
    private void RestoreListCursors()
    {
        if (_pendingCursors.Count == 0)
        {
            return;
        }

        foreach (var (key, index) in _pendingCursors)
        {
            if (_listCursors.TryGetValue(key, out var cursor) && cursor.Count() is > 0 and var count)
            {
                cursor.MoveTo(Math.Min(index, count - 1));
            }
        }

        _pendingCursors.Clear();
    }

    /// <summary>The cursor row of the list hosted at <paramref name="key"/>, or null if there is none.</summary>
    internal int? ListCursorIndex(string key) => _listCursors.TryGetValue(key, out var cursor) ? cursor.Index() : null;

    public void Render()
    {
        if (_terminal is not { } terminal || !IsReady)
        {
            return;
        }

        NeedsRedraw = false;
        Tracker.BeginFrame();

        // Data first: RenderContent decides what the tab is showing, and BuildLayout is allowed to
        // branch on that (a placeholder state can arrange differently from a live one).
        RenderContent();
        RestoreListCursors();

        var (columns, rows) = terminal.Size;
        var content = new Rect<int>(0, _topRows, columns, Math.Max(0, rows - _topRows - _bottomRows));
        if (content.Width <= 0 || content.Height <= 0)
        {
            return;
        }

        // Published before BuildLayout so a tree can branch on how much room it has -- how many card
        // columns fit, whether to stack rather than dock. Content area, not the whole terminal.
        Content = content;

        Arranged = Layout.Engine.Arrange(BuildLayout(), content, MeasureContext);
        CellLayout.Paint(terminal, Arranged, PlaceAndPaint);

        // The caret is STICKY terminal state, so the frame that stops drawing a focused field has to say
        // so or it stays parked where the field used to be. A field that IS focused parks it itself while
        // being painted, so only the absence needs handling here -- and it belongs here rather than in a
        // tab, because a tab that forgot would leave a caret blinking over someone else's text.
        var caretOwned = false;
        foreach (var an in Arranged)
        {
            if (an.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput { State.IsActive: true } })
            {
                caretOwned = true;
                break;
            }
        }

        if (!caretOwned)
        {
            terminal.HideCaret();
        }

        RegisterClickableRegions();
    }

    /// <summary>
    /// Re-points a hosted widget's viewport at the rect its <c>Fill</c> leaf was arranged into, then lets
    /// the tab draw. An unregistered key is ignored rather than throwing: a tree that names a host it did
    /// not create should leave a hole, not take the whole TUI down mid-frame.
    /// </summary>
    private void PlaceAndPaint(Layout.Content.Fill fill, Rect<int> rect)
    {
        if (fill.Key is not { } key || !_hosts.TryGetValue(key, out var host))
        {
            return;
        }

        var geometryChanged = host.Place(rect);
        PaintHost(key, rect, geometryChanged);
    }

    /// <summary>
    /// Creates and registers a viewport for the widget hosted at <paramref name="key"/>. Call once from
    /// <see cref="CreateWidgets"/> and pass the result to the widget's constructor; its geometry is
    /// meaningless until the first arrange places it.
    /// </summary>
    protected TerminalViewport Host(string key)
    {
        var terminal = _terminal ?? throw new InvalidOperationException(
            $"Host('{key}') is only valid from CreateWidgets, after Attach has supplied the terminal.");

        var viewport = new TerminalViewport(terminal, 0, 0, 0, 0);
        _hosts[key] = new HostedRegion(viewport);
        return viewport;
    }

    /// <summary>
    /// Creates the <see cref="ScrollableList{T}"/> hosted at <paramref name="key"/> and makes its cursor
    /// survive <see cref="Attach"/>. A list whose cursor is the user's own place (not one the tab derives
    /// from state every frame) should be created through this rather than <see cref="Host"/>.
    /// </summary>
    protected ScrollableList<T> HostList<T>(string key) where T : IRowLayout
    {
        var list = new ScrollableList<T>(Host(key));
        _listCursors[key] = new ListCursor(() => list.CursorIndex, () => list.ItemCount, index => list.MoveTo(index));
        return list;
    }

    /// <summary>
    /// Dispatches a click against the arranged tree first, then hands the event to the tab.
    /// <para>
    /// Layout-tree hits are resolved HERE rather than per tab, which removes an asymmetry with the pixel
    /// side: there, <c>PixelWidgetBase.PaintLayout</c> registers each node's hit as a side effect of
    /// painting, so draw==hit holds automatically. <see cref="CellLayout"/> only draws, so a terminal tab
    /// had to remember to wire the dispatch itself -- and a tab that forgot had clickable nodes that
    /// silently did nothing. Doing it in the base means every layout-driven tab gets it, and no tab can get
    /// it wrong.
    /// </para>
    /// <para>
    /// A tree hit consumes the event; anything else falls through to <see cref="HandleTabInput"/>. That
    /// ordering only affects a tab whose tree actually carries <c>Clickable</c> nodes -- the tabs whose
    /// trees are <c>Fill</c> leaves still route their clicks to their own lists and trackers.
    /// </para>
    /// </summary>
    public void HandleInput(InputEvent evt)
    {
        if (evt is InputEvent.MouseUp(var x, var y, MouseButton.Left) && DispatchLayoutHit(x, y))
        {
            NeedsRedraw = true;
            return;
        }

        HandleTabInput(evt);
    }

    /// <summary>
    /// The tab's own input handling: keys, and mouse the layout tree did not claim. Signal that an event was
    /// consumed by setting <see cref="NeedsRedraw"/> -- which is also what stops the app loop treating a
    /// swallowed <c>Q</c> as a request to exit. Default is to ignore everything, so a tab whose whole
    /// surface is clickable layout nodes needs no override.
    /// </summary>
    protected virtual void HandleTabInput(InputEvent evt) { }

    /// <summary>
    /// Raw mouse dispatch for ScrollableList drag handling. Default is a no-op --
    /// override to route to the tab's list widgets (e.g., <c>_xxxList.HandleMouse(mouse)</c>).
    /// </summary>
    public virtual bool HandleRawMouse(MouseEvent mouse) => false;

    /// <summary>
    /// Creates the tab's widgets, taking each one's viewport from <see cref="Host"/>. Called from
    /// <see cref="Attach"/>, so it re-runs on terminal resize.
    /// </summary>
    protected abstract void CreateWidgets();

    /// <summary>The arrangement for this frame. Rebuilt every frame, so it may branch on live state.</summary>
    protected abstract Layout.Node BuildLayout();

    /// <summary>Whether all required widgets have been created.</summary>
    protected abstract bool IsReady { get; }

    /// <summary>
    /// Fills widget data for the current frame. Runs before the tree is built and painted, so the values
    /// it computes are available to <see cref="BuildLayout"/>.
    /// </summary>
    protected abstract void RenderContent();

    /// <summary>
    /// Draws the widget hosted at <paramref name="key"/>; its viewport is already positioned at
    /// <paramref name="rect"/>.
    /// <para>
    /// <paramref name="geometryChanged"/> is true on the first paint and whenever the rect's size changed
    /// since the last one. A cell-based widget can ignore it, but a <b>pixel-backed</b> host must not: a
    /// Sixel <see cref="Canvas"/> owns a renderer allocated at a fixed pixel size, and that size is only
    /// knowable after the arrange. This flag is when to (re)allocate it.
    /// </para>
    /// </summary>
    protected abstract void PaintHost(string key, Rect<int> rect, bool geometryChanged);

    /// <summary>
    /// Registers clickable regions after the frame is painted. Prefer binding hits on the nodes
    /// themselves (<c>.Clickable(...)</c>) and dispatching through <see cref="CellLayout.HitTest"/> over
    /// <see cref="Arranged"/>, which keeps draw and hit on the same rect by construction.
    /// </summary>
    protected virtual void RegisterClickableRegions() { }

    /// <summary>A list's cursor, read and moved without knowing its item type.</summary>
    private sealed record ListCursor(Func<int> Index, Func<int> Count, Func<int, bool> MoveTo);

    /// <summary>
    /// One hosted widget's viewport, plus the last size it was placed at so a resize can be reported
    /// exactly once per change rather than every frame.
    /// </summary>
    private sealed class HostedRegion(TerminalViewport viewport)
    {
        private int _width = -1;
        private int _height = -1;

        /// <summary>Moves the viewport to <paramref name="rect"/>; returns true if its size changed.</summary>
        public bool Place(Rect<int> rect)
        {
            viewport.UpdateGeometry(rect.X, rect.Y, rect.Width, rect.Height);

            if (rect.Width == _width && rect.Height == _height)
            {
                return false;
            }

            _width = rect.Width;
            _height = rect.Height;
            return true;
        }
    }
}
