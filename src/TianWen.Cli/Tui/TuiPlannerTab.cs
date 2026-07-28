using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;
using TianWen.Cli.Plan;

namespace TianWen.Cli.Tui;

/// <summary>
/// TUI planner tab. Extracted from <see cref="PlanSubCommand.RunInteractiveLoopAsync"/>.
/// Shows tonight's best targets with altitude chart, target list, and details panel.
/// </summary>
internal sealed class TuiPlannerTab(
    GuiAppState appState,
    PlannerState plannerState,
    string fontPath,
    ITimeProvider timeProvider) : TuiTabBase
{
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
            Layout.Builder.HStack(
                Layout.Builder.Fill(key: ListKey).WFixed(32),
                Layout.Builder.Fill(key: CanvasKey).WStar()).Stretch(),
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
                }

                if (_canvas is { } canvas && _canvasRenderer is { } canvasRenderer)
                {
                    RenderAltitudeChart(canvas, canvasRenderer);
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
        _topBar.Text($" {siteLabel} | Dark: {darkLocal:HH:mm}-{twLocal:HH:mm} | Proposals: {plannerState.Proposals.Length}");
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
        var statusText = plannerState.StatusMessage is { } msg
            ? $" {msg}"
            : " \u2191\u2193:nav Enter:toggle P:priority S:schedule Q:quit";
        _statusBar.Text(statusText);
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    /// <summary>
    /// Row clicks select the target. The list owns the geometry -- including yielding the scrollbar
    /// column, which this used to compute for itself.
    /// </summary>
    protected override void RegisterClickableRegions() =>
        _targetList?.RegisterRowHits(Tracker,
            hitFor: (index, _) => new HitResult.ListItemHit("TargetList", index),
            onClick: (index, _) => plannerState.SelectedTargetIndex = index);

    public override bool HandleRawMouse(MouseEvent mouse)
    {
        if (_targetList is { } list && list.HandleMouse(mouse))
        {
            NeedsRedraw = true;
            return true;
        }
        return false;
    }

    public override bool HandleInput(InputEvent evt)
    {
        switch (evt)
        {
            case InputEvent.MouseUp(var x, var y, MouseButton.Left):
                // Slider hit test (uses chart time-layout math directly)
                if (HitTestSliderOnCanvas(x, y))
                {
                    NeedsRedraw = true;
                    return false;
                }

                // Target list and other regions via tracker
                if (Tracker.HitTestAndDispatch(x, y) is not null)
                {
                    NeedsRedraw = true;
                }
                else if (plannerState.SelectedSliderIndex >= 0)
                {
                    // Click outside any region → deselect slider
                    PlannerActions.SelectSlider(plannerState, -1);
                    NeedsRedraw = true;
                }
                return false;

            case InputEvent.Scroll(var delta, _, _, _):
                var scrollTargets = PlannerActions.GetFilteredTargets(plannerState);
                var scrollStep = delta > 0 ? -3 : 3;
                plannerState.SelectedTargetIndex = Math.Clamp(
                    plannerState.SelectedTargetIndex + scrollStep, 0, scrollTargets.Count - 1);
                NeedsRedraw = true;
                return false;

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
                    return false;
                }

                var filtered = PlannerActions.GetFilteredTargets(plannerState);
                switch (key)
                {
                    case InputKey.Up:
                        if (plannerState.SelectedTargetIndex > 0)
                        {
                            plannerState.SelectedTargetIndex--;
                            NeedsRedraw = true;
                        }
                        return false;

                    case InputKey.Down:
                        if (plannerState.SelectedTargetIndex < filtered.Count - 1)
                        {
                            plannerState.SelectedTargetIndex++;
                            NeedsRedraw = true;
                        }
                        return false;

                    case InputKey.Enter:
                        if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < filtered.Count)
                        {
                            // followPinnedSelection: the cursor follows the pinned target into the
                            // pinned section (render's EnsureVisible then scrolls it into view).
                            PlannerActions.ToggleProposal(plannerState, filtered[plannerState.SelectedTargetIndex].Target, followPinnedSelection: true);
                            NeedsRedraw = true;
                        }
                        return false;

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
                        return false;

                }
                break;
        }

        return false;
    }

    /// <summary>
    /// Hit-tests sliders on the chart canvas using chart time-layout math.
    /// Returns true if a slider was hit and selected.
    /// <para>
    /// The one place in this tab that still converts cells to pixels by hand, and legitimately so: the
    /// chart is a raster surface, and a slider drawn into it is at a pixel position no layout node
    /// describes. Row-level geometry belongs to <see cref="ScrollableList{T}"/> (see
    /// <see cref="RegisterClickableRegions"/>); this is the raster bucket, not that one.
    /// </para>
    /// </summary>
    private bool HitTestSliderOnCanvas(float x, float y)
    {
        if (_canvas is null)
        {
            return false;
        }

        var canvasCell = _canvas.Viewport.CellSize;
        var canvasOffset = _canvas.Viewport.Offset;
        var canvasPixelSize = _canvas.PixelSize;
        var localX = x - canvasOffset.Column * canvasCell.Width;
        var localY = y - canvasOffset.Row * canvasCell.Height;

        if (localX < 0 || localX >= canvasPixelSize.Width ||
            localY < 0 || localY >= canvasPixelSize.Height)
        {
            return false;
        }

        var sliderIdx = PlannerActions.HitTestSlider(
            plannerState, localX, 0, canvasPixelSize.Width);
        if (sliderIdx >= 0)
        {
            PlannerActions.SelectSlider(plannerState, sliderIdx);
            return true;
        }

        return false;
    }
}
