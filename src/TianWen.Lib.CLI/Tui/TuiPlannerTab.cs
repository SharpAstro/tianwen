using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.UI.Abstractions;
using TianWen.Lib.CLI.Plan;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI planner tab. Extracted from <see cref="PlanSubCommand.RunInteractiveLoopAsync"/>.
/// Shows tonight's best targets with altitude chart, target list, and details panel.
/// </summary>
internal sealed class TuiPlannerTab(
    PlannerState plannerState,
    string fontPath) : TuiTabBase
{
    private TextBar? _topBar;
    private TextBar? _statusBar;
    private ScrollableList<TargetListItem>? _targetList;
    private MarkdownWidget? _detailWidget;
    private Canvas<RgbaImage>? _canvas;
    private RgbaImageRenderer? _canvasRenderer;

    [MemberNotNullWhen(true, nameof(_topBar), nameof(_statusBar), nameof(_targetList),
        nameof(_detailWidget), nameof(_canvas), nameof(_canvasRenderer))]
    protected override bool IsReady =>
        _topBar is not null && _statusBar is not null && _targetList is not null
        && _detailWidget is not null && _canvas is not null && _canvasRenderer is not null;

    protected override void CreateWidgets(Panel panel)
    {
        var topVp = panel.Dock(DockStyle.Top, 1);
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var detailVp = panel.Dock(DockStyle.Bottom, 8);
        var leftVp = panel.Dock(DockStyle.Left, 32);
        var fillVp = panel.Fill();

        _topBar = new TextBar(topVp);
        _statusBar = new TextBar(bottomVp);
        _targetList = new ScrollableList<TargetListItem>(leftVp);
        _detailWidget = new MarkdownWidget(detailVp);

        var canvasPixelSize = fillVp.PixelSize;
        _canvasRenderer = new RgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
        _canvas = new Canvas<RgbaImage>(fillVp, _canvasRenderer);

        panel.Add(_topBar).Add(_statusBar).Add(_targetList).Add(_detailWidget).Add(_canvas);
    }

    protected override void RenderContent()
    {
        if (!IsReady) return;

        // Top bar
        var siteLabel = $"{plannerState.SiteLatitude:F1}\u00b0N {plannerState.SiteLongitude:F1}\u00b0E";
        var darkLocal = plannerState.AstroDark.ToOffset(plannerState.SiteTimeZone);
        var twLocal = plannerState.AstroTwilight.ToOffset(plannerState.SiteTimeZone);
        _topBar.Text($" {siteLabel} | Dark: {darkLocal:HH:mm}-{twLocal:HH:mm} | Proposals: {plannerState.Proposals.Count}");
        _topBar.RightText($"{plannerState.ActiveProfile?.DisplayName ?? "No profile"} ");

        // Target list
        var filteredTargets = PlannerActions.GetFilteredTargets(plannerState);
        var targetRows = PlannerTargetList.GetItems(plannerState, filteredTargets);
        var items = new TargetListItem[targetRows.Count];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new TargetListItem(targetRows[i]);
        }
        _targetList.Items(items).Header("Tonight's Best").ScrollTo(
            Math.Max(0, plannerState.SelectedTargetIndex - _targetList.VisibleRows / 2));

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

        // Altitude chart
        var canvasPixelSize = _canvas.PixelSize;
        _canvasRenderer.FillRectangle(
            new RectInt(new PointInt((int)canvasPixelSize.Width, (int)canvasPixelSize.Height), new PointInt(0, 0)),
            new RGBAColor32(0x1a, 0x1a, 0x2e, 0xff));
        AltitudeChartRenderer.Render(_canvasRenderer, plannerState, fontPath,
            highlightTargetIndex: plannerState.SelectedTargetIndex);

        // Status bar
        var statusText = plannerState.StatusMessage is { } msg
            ? $" {msg}"
            : " \u2191\u2193:nav Enter:toggle P:priority S:schedule Q:quit";
        _statusBar.Text(statusText);
        _statusBar.RightText(plannerState.StatusMessage ?? "");
    }

    protected override void RegisterClickableRegions()
    {
        if (_targetList is not { } targetList)
        {
            return;
        }

        var cellSize = targetList.Viewport.CellSize;
        var offset = targetList.Viewport.Offset;
        var baseX = (float)(offset.Column * cellSize.Width);
        var baseY = (float)(offset.Row * cellSize.Height);
        var rowW = (float)(targetList.Viewport.Size.Width * cellSize.Width);
        var rowH = (float)cellSize.Height;
        var scrollOffset = Math.Max(0, plannerState.SelectedTargetIndex - targetList.VisibleRows / 2);

        for (var i = 0; i < targetList.VisibleRows && scrollOffset + i < plannerState.TonightsBest.Count; i++)
        {
            var capturedIdx = scrollOffset + i;
            var y = baseY + (1 + i) * rowH; // +1 for header
            Tracker.Register(baseX, y, rowW, rowH,
                new HitResult.ListItemHit("TargetList", capturedIdx),
                _ => { plannerState.SelectedTargetIndex = capturedIdx; });
        }
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
                var step = delta > 0 ? -3 : 3;
                plannerState.SelectedTargetIndex = Math.Clamp(
                    plannerState.SelectedTargetIndex + step, 0, plannerState.TonightsBest.Count - 1);
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
                        if (plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count - 1)
                        {
                            plannerState.SelectedTargetIndex++;
                            NeedsRedraw = true;
                        }
                        return false;

                    case InputKey.Enter:
                        if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
                        {
                            var target = plannerState.TonightsBest[plannerState.SelectedTargetIndex].Target;
                            PlannerActions.ToggleProposal(plannerState, target);
                            NeedsRedraw = true;
                        }
                        return false;

                    case InputKey.P:
                        if (plannerState.SelectedTargetIndex >= 0 && plannerState.SelectedTargetIndex < plannerState.TonightsBest.Count)
                        {
                            var target = plannerState.TonightsBest[plannerState.SelectedTargetIndex].Target;
                            var propIdx = plannerState.Proposals.FindIndex(p => p.Target == target);
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
