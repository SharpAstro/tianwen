using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI guider tab. Shows guide error sparklines and RMS stats.
/// PHD2-like monitoring for the guide loop.
/// </summary>
internal sealed class TuiGuiderTab(
    GuiAppState appState,
    LiveSessionState liveState) : TuiTabBase
{
    private const int SparklineWidth = 40;

    private readonly GuiderTabState _state = new GuiderTabState();

    private TextBar? _topBar;
    private TextBar? _statusBar;
    private MarkdownWidget? _graphPanel;
    private MarkdownWidget? _targetPanel;
    private MarkdownWidget? _statsPanel;

    [MemberNotNullWhen(true, nameof(_topBar), nameof(_statusBar), nameof(_graphPanel), nameof(_targetPanel), nameof(_statsPanel))]
    protected override bool IsReady =>
        _topBar is not null && _statusBar is not null && _graphPanel is not null && _targetPanel is not null && _statsPanel is not null;

    protected override void CreateWidgets(Panel panel)
    {
        var topVp = panel.Dock(DockStyle.Top, 1);
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);
        var leftVp = panel.Dock(DockStyle.Left, 44);
        var centerVp = panel.Dock(DockStyle.Left, 24);
        var fillVp = panel.Fill();

        _topBar = new TextBar(topVp);
        _statusBar = new TextBar(bottomVp);
        _graphPanel = new MarkdownWidget(leftVp);
        _targetPanel = new MarkdownWidget(centerVp);
        _statsPanel = new MarkdownWidget(fillVp);

        panel.Add(_topBar).Add(_statusBar).Add(_graphPanel).Add(_targetPanel).Add(_statsPanel);
    }

    protected override void RenderContent()
    {
        if (!IsReady) return;

        _state.PollFromLiveState(liveState);

        // Top bar
        var placeholder = _state.PlaceholderReason;
        if (placeholder is { } reason)
        {
            _topBar.Text($" {GuiderActions.PlaceholderText(reason)}");
            _topBar.RightText("");
        }
        else
        {
            var guiderLabel = _state.GuiderState ?? "Guiding";
            _topBar.Text($" [{guiderLabel}]  {_state.CurrentActivity ?? ""}");
            _topBar.RightText(GuiderActions.FormatRmsSummary(_state.LastGuideStats));
        }

        // Graph panel (left) — sparklines
        if (placeholder is not null)
        {
            _graphPanel.Markdown($"## Guider\n\n{GuiderActions.PlaceholderText(placeholder.Value)}");
        }
        else
        {
            var (raSpark, decSpark, raRange, decRange) = GuiderActions.BuildGuideSparklines(_state.GuideSamples, SparklineWidth);
            _graphPanel.Markdown(
                $"## RA Error ({raRange})\n\n" +
                $"{raSpark}\n\n" +
                $"## Dec Error ({decRange})\n\n" +
                $"{decSpark}");
        }

        // Target view (center)
        if (placeholder is not null)
        {
            _targetPanel.Markdown("");
        }
        else
        {
            _targetPanel.Markdown(GuiderActions.BuildTargetView(_state.GuideSamples));
        }

        // Stats panel (right)
        if (placeholder is not null)
        {
            _statsPanel.Markdown("");
        }
        else
        {
            _statsPanel.Markdown($"## Stats\n\n{GuiderActions.FormatStatsBlock(_state)}");
        }

        // Status bar
        var targetName = _state.ActiveObservation is { Target: var t } ? t.Name : "";
        _statusBar.Text(targetName.Length > 0 ? $" → {targetName}" : "");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    public override bool HandleInput(InputEvent evt)
    {
        // Read-only monitoring tab — no special input handling
        return false;
    }
}
