using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using TianWen.Cli.Plan;

namespace TianWen.Cli.Tui;

/// <summary>
/// TUI planner tab. Extracted from <see cref="PlanSubCommand.RunInteractiveLoopAsync"/>.
/// Shows tonight's best targets with altitude chart, target list, and details panel.
/// </summary>
/// <remarks>
/// The night calendar (docs/plans/night-calendar.md, P4) is the GUI's own widget,
/// <see cref="NightCalendarPopover{TSurface}"/>, painted over the chart on the same Sixel canvas: the drawn Moon,
/// the verdict tints and the pins strip need pixels, and a second, text-cell calendar would be a second answer
/// to what a night looks like. A terminal has no hover, so the calendar is driven by its own keys (a cursor the
/// detail follows), and a click reaches it through a router over the widget, in canvas pixels.
/// </remarks>
internal sealed class TuiPlannerTab(
    GuiAppState appState,
    PlannerState plannerState,
    string fontPath,
    ITimeProvider timeProvider,
    SignalBus bus) : TuiTabBase
{
    // The calendar's design size in design units (NightCalendarPopover's box with pins lines): the canvas is fitted
    // to it, so the calendar scales with the terminal instead of overflowing a small one.
    private const float CalendarDesignWidth = 480f;
    private const float CalendarDesignHeight = 480f;

    private TextBar? _topBar;
    private TextBar? _statusBar;
    private ScrollableList<TargetListItem>? _targetList;
    private MarkdownWidget? _detailWidget;

    // The altitude chart is pixel-backed, so its renderer can only be sized once the tree has been
    // arranged. Built on first placement and rebuilt on resize (PaintHost). Interface-typed because
    // PixelSize is a default interface member, only reachable through ITerminalViewport.
    private ITerminalViewport? _canvasViewport;
    private Canvas? _canvas;
    private SixelRgbaImageRenderer? _canvasRenderer;
    private int _lastEnsuredIndex = -1;

    // Rebuilt with the canvas renderer, which a resize replaces.
    private NightCalendarPopover<RgbaImage>? _calendar;
    private InputRouter? _calendarRouter;
    private readonly BackgroundTaskTracker _calendarTracker = new BackgroundTaskTracker();

    /// <summary>
    /// Readiness covers only what <see cref="CreateWidgets"/> builds. The chart canvas is deliberately
    /// absent: it cannot exist before the first arrange, so requiring it here would stop the tab ever
    /// rendering the frame that would size it.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_topBar), nameof(_statusBar), nameof(_targetList),
        nameof(_detailWidget))]
    protected override bool IsReady =>
        _topBar is not null && _statusBar is not null && _targetList is not null
        && _detailWidget is not null;

    // Fill-leaf keys. The tree names these; PaintHost draws them.
    private const string TopKey = "top";
    private const string ListKey = "list";
    private const string CanvasKey = "canvas";
    private const string DetailKey = "detail";
    private const string StatusKey = "status";

    protected override void CreateWidgets()
    {
        _topBar = new TextBar(Host(TopKey));
        _statusBar = new TextBar(Host(StatusKey));
        _targetList = new ScrollableList<TargetListItem>(Host(ListKey));
        _detailWidget = new MarkdownWidget(Host(DetailKey));

        // Only the viewport -- the renderer and canvas need a pixel size the arrange has not produced yet.
        _canvasViewport = Host(CanvasKey);
    }

    /// <summary>
    /// Top bar, then the target list beside the altitude chart, then the detail panel, then the status
    /// row -- the same arrangement the docked Panel produced (its Bottom 1 / Bottom 8 order is what put
    /// the detail panel above the status bar).
    /// </summary>
    protected override Layout.Node BuildLayout() =>
        Layout.Builder.VStack(
            Layout.Builder.Fill(key: TopKey).RowH(1),
            // ColW (Width=Fixed, Height=Star), not WFixed: in an HStack the cross axis is the height, and a
            // Fill leaf on Auto height measures its MinHeight -- zero. That left the chart canvas zero rows
            // tall, so it allocated no Sixel buffer and drew nothing.
            Layout.Builder.HStack(
                Layout.Builder.Fill(key: ListKey).ColW(32),
                Layout.Builder.Fill(key: CanvasKey).Stretch()).Stretch(),
            Layout.Builder.Fill(key: DetailKey).RowH(8),
            Layout.Builder.Fill(key: StatusKey).RowH(1));

    /// <summary>
    /// Draws one hosted widget. The chart is why <paramref name="geometryChanged"/> matters: its Sixel
    /// renderer is a fixed-size pixel buffer, so it is allocated on first placement and reallocated on
    /// resize. The Panel-based version sized it once at construction and never again, so a terminal
    /// resize left the chart rendering at the old pixel size.
    /// </summary>
    protected override void PaintHost(string key, Rect<int> rect, bool geometryChanged)
    {
        switch (key)
        {
            case TopKey:
                _topBar?.Render();
                break;

            case ListKey:
                _targetList?.Render();
                break;

            case DetailKey:
                _detailWidget?.Render();
                break;

            case StatusKey:
                _statusBar?.Render();
                break;

            case CanvasKey when _canvasViewport is { } viewport:
                if (geometryChanged)
                {
                    var (pixelWidth, pixelHeight) = viewport.PixelSize;
                    _canvasRenderer?.Dispose();
                    _canvasRenderer = new SixelRgbaImageRenderer((uint)pixelWidth, (uint)pixelHeight);
                    _canvas = new Canvas(viewport, _canvasRenderer);
                    var calendar = new NightCalendarPopover<RgbaImage>(_canvasRenderer) { FontPath = fontPath };
                    _calendar = calendar;
                    _calendarRouter = new InputRouter(calendar.Ui, _calendarTracker, () => NeedsRedraw = true)
                    {
                        Widgets = () => [calendar],
                    };
                }

                if (_canvas is { } canvas && _canvasRenderer is { } canvasRenderer)
                {
                    RenderAltitudeChart(canvas, canvasRenderer);
                    RenderCalendar(canvasRenderer);
                    canvas.Render();
                }
                break;
        }
    }

    /// <summary>
    /// Draws the altitude chart. Lives here rather than in <see cref="RenderContent"/> because it needs
    /// the canvas's pixel size, which only exists once the tree has been arranged.
    /// </summary>
    private void RenderAltitudeChart(Canvas canvas, SixelRgbaImageRenderer canvasRenderer)
    {
        var canvasPixelSize = canvas.PixelSize;
        canvasRenderer.FillRectangle(
            new RectInt(new PointInt((int)canvasPixelSize.Width, (int)canvasPixelSize.Height), new PointInt(0, 0)),
            new RGBAColor32(0x1a, 0x1a, 0x2e, 0xff));
        var chartCurrentTime = plannerState.PlanningDate.HasValue
            ? null as DateTimeOffset?
            : timeProvider.GetUtcNow().ToOffset(plannerState.SiteTimeZone);
        AltitudeChartRenderer.Render(canvasRenderer, plannerState, fontPath,
            0, 0, (int)canvasRenderer.Width, (int)canvasRenderer.Height,
            highlightTargetIndex: plannerState.SelectedTargetIndex,
            currentTime: chartCurrentTime);
    }

    /// <summary>
    /// The night calendar over the chart, fitted to the canvas and hung from its top edge. Painted every frame,
    /// open or not: a closed calendar only begins its frame, which is what retires its regions.
    /// </summary>
    private void RenderCalendar(SixelRgbaImageRenderer renderer)
    {
        if (_calendar is not { } calendar)
        {
            return;
        }

        var width = (float)renderer.Width;
        var height = (float)renderer.Height;
        calendar.DpiScale = Math.Clamp(Math.Min(width / CalendarDesignWidth, height / CalendarDesignHeight), 0.5f, 1.5f);
        var calendarWidth = CalendarDesignWidth * calendar.DpiScale;
        calendar.Render(plannerState, new RectF32((width - calendarWidth) / 2f, 0f, calendarWidth, 0f),
            new RectF32(0f, 0f, width, height), timeProvider, bus);
    }

    protected override void RenderContent()
    {
        if (!IsReady) return;

        // Top bar
        var siteLabel = $"{plannerState.SiteLatitude:F1}\u00b0N {plannerState.SiteLongitude:F1}\u00b0E";

        // Guard: planner data not yet computed (AstroDark defaults to 0001-01-01)
        if (plannerState.AstroDark == default)
        {
            _topBar.Text($" {siteLabel} | Computing...");
            _statusBar.Text(" Waiting for planner data...");
            return;
        }

        var darkLocal = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
        var twLocal = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);

        // The planned night and its verdict, from the same summary the calendar cell and the GUI's status bar read.
        var night = NightCalendarActions.PlanningEveningDate(plannerState, timeProvider);
        var dateLabel = plannerState.PlanningDate is null ? "Tonight" : night.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        var verdict = plannerState.Calendar.Data.Nights.TryGetValue(night, out var summary)
            ? $" {NightVerdict.For(summary).Label}"
            : "";
        _topBar.Text($" {siteLabel} | {dateLabel} {darkLocal:HH:mm}-{twLocal:HH:mm}{verdict} | Proposals: {plannerState.Proposals.Length}");
        _topBar.RightText($"{plannerState.ActiveProfile?.DisplayName ?? "No profile"} ");

        // Target list
        var filteredTargets = PlannerActions.GetFilteredTargets(plannerState);
        var targetRows = PlannerTargetList.GetItems(plannerState, filteredTargets);
        var items = new TargetListItem[targetRows.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new TargetListItem(targetRows[i]);
        }
        _targetList.Items(items).Header("Tonight's Best");
        if (plannerState.SelectedTargetIndex != _lastEnsuredIndex)
        {
            _targetList.EnsureVisible(plannerState.SelectedTargetIndex);
            _lastEnsuredIndex = plannerState.SelectedTargetIndex;
        }

        // Detail panel
        var detailLines = PlannerDetails.GetLines(plannerState, filteredTargets);
        if (detailLines.Count > 0)
        {
            var md = $"## {detailLines[0]}\n\n";
            for (var i = 1; i < detailLines.Count; i++)
            {
                md += detailLines[i] + "\n\n";
            }
            md += "*Enter* to add/remove | *P* priority | *S* schedule | *Q* quit";
            _detailWidget.Markdown(md);
        }

        // The altitude chart is drawn from PaintHost, not here -- it needs the arranged pixel size.

        // Status bar
        var statusText = plannerState.StatusMessage is { } msg ? $" {msg}"
            : plannerState.Calendar.Popover.IsOpen
                ? " \u2190\u2192\u2191\u2193:night Enter:plan PgUp/PgDn:month T:tonight Esc:close"
                : " \u2191\u2193:nav Enter:toggle P:priority PgUp/PgDn:night C:calendar Q:quit";
        _statusBar.Text(statusText);
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    /// <summary>
    /// Steps the target list by one row and mirrors the result into the shared selection.
    /// </summary>
    /// <remarks>
    /// The list owns the walk -- its own item count, its own clamp, its own scroll follow -- where this
    /// used to be a hand-written bounds check against a separately recomputed filtered list, which is two
    /// derivations of one row count. The selection itself stays in <see cref="PlannerState"/>, the GUI
    /// reading it too, so the cursor is placed FROM it before the step exactly as the click path writes
    /// it straight (see <see cref="DispatchTargetListClick"/>).
    /// <para>
    /// With nothing selected a step forward lands on the first row and a step back on nothing, which is
    /// where the bounds check left it.
    /// </para>
    /// </remarks>
    private void MoveTargetCursor(int delta)
    {
        if (_targetList is not { ItemCount: > 0 } list)
        {
            return;
        }

        var current = plannerState.SelectedTargetIndex;
        if (current < 0)
        {
            if (delta <= 0)
            {
                return;
            }

            list.MoveTo(0);
        }
        else
        {
            list.MoveTo(current);
            if (!list.MoveCursor(delta))
            {
                return;
            }
        }

        plannerState.SelectedTargetIndex = list.CursorIndex;
        NeedsRedraw = true;
    }

    /// <summary>
    /// Resolves a click on the target list to the target behind it. The list owns the geometry -- including
    /// yielding the scrollbar column, which this used to compute for itself.
    /// <para>
    /// The selected target lives in <see cref="PlannerState"/> (shared with the GUI) rather than in the
    /// list cursor, so the click has to write it explicitly; <see cref="ScrollableList{T}.HitTestRow"/>
    /// reports the ITEM index, which is the one the state is indexed by even when the list is scrolled.
    /// </para>
    /// </summary>
    private bool DispatchTargetListClick(int x, int y)
    {
        if (_targetList?.HitTestRow(x, y) is not { ItemIndex: var index })
        {
            return false;
        }

        plannerState.SelectedTargetIndex = index;
        NeedsRedraw = true;
        return true;
    }

    /// <summary>
    /// Registers the chart's slider bands.
    /// </summary>
    protected override void RegisterClickableRegions()
    {
        // The chart is a raster surface, so a divider drawn into it sits at a pixel position no layout node
        // describes -- this is the raster bucket, and the tracker is where its regions belong. The geometry
        // itself is NOT computed here: it comes from the same helper the GUI registers, which is what stops
        // the two drifting (this tab's own copy had lost the plot-Y bound).
        if (ChartCanvasGeometry() is not { } geometry)
        {
            return;
        }

        // At least one cell wide: a terminal cannot report a click finer than a cell, so a sub-cell band
        // would be a handle nothing can grab.
        var bands = PlannerSliderInteraction.GetHitBands(plannerState, geometry.Rect,
            MathF.Max(PlannerSliderInteraction.DefaultBandWidth, geometry.CellWidth));

        // The chart first, the handles over it: a later registration wins, so a press on a handle grabs
        // it and a press anywhere else in the plot places the nearest one -- the same order the GUI uses.
        Tracker.Register(geometry.Rect.X, geometry.Rect.Y, geometry.Rect.Width, geometry.Rect.Height,
            new HitResult.ButtonHit(PlannerSliderInteraction.ChartRegion));

        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            var index = i;
            Tracker.Register(band.X, band.Y, band.Width, band.Height,
                new HitResult.ButtonHit(PlannerSliderInteraction.DividerRegion),
                onClick: _ => PlannerActions.SelectSlider(plannerState, index));
        }
    }

    /// <summary>
    /// The chart canvas in MOUSE-PIXEL space -- the space <see cref="InputEvent"/> coordinates and the
    /// <see cref="TuiTabBase.Tracker"/> both use -- together with the cell width, or null before the first
    /// arrange has sized it. The chart itself draws at the canvas's own origin, so the viewport offset is
    /// what converts between the two.
    /// </summary>
    private (RectF32 Rect, float CellWidth)? ChartCanvasGeometry()
    {
        if (_canvas is not { } canvas)
        {
            return null;
        }

        var cell = canvas.Viewport.CellSize;
        var offset = canvas.Viewport.Offset;
        var pixels = canvas.PixelSize;
        return (new RectF32(offset.Column * cell.Width, offset.Row * cell.Height, pixels.Width, pixels.Height),
            cell.Width);
    }

    /// <summary>SGR mouse buttons for a wheel notch; a touchpad's two-finger scroll arrives as the same two.</summary>
    private const int WheelUp = 64;
    private const int WheelDown = 65;

    public override bool HandleRawMouse(MouseEvent mouse)
    {
        if (_targetList is not { } list)
        {
            return false;
        }

        // The wheel (and a touchpad, which a terminal reports as the same buttons) SCROLLS the list under the
        // pointer and moves nothing else. It used to fall through to HandleTabInput, which stepped the SELECTION
        // three rows per notch, so scrolling to look further down the list also changed the chart, the details
        // and what Enter would pin. Anywhere else the wheel means nothing on this tab, and is consumed as such.
        if (mouse.Button is WheelUp or WheelDown)
        {
            if (list.HitTest(mouse.X, mouse.Y) is not null
                && list.HandleWheel(mouse.Button == WheelUp ? list.WheelStep : -list.WheelStep))
            {
                NeedsRedraw = true;
            }
            return true;
        }

        if (list.HandleMouse(mouse))
        {
            NeedsRedraw = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Input while the calendar is open: keys through its router (Escape closes, the rest are its own), a click
    /// on the canvas through the router in canvas pixels, and a click anywhere else closes it before doing what
    /// it would have done, as the GUI's backdrop does.
    /// </summary>
    /// <returns>Whether the calendar took the event.</returns>
    private bool HandleCalendarInput(InputEvent evt)
    {
        if (!plannerState.Calendar.Popover.IsOpen || _calendarRouter is not { } router)
        {
            return false;
        }

        switch (evt)
        {
            case InputEvent.KeyDown when router.Handle(evt):
                NeedsRedraw = true;
                return true;

            case InputEvent.MouseUp(var x, var y, MouseButton.Left):
                if (ChartCanvasGeometry() is { Rect: var canvas } && canvas.Contains(x, y))
                {
                    var (cx, cy) = (x - canvas.X, y - canvas.Y);
                    router.Handle(new InputEvent.MouseDown(cx, cy, MouseButton.Left));
                    router.Handle(new InputEvent.MouseUp(cx, cy, MouseButton.Left));
                    NeedsRedraw = true;
                    return true;
                }

                plannerState.Calendar.Popover.Close();
                NeedsRedraw = true;
                return false;

            default:
                return false;
        }
    }

    protected override void HandleTabInput(InputEvent evt){
        if (HandleCalendarInput(evt))
        {
            return;
        }

        switch (evt)
        {
            case InputEvent.MouseUp(var x, var y, MouseButton.Left):
            {
                // A click on the target list is never also a slider grab -- the two surfaces do not
                // overlap -- so resolving rows first keeps the slider state machine off that path.
                if (DispatchTargetListClick((int)x, (int)y))
                {
                    return;
                }

                var selectedBefore = plannerState.SelectedSliderIndex;

                // Dispatching runs a handle's own click handler, which selects it. Two cases it cannot
                // carry: the CHART needs the press position to know where to place, and a click that
                // reached nothing means deselect.
                var hit = Tracker.HitTestAndDispatch(x, y);
                switch (hit)
                {
                    case HitResult.ButtonHit { Action: PlannerSliderInteraction.ChartRegion }:
                        // A terminal click is a ZERO-LENGTH drag: press and release arrive together, so
                        // the capture is opened and closed here rather than left for a move that never
                        // comes. Everything else about the gesture is the shared definition's.
                        PlannerSliderInteraction
                            .BeginPlaceNearest(plannerState, ChartCanvasGeometry()?.Rect ?? default, x, y)
                            ?.Release(default);
                        break;

                    case null:
                        PlannerSliderInteraction.HandlePressWithNoTarget(plannerState);
                        break;
                }

                if (hit is not null || plannerState.SelectedSliderIndex != selectedBefore)
                {
                    NeedsRedraw = true;
                }
                return;
            }

            case InputEvent.KeyDown(var key, var modifiers):
                if (plannerState.StatusMessage is not null)
                {
                    plannerState.StatusMessage = null;
                    NeedsRedraw = true;
                }

                // Slider keyboard control (shared with GPU)
                if (PlannerActions.HandleSliderKeyboard(plannerState, key, modifiers))
                {
                    NeedsRedraw = true;
                    return;
                }

                var filtered = PlannerActions.GetFilteredTargets(plannerState);
                switch (key)
                {
                    case InputKey.Up:
                    case InputKey.Down:
                        MoveTargetCursor(key == InputKey.Down ? 1 : -1);
                        return;

                    case InputKey.Enter:
                        if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < filtered.Count)
                        {
                            // followPinnedSelection: the cursor follows the pinned target into the
                            // pinned section (render's EnsureVisible then scrolls it into view).
                            PlannerActions.ToggleProposal(plannerState, filtered[plannerState.SelectedTargetIndex].Target, followPinnedSelection: true);
                            NeedsRedraw = true;
                        }
                        return;

                    // The night being planned, as the GUI planner keys it: PageUp the next night, PageDown the
                    // previous, T back to tonight, C the calendar.
                    case InputKey.PageUp:
                    case InputKey.PageDown:
                        PlannerActions.ShiftPlanningDate(plannerState, timeProvider, key == InputKey.PageUp ? 1 : -1);
                        NeedsRedraw = true;
                        return;

                    case InputKey.T:
                        PlannerActions.ResetPlanningDate(plannerState);
                        NeedsRedraw = true;
                        return;

                    case InputKey.C:
                        NightCalendarActions.PrepareToOpen(plannerState.Calendar);
                        plannerState.Calendar.Popover.Open();
                        NeedsRedraw = true;
                        return;

                    case InputKey.P:
                        if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < filtered.Count)
                        {
                            var propIdx = PlannerActions.FindProposalIndex(plannerState.Proposals, filtered[plannerState.SelectedTargetIndex].Target);
                            if (propIdx >= 0)
                            {
                                PlannerActions.CyclePriority(plannerState, propIdx);
                                NeedsRedraw = true;
                            }
                        }
                        return;

                }
                break;
        }

        return;
    }

}
