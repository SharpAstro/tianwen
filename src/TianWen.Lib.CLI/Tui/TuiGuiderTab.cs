using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.UI.Abstractions;

namespace TianWen.Lib.CLI.Tui;

/// <summary>
/// TUI guider tab. On Sixel-capable terminals, renders the full graphical guider
/// (error graph, target scatter, star profile, stats) via <see cref="GuiderTab{TSurface}"/>
/// into a <see cref="SixelRgbaImageRenderer"/>. Falls back to text sparklines otherwise.
/// </summary>
internal sealed class TuiGuiderTab(
    GuiAppState appState,
    LiveSessionState liveState,
    IVirtualTerminal terminal,
    string fontPath,
    TimeProvider timeProvider) : TuiTabBase
{
    private const int SparklineWidth = 40;

    private readonly GuiderTabState _state = new GuiderTabState();

    private TextBar? _topBar;
    private TextBar? _statusBar;

    // Sixel mode: single canvas with full graphical guider
    private Canvas? _canvas;
    private SixelRgbaImageRenderer? _canvasRenderer;
    private GuiderTab<RgbaImage>? _guiderWidget;

    // Text fallback: markdown panels for non-Sixel terminals
    private MarkdownWidget? _graphPanel;
    private MarkdownWidget? _targetPanel;
    private MarkdownWidget? _statsPanel;

    private bool UseSixel => terminal.HasSixelSupport;

    [MemberNotNullWhen(true, nameof(_topBar), nameof(_statusBar))]
    protected override bool IsReady => _topBar is not null && _statusBar is not null
        && (UseSixel
            ? _canvas is not null && _canvasRenderer is not null && _guiderWidget is not null
            : _graphPanel is not null && _targetPanel is not null && _statsPanel is not null);

    protected override void CreateWidgets(Panel panel)
    {
        var topVp = panel.Dock(DockStyle.Top, 1);
        var bottomVp = panel.Dock(DockStyle.Bottom, 1);

        _topBar = new TextBar(topVp);
        _statusBar = new TextBar(bottomVp);

        if (UseSixel)
        {
            var fillVp = panel.Fill();
            var canvasPixelSize = fillVp.PixelSize;
            _canvasRenderer = new SixelRgbaImageRenderer((uint)canvasPixelSize.Width, (uint)canvasPixelSize.Height);
            _canvas = new Canvas(fillVp, _canvasRenderer);
            _guiderWidget = new GuiderTab<RgbaImage>(_canvasRenderer);

            panel.Add(_topBar).Add(_statusBar).Add(_canvas);
        }
        else
        {
            var leftVp = panel.Dock(DockStyle.Left, 44);
            var centerVp = panel.Dock(DockStyle.Left, 24);
            var fillVp = panel.Fill();

            _graphPanel = new MarkdownWidget(leftVp);
            _targetPanel = new MarkdownWidget(centerVp);
            _statsPanel = new MarkdownWidget(fillVp);

            panel.Add(_topBar).Add(_statusBar).Add(_graphPanel).Add(_targetPanel).Add(_statsPanel);
        }
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

        // Main content
        if (UseSixel && _canvas is not null && _canvasRenderer is not null && _guiderWidget is not null)
        {
            RenderSixelContent();
        }
        else
        {
            RenderTextContent(placeholder);
        }

        // Status bar
        var targetName = _state.ActiveObservation is { Target: var t } ? t.Name : "";
        _statusBar.Text(targetName.Length > 0 ? $" \u2192 {targetName}" : "");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    private void RenderSixelContent()
    {
        var canvasPixelSize = _canvas!.PixelSize;
        var contentRect = new RectF32(0, 0, canvasPixelSize.Width, canvasPixelSize.Height);

        _guiderWidget!.Render(liveState, contentRect, 1.0f, fontPath, timeProvider);
    }

    private void RenderTextContent(GuiderPlaceholder? placeholder)
    {
        // Graph panel (left) — sparklines
        if (placeholder is not null)
        {
            _graphPanel!.Markdown($"## Guider\n\n{GuiderActions.PlaceholderText(placeholder.Value)}");
        }
        else
        {
            var (raSpark, decSpark, raRange, decRange) = GuiderActions.BuildGuideSparklines(_state.GuideSamples, SparklineWidth);
            _graphPanel!.Markdown(
                $"## RA Error ({raRange})\n\n" +
                $"{raSpark}\n\n" +
                $"## Dec Error ({decRange})\n\n" +
                $"{decSpark}");
        }

        // Target view (center)
        if (placeholder is not null)
        {
            _targetPanel!.Markdown("");
        }
        else
        {
            _targetPanel!.Markdown(GuiderActions.BuildTargetView(_state.GuideSamples));
        }

        // Stats panel (right)
        if (placeholder is not null)
        {
            _statsPanel!.Markdown("");
        }
        else
        {
            _statsPanel!.Markdown($"## Stats\n\n{GuiderActions.FormatStatsBlock(_state)}");
        }
    }

    public override bool HandleInput(InputEvent evt)
    {
        // Read-only monitoring tab — no special input handling
        return false;
    }
}
