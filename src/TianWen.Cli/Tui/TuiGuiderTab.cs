using System.Diagnostics.CodeAnalysis;
using Console.Lib;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.UI.Abstractions;

namespace TianWen.Cli.Tui;

/// <summary>
/// TUI guider tab. On Sixel-capable terminals, renders the full graphical guider
/// (error graph, target scatter, star profile, stats) via <see cref="GuiderTab{TSurface}"/>
/// into a <see cref="SixelRgbaImageRenderer"/>. Falls back to text sparklines otherwise.
/// </summary>
internal sealed class TuiGuiderTab(
    GuiAppState appState,
    ViewContexts contexts,
    IVirtualTerminal terminal,
    string fontPath,
    ITimeProvider timeProvider) : TuiTabBase
{
    /// <summary>The on-screen context's session state. Resolved per use, never cached in a field --
    /// the active context can change between frames.</summary>
    private LiveSessionState LiveState => contexts.Active.LiveSession;

    private const int SparklineWidth = 40;

    private readonly GuiderTabState _state = new GuiderTabState();

    private TextBar? _topBar;
    private TextBar? _statusBar;

    // Sixel mode: single canvas with full graphical guider. All three are built on first placement and
    // rebuilt on resize (PaintHost), because their pixel size is only known once the tree is arranged.
    // Interface-typed: PixelSize is a default interface member, so it is only reachable through
    // ITerminalViewport, and this is the one place the tab needs it.
    private ITerminalViewport? _canvasViewport;
    private Canvas? _canvas;
    private SixelRgbaImageRenderer? _canvasRenderer;
    private GuiderTab<RgbaImage>? _guiderWidget;

    // Text fallback: markdown panels for non-Sixel terminals
    private MarkdownWidget? _graphPanel;
    private MarkdownWidget? _targetPanel;
    private MarkdownWidget? _statsPanel;

    private bool UseSixel => terminal.HasSixelSupport;

    /// <summary>
    /// Readiness covers only what <see cref="CreateWidgets"/> builds. The Sixel canvas is deliberately
    /// absent: it cannot exist before the first arrange, so requiring it here would stop the tab ever
    /// rendering the frame that would size it.
    /// </summary>
    [MemberNotNullWhen(true, nameof(_topBar), nameof(_statusBar))]
    protected override bool IsReady => _topBar is not null && _statusBar is not null
        && (UseSixel || (_graphPanel is not null && _targetPanel is not null && _statsPanel is not null));

    // Fill-leaf keys. The tree names these; PaintHost draws them.
    private const string TopKey = "top";
    private const string StatusKey = "status";
    private const string CanvasKey = "canvas";
    private const string GraphKey = "graph";
    private const string TargetKey = "target";
    private const string StatsKey = "stats";

    protected override void CreateWidgets()
    {
        _topBar = new TextBar(Host(TopKey));
        _statusBar = new TextBar(Host(StatusKey));

        if (UseSixel)
        {
            // Only the viewport. The renderer, canvas and guider widget all need a pixel size, which
            // comes from the arranged rect -- PaintHost builds them on first placement and on resize.
            _canvasViewport = Host(CanvasKey);
        }
        else
        {
            _graphPanel = new MarkdownWidget(Host(GraphKey));
            _targetPanel = new MarkdownWidget(Host(TargetKey));
            _statsPanel = new MarkdownWidget(Host(StatsKey));
        }
    }

    /// <summary>
    /// The arrangement: a one-row bar top and bottom with the content between them. The Sixel and text
    /// variants are now two expressions of one tree rather than two widget-construction paths -- and
    /// because the tree is rebuilt per frame, the fallback's fixed 44/24 columns can give way to Star
    /// sizing on a narrow terminal without touching widget setup.
    /// </summary>
    protected override Layout.Node BuildLayout() =>
        Layout.Builder.VStack(
            Layout.Builder.Fill(key: TopKey).RowH(1),
            UseSixel
                ? Layout.Builder.Fill(key: CanvasKey).Stretch()
                // ColW (Width=Fixed, Height=Star), not WFixed: in an HStack the cross axis is the height, and
                // a Fill leaf left on Auto height measures its MinHeight -- zero.
                : Layout.Builder.HStack(
                    Layout.Builder.Fill(key: GraphKey).ColW(44),
                    Layout.Builder.Fill(key: TargetKey).ColW(24),
                    Layout.Builder.Fill(key: StatsKey).Stretch()).Stretch(),
            Layout.Builder.Fill(key: StatusKey).RowH(1));

    /// <summary>
    /// Draws one hosted widget. The Sixel canvas is the reason <paramref name="geometryChanged"/> exists:
    /// its renderer is a fixed-size pixel buffer, so it is allocated on first placement and reallocated
    /// whenever the cell rect resizes. The Panel-based version sized it once at construction and never
    /// again, so a terminal resize left the guider rendering at the old pixel size.
    /// </summary>
    protected override void PaintHost(string key, Rect<int> rect, bool geometryChanged)
    {
        switch (key)
        {
            case TopKey:
                _topBar?.Render();
                break;

            case StatusKey:
                _statusBar?.Render();
                break;

            case CanvasKey when _canvasViewport is { } viewport:
                if (geometryChanged)
                {
                    var (pixelWidth, pixelHeight) = viewport.PixelSize;
                    _canvasRenderer?.Dispose();
                    _canvasRenderer = new SixelRgbaImageRenderer(pixelWidth, pixelHeight);
                    _guiderWidget = new GuiderTab<RgbaImage>(_canvasRenderer) { FontPath = fontPath };
                    _canvas = new Canvas(viewport, _canvasRenderer);
                }

                if (_canvas is { } canvas && _guiderWidget is { } guiderWidget)
                {
                    // A terminal Sixel canvas has no DPI scaling; the widget's DpiScale stays at its
                    // default 1. FontPath was set when the widget was built, not passed per render.
                    var (pixelWidth, pixelHeight) = canvas.PixelSize;
                    guiderWidget.Render(LiveState, new RectF32(0, 0, pixelWidth, pixelHeight), timeProvider);
                    canvas.Render();
                }
                break;

            case GraphKey:
                _graphPanel?.Render();
                break;

            case TargetKey:
                _targetPanel?.Render();
                break;

            case StatsKey:
                _statsPanel?.Render();
                break;
        }
    }

    protected override void RenderContent()
    {
        if (!IsReady) return;

        _state.PollFromLiveState(LiveState);

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

        // Text-fallback content. The Sixel path has no data step -- the guider widget draws straight from
        // LiveState during PaintHost, once its canvas has been sized by the arrange.
        // The null-checked widgets flow into the helper as parameters so the non-null guarantee travels
        // with them -- no null-forgiving '!' re-derefs inside.
        if (!UseSixel && _graphPanel is { } graphPanel && _targetPanel is { } targetPanel && _statsPanel is { } statsPanel)
        {
            RenderTextContent(placeholder, graphPanel, targetPanel, statsPanel);
        }

        // Status bar
        var targetName = _state.ActiveObservation is { Target: var t } ? t.Name : "";
        _statusBar.Text(targetName.Length > 0 ? $" \u2192 {targetName}" : "");
        _statusBar.RightText(appState.StatusMessage ?? "");
    }

    private void RenderTextContent(GuiderPlaceholder? placeholder,
        MarkdownWidget graphPanel, MarkdownWidget targetPanel, MarkdownWidget statsPanel)
    {
        // Graph panel (left): sparklines
        if (placeholder is not null)
        {
            graphPanel.Markdown($"## Guider\n\n{GuiderActions.PlaceholderText(placeholder.Value)}");
        }
        else
        {
            var (raSpark, decSpark, raRange, decRange) = GuiderActions.BuildGuideSparklines(_state.GuideSamples, SparklineWidth);
            graphPanel.Markdown(
                $"## RA Error ({raRange})\n\n" +
                $"{raSpark}\n\n" +
                $"## Dec Error ({decRange})\n\n" +
                $"{decSpark}");
        }

        // Target view (center)
        if (placeholder is not null)
        {
            targetPanel.Markdown("");
        }
        else
        {
            targetPanel.Markdown(GuiderActions.BuildTargetView(_state.GuideSamples));
        }

        // Stats panel (right)
        if (placeholder is not null)
        {
            statsPanel.Markdown("");
        }
        else
        {
            statsPanel.Markdown($"## Stats\n\n{GuiderActions.FormatStatsBlock(_state)}");
        }
    }

    protected override void HandleTabInput(InputEvent evt){
        // Read-only monitoring tab: no special input handling
        return;
    }
}
