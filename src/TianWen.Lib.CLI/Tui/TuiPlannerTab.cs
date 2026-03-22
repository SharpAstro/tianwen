using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using TianWen.Lib.CLI.Plan;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI planner tab. Extracted from <see cref="PlanSubCommand.RunInteractiveLoopAsync"/>.
/// Shows tonight's best targets with altitude chart, target list, and details panel.
/// </summary>
internal sealed class TuiPlannerTab(
    PlannerState plannerState,
    Transform transform,
    string fontPath) : ITuiTab
{
    private Panel? _panel;
    private TextBar? _topBar;
    private TextBar? _statusBar;
    private ScrollableList<TargetListItem>? _targetList;
    private MarkdownWidget? _detailWidget;
    private Canvas<RgbaImage>? _canvas;
    private RgbaImageRenderer? _canvasRenderer;

    public bool NeedsRedraw { get; set; } = true;

    public void BuildPanel(IVirtualTerminal terminal, int topRows = 1, int bottomRows = 1)
    {
        _panel = new Panel(terminal);
        // Reserve space for the TUI host's tab bar and status bar
        _panel.Dock(DockStyle.Top, topRows);
        _panel.Dock(DockStyle.Bottom, bottomRows);
        var topVp = _panel.Dock(DockStyle.Top, 1);
        var bottomVp = _panel.Dock(DockStyle.Bottom, 1);
        var detailVp = _panel.Dock(DockStyle.Bottom, 8);
        var leftVp = _panel.Dock(DockStyle.Left, 32);
        var fillVp = _panel.Fill();

        _topBar = new TextBar(topVp);
        _statusBar = new TextBar(bottomVp);
        _targetList = new ScrollableList<TargetListItem>(leftVp);
        _detailWidget = new MarkdownWidget(detailVp);

        var canvasPixelSize = fillVp.PixelSize;
        _canvasRenderer = new RgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
        _canvas = new Canvas<RgbaImage>(fillVp, _canvasRenderer);

        _panel.Add(_topBar).Add(_statusBar).Add(_targetList).Add(_detailWidget).Add(_canvas);
        NeedsRedraw = true;
    }

    public void Render()
    {
        if (_panel is null || _topBar is null || _statusBar is null ||
            _targetList is null || _detailWidget is null || _canvas is null || _canvasRenderer is null)
        {
            return;
        }

        NeedsRedraw = false;

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
        var scheduleStatus = plannerState.Schedule is { Count: > 0 } s
            ? $"Schedule: {s.Count} obs"
            : "";
        var statusText = plannerState.StatusMessage is { } msg
            ? $" {msg}"
            : " \u2191\u2193:nav Enter:toggle P:priority S:schedule Q:quit";
        _statusBar.Text(statusText);
        _statusBar.RightText($"{scheduleStatus} ");

        _panel.RenderAll();
    }

    public bool HandleInput(InputEvent evt)
    {
        switch (evt)
        {
            case InputEvent.MouseUp(var x, var y, MouseButton.Left):
                if (_targetList is not null)
                {
                    var cell = _targetList.HitTest((int)x, (int)y);
                    if (cell is { Row: var row } && row >= 0)
                    {
                        var scrollOffset = Math.Max(0, plannerState.SelectedTargetIndex - _targetList.VisibleRows / 2);
                        var itemIndex = row - 1 + scrollOffset;
                        if (itemIndex >= 0 && itemIndex < plannerState.TonightsBest.Count)
                        {
                            plannerState.SelectedTargetIndex = itemIndex;
                            NeedsRedraw = true;
                        }
                    }
                }
                return false;

            case InputEvent.Scroll(var delta, _, _, _):
                var step = delta > 0 ? -3 : 3;
                plannerState.SelectedTargetIndex = Math.Clamp(
                    plannerState.SelectedTargetIndex + step, 0, plannerState.TonightsBest.Count - 1);
                NeedsRedraw = true;
                return false;

            case InputEvent.KeyDown(var key, _):
                if (plannerState.StatusMessage is not null)
                {
                    plannerState.StatusMessage = null;
                    NeedsRedraw = true;
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

                    case InputKey.S:
                        PlannerActions.BuildSchedule(plannerState, transform,
                            defaultGain: 120, defaultOffset: 10,
                            defaultSubExposure: TimeSpan.FromSeconds(120),
                            defaultObservationTime: TimeSpan.FromMinutes(60));
                        NeedsRedraw = true;
                        return false;
                }
                break;
        }

        return false;
    }
}
