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
    private static readonly CellMeasureContext MeasureContext = new CellMeasureContext();

    private readonly Dictionary<string, HostedRegion> _hosts = [];
    private IVirtualTerminal? _terminal;
    private int _topRows;
    private int _bottomRows;

    public bool NeedsRedraw { get; set; } = true;

    /// <summary>
    /// The tree as last arranged, for hit-testing via <see cref="CellLayout.HitTest"/> and for tests that
    /// assert placement without a terminal.
    /// </summary>
    protected ImmutableArray<Layout.ArrangedNode<int>> Arranged { get; private set; }

    public void Attach(IVirtualTerminal terminal, int topRows = 1, int bottomRows = 1)
    {
        _terminal = terminal;
        _topRows = topRows;
        _bottomRows = bottomRows;
        _hosts.Clear();
        CreateWidgets();
        NeedsRedraw = true;
    }

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

        var (columns, rows) = terminal.Size;
        var content = new Rect<int>(0, _topRows, columns, Math.Max(0, rows - _topRows - _bottomRows));
        if (content.Width <= 0 || content.Height <= 0)
        {
            return;
        }

        Arranged = Layout.Engine.Arrange(BuildLayout(), content, MeasureContext);
        CellLayout.Paint(terminal, Arranged, PlaceAndPaint);
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

    public abstract bool HandleInput(InputEvent evt);

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
