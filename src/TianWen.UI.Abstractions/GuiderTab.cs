using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic guider tab. Shows guide error graph (RA/Dec polylines),
    /// RMS stats panel, and placeholder states when not guiding.
    /// <para>
    /// Layout-driven (see <c>docs/plans/layout-driven-ui.md</c>): the whole frame + chrome (header,
    /// stats rows, panel titles, empty-state labels) is ONE <c>Layout</c> tree per frame; only the
    /// four raster panes (guide camera, star-profile plot, target scatter, error graph) draw pixels,
    /// each inside its keyed <c>Fill</c> leaf via the <c>drawFill</c> callback.
    /// </para>
    /// </summary>
    public class GuiderTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        // Layout constants (design units at 1x scale; the engine applies dpiScale)
        private static readonly float BaseFontSize = GuiTheme.Metrics.BaseFontSize;
        private static readonly float BasePadding = GuiTheme.Metrics.Padding;
        private const float BaseHeaderHeight = 32f;
        private const float BaseStatsWidth = 220f;
        private const float BaseProfileWidth = 120f;     // star-profile pane; right column = this + BaseStatsWidth
        private const float BaseGuiderLabelWidth = 200f; // header "[Guiding ...]" column
        private const float BaseStatsLabelWidth = 90f;   // stats-row label column
        private static readonly float BaseStatsLineH = BaseFontSize * 1.6f;

        // Fill keys routing the raster panes to their painters in the drawFill callback.
        private const string CameraFillKey = "guideCamera";
        private const string ProfileFillKey = "starProfile";
        private const string TargetFillKey = "targetView";
        private const string GraphFillKey = "guideGraph";

        // Colors. The palette-derived ones are PROPERTIES, not static readonly fields: a field
        // initialiser snapshots the palette at type-init, so this tab went on painting the startup
        // scheme through every theme switch. RGBAColor32 is a struct, so reading through allocates
        // nothing. What literals remain are CATEGORICAL series (index N, no severity to express)
        // or the two-trace exemption, each noted at its declaration.
        private static RGBAColor32 ContentBg => GuiTheme.Palette.ContentBg;
        private static RGBAColor32 PanelBg => GuiTheme.Palette.PanelBg;
        private static RGBAColor32 HeaderBg => GuiTheme.Palette.HeaderBg;
        private static RGBAColor32 HeaderText => GuiTheme.Palette.HeaderText;
        private static RGBAColor32 BodyText => GuiTheme.Palette.BodyText;
        private static RGBAColor32 DimText => GuiTheme.Palette.DimText;
        private static RGBAColor32 PlaceholderText => GuiTheme.Palette.DimText;
        private static RGBAColor32 AlertText => GuiTheme.Palette.Error;

        public GuiderTabState State { get; } = new GuiderTabState();

        /// <summary>
        /// The shared full image viewer (same widget as the FITS viewer) used to show the guide camera frame.
        /// Set by the host. Configured chromeless with a lightweight <see cref="LiveFramePreviewSource"/> feed;
        /// the guide-star crosshair + calibration L-shape are drawn by this tab ON TOP after the viewer renders.
        /// </summary>
        public ImageRendererBase<TSurface>? GuideCameraViewer { get; set; }

        /// <summary>
        /// The loop of the guide camera while no session runs (#1915), set by the host. With it the NoSession placeholder is a
        /// Loop / Stop button with the exposure and gain, and the pane shows the looped frames.
        /// </summary>
        public GuideLoopController? GuideLoop { get; set; }

        private static FormRowLayout.StepperStyle LoopStepperStyle => new(
            GuiTheme.NeutralButtonBg, BodyText, GuiTheme.NeutralButtonBg, DimText, BaseFontSize * 0.85f, 28f);

        /// <summary>Lightweight live-frame source feeding <see cref="GuideCameraViewer"/>.</summary>
        private readonly LiveFramePreviewSource _guideSource = new();

        /// <summary>Per-instance viewer state for the guide preview: chromeless, image-only, fit-to-window.</summary>
        private readonly ViewerState _guideState = new()
        {
            HideChrome = true,
            ShowInfoPanel = false,
            ShowFileList = false,
            ShowHistogram = false,
            ZoomToFit = true,
        };

        /// <summary>Tracks which guide frame reference is displayed to avoid redundant uploads.</summary>
        private Image? _displayedGuideFrame;
        private int _guideFrameCount;

        /// <summary>Arranged raster-pane rects from the last render, captured in the drawFill callback
        /// (default/empty when that pane showed its empty-state text leaf instead). Internal: the
        /// layout pins in <c>GuiderTabLayoutTests</c> read these instead of reflecting.</summary>
        internal RectF32 CameraRect { get; private set; }
        internal RectF32 ProfilePlotRect { get; private set; }
        internal RectF32 TargetViewRect { get; private set; }
        internal RectF32 GraphRect { get; private set; }

        public override bool HandleInput(InputEvent evt) => false;

        public void Render(
            LiveSessionState liveState,
            RectF32 contentRect,
            ITimeProvider timeProvider)
        {
            BeginFrame();
            // DPI comes from the inherited DpiScale (host-set); local alias keeps the px math unchanged.
            var dpiScale = DpiScale;
            State.PollFromLiveState(liveState);
            CameraRect = ProfilePlotRect = TargetViewRect = GraphRect = default;
            // A rig whose node has gone quiet says so in the header (P5b part 6): the graph and the frame are then only
            // the last the node sent.
            var quiet = RemoteRigActions.DescribeContact(liveState.Contact, binding: null, timeProvider.GetUtcNow());

            if (State.PlaceholderReason is GuiderPlaceholder.NoSession && GuideLoop is { } loop)
            {
                RenderLoop(loop, liveState, contentRect, quiet);
                return;
            }

            if (State.PlaceholderReason is { } reason)
            {
                RenderLayout(BuildPlaceholderTree(GuiderActions.PlaceholderText(reason), quiet),
                    contentRect);
                return;
            }

            // Feed a new guide frame to the shared viewer if changed. State sync, done BEFORE the
            // tree is built so the camera pane knows whether it has an image to show.
            // The frame is the guider's and is released once its successor lands, so AcceptFrame leases
            // it and refuses one already given back; only a frame actually copied in counts as shown.
            if (GuideCameraViewer is not null && State.LastGuideFrame is { } frame
                && !ReferenceEquals(frame, _displayedGuideFrame)
                && _guideSource.AcceptFrame(frame, freezeStats: false))
            {
                _displayedGuideFrame = frame;
                _guideFrameCount++;
                _guideState.NeedsTextureUpdate = true;
            }

            var fontSize = BaseFontSize * dpiScale;
            RenderLayout(BuildFrameTree(contentRect.Height / dpiScale, quiet), contentRect,
                drawFill: (fill, rect) =>
                {
                    switch (fill.Key)
                    {
                        case CameraFillKey:
                            CameraRect = rect;
                            RenderGuideCamera(rect, fontSize, _guideSource);
                            break;
                        case ProfileFillKey:
                            ProfilePlotRect = rect;
                            RenderStarProfilePlot(rect, fontSize);
                            break;
                        case TargetFillKey:
                            TargetViewRect = rect;
                            RenderTargetView(rect, fontSize);
                            break;
                        case GraphFillKey:
                            GraphRect = rect;
                            RenderGraph(rect, fontSize);
                            break;
                    }
                });
        }

        /// <summary>
        /// The tab with no session running and a loop to offer (#1915): a header with the Loop / Stop button, the exposure and
        /// the gain, and the guide camera's looped frames below, drawn by the same viewer the session's guide frames are.
        /// </summary>
        private void RenderLoop(GuideLoopController loop, LiveSessionState liveState, RectF32 contentRect, string? quiet)
        {
            var view = loop.View;
            var fresh = view.Tick();
            if (fresh)
            {
                _guideState.NeedsTextureUpdate = true;
            }
            // The chosen star is read in the frame the pane shows, whether or not a viewer is set to draw it.
            State.UpdateReadout(view.Source, fresh);
            var fontSize = BaseFontSize * DpiScale;
            var source = GuideCameraViewer is not null ? view.Source : null;

            var running = view.IsLive;
            var button = Layout.Builder.Text(running ? "Stop" : "Loop", BaseFontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center)
                .WFixed(72f).HStar().Bg(running ? GuiTheme.DangerButtonBg : GuiTheme.GoButtonBg)
                .BgHover(GuiTheme.Hover(running ? GuiTheme.DangerButtonBg : GuiTheme.GoButtonBg))
                .Clickable(new HitResult.ButtonHit("GuideLoopToggle"), _ =>
                {
                    if (running)
                    {
                        PostSignal(new StopGuideLoopSignal());
                    }
                    else
                    {
                        PostSignal(new StartGuideLoopSignal(loop.ExposureSeconds, loop.Gain));
                    }
                });
            var exposure = FormRowLayout.StepperControl(LoopStepperStyle,
                "-", "GuideLoopExpDec", _ => loop.StepExposure(-1),
                "+", "GuideLoopExpInc", _ => loop.StepExposure(+1),
                $"Exp: {LiveSessionActions.FormatExposureLabel(loop.ExposureSeconds)}", BaseFontSize * 0.85f, BodyText, enabled: true);
            var gain = FormRowLayout.StepperControl(LoopStepperStyle,
                "-", "GuideLoopGainDec", _ => loop.StepGain(-1),
                "+", "GuideLoopGainInc", _ => loop.StepGain(+1),
                loop.GainLabel, BaseFontSize * 0.85f, loop.Gain.HasValue ? BodyText : DimText, enabled: true);

            var status = view.State is { } s
                ? running
                    ? s.FramesPerSecond is { } fps && s.Width > 0 ? $"{fps:0.#} fps  {s.Width}x{s.Height}" : "Starting\u2026"
                    : s.FailureReason ?? ""
                : "";
            // Auto picks the brightest unsaturated isolated star, which a click on the frame overrides; Clear drops the choice.
            var auto = Layout.Builder.Text("Auto", BaseFontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center)
                .WFixed(56f).HStar().Bg(GuiTheme.NeutralButtonBg).BgHover(GuiTheme.Hover(GuiTheme.NeutralButtonBg))
                .Clickable(new HitResult.ButtonHit("GuideStarAuto"), _ => State.SelectAutoStar());
            var clear = Layout.Builder.Text("Clear", BaseFontSize * 0.9f, State.Selection is null ? DimText : BodyText, TextAlign.Center, TextAlign.Center)
                .WFixed(56f).HStar().Bg(GuiTheme.NeutralButtonBg).BgHover(GuiTheme.Hover(GuiTheme.NeutralButtonBg))
                .Clickable(new HitResult.ButtonHit("GuideStarClear"), _ => State.ClearSelection());

            var controls = Layout.Builder.HStack(button, exposure.WFixed(150f), gain.WFixed(150f), auto, clear,
                    Layout.Builder.Text(quiet ?? status, BaseFontSize * 0.85f, DimText).Stretch())
                .WithGap(BasePadding).Pad(BasePadding).Bg(HeaderBg);

            // A press on the frame picks the star nearest it, mapped through the viewer that drew the frame.
            var camera = (source is not null
                    ? Layout.Builder.Fill(key: CameraFillKey).Pressable(new HitResult.ButtonHit("GuideStarPick"), press =>
                    {
                        if (GuideCameraViewer is { } viewer)
                        {
                            var (x, y) = viewer.ScreenToFrame(_guideState, press.X, press.Y);
                            State.SelectStar(x, y);
                        }
                        return null;
                    }, CursorKind.Crosshair)
                    : Layout.Builder.Text(running ? "Waiting for guide frame\u2026" : "Press Loop to look through the guide camera",
                        BaseFontSize, DimText, TextAlign.Center, TextAlign.Center))
                .Stretch().Bg(CameraBg);

            var readoutPane = (State.Readout is not null
                    ? Layout.Builder.Fill(key: ProfileFillKey)
                    : Layout.Builder.Text("Awaiting data\u2026", BaseFontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center))
                .Stretch();
            var profile = Layout.Builder.VStack(
                    Layout.Builder.Text("Star Profile", BaseFontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Near).WStar(),
                    readoutPane)
                .Stretch().Pad(BasePadding).Bg(ProfileBg);

            var right = new List<Layout.Node> { Layout.Builder.HStack(profile, BuildReadoutPanel().ColW(BaseStatsWidth)).Stretch() };
            if (BuildGuideFocuserPanel(liveState) is { } focuser)
            {
                right.Add(focuser);
            }

            // The right column is there with no frame yet: the guide focuser needs no frame to be moved.
            var body = Layout.Builder.HStack(camera, Layout.Builder.VStack([.. right]).ColW(BaseProfileWidth + BaseStatsWidth)).Stretch();

            RenderLayout(Layout.Builder.VStack(controls.RowH(BaseHeaderHeight), body).Bg(ContentBg), contentRect,
                drawFill: (fill, rect) =>
                {
                    switch (fill.Key)
                    {
                        case CameraFillKey when source is not null:
                            CameraRect = rect;
                            RenderGuideCamera(rect, fontSize, source);
                            RenderSelection(rect);
                            break;
                        case ProfileFillKey when State.Readout is { } star:
                            ProfilePlotRect = rect;
                            StarProfilePlotRenderer.Render(Renderer, rect, star.HProfile, star.VProfile, DpiScale, FontPath, fontSize);
                            break;
                    }
                });
        }

        /// <summary>
        /// The chosen star's readout (#1915): HFD to minimise while focusing, SNR, the peak against the frame's full scale and
        /// the saturation in words (a clipped star reads sharper than it is, so it is said before it misleads the focus).
        /// </summary>
        private Layout.Node BuildReadoutPanel()
        {
            var rows = new List<Layout.Node>
            {
                StatsPitch(Layout.Builder.Text("Guide Star", BaseFontSize, HeaderText)),
                Layout.Builder.Spacer().RowH(BaseStatsLineH * 0.2f),
            };

            if (State.Selection is null)
            {
                rows.Add(StatsPitch(Layout.Builder.Text("Click a star in the frame", BaseFontSize * 0.9f, DimText)));
                rows.Add(StatsPitch(Layout.Builder.Text("or press Auto", BaseFontSize * 0.9f, DimText)));
            }
            else if (State.Readout is not { } star)
            {
                rows.Add(StatsPitch(Layout.Builder.Text("Star not found", BaseFontSize * 0.9f, AlertText)));
                rows.Add(StatsPitch(Layout.Builder.Text("Click another, or Auto", BaseFontSize * 0.9f, DimText)));
            }
            else
            {
                var saturation = star.Saturated ? AlertText : star.PeakFraction >= GuiderActions.NearSaturationShare ? GuiTheme.Palette.Warn : DimText;
                rows.Add(StatsRow("HFD:", double.IsNaN(star.Hfd) ? "\u2014" : $"{star.Hfd:F2} px"));
                rows.Add(StatsRow("SNR:", $"{star.Snr:F0}"));
                rows.Add(StatsRow("Peak:", GuiderActions.PeakText(star)));
                rows.Add(StatsRow("Position:", $"{star.X:F1}, {star.Y:F1}"));
                rows.Add(StatsPitch(Layout.Builder.Text(GuiderActions.SaturationWords(star), BaseFontSize * 0.9f, saturation)));
            }

            return Layout.Builder.VStack(CollectionsMarshal.AsSpan(rows)).Pad(BasePadding).Bg(PanelBg);
        }

        /// <summary>
        /// The guide focuser's controls, the Preview's own (<see cref="FocuserControls"/>), only while the profile has one
        /// assigned and connected; its moves are the Preview's signals aimed at <see cref="FocuserTargets.Guider"/>.
        /// </summary>
        private Layout.Node? BuildGuideFocuserPanel(LiveSessionState liveState)
        {
            if (State.GuideFocuser is not { } reading)
            {
                return null;
            }

            var rows = new List<Layout.Node>
            {
                StatsPitch(Layout.Builder.Text("Guide Focuser", BaseFontSize, HeaderText)),
            };
            rows.AddRange(FocuserControls.Rows("Guide", reading, liveState.GuideFocuserGotoInput,
                delta => PostSignal(new JogFocuserSignal(FocuserTargets.Guider, delta)),
                position => PostSignal(new GotoFocuserSignal(FocuserTargets.Guider, position)),
                BaseStatsLineH * 1.2f));
            return Layout.Builder.VStack(CollectionsMarshal.AsSpan(rows)).Pad(BasePadding).Bg(PanelBg).HAuto();
        }

        /// <summary>
        /// The chosen star's mark on the frame: the session's own crosshair (<see cref="DrawGuideStarCrosshair"/>) inside a box of
        /// the measuring aperture, both placed through the viewer's frame-to-screen mapping and never an origin of this tab's.
        /// The box turns to the alert colour while the star is clipped.
        /// </summary>
        private void RenderSelection(RectF32 rect)
        {
            if (GuideCameraViewer is not { } viewer || State.Selection is not { } selection)
            {
                return;
            }

            // The box is the aperture, so one near the frame's edge runs past the pane: clipped to it.
            PushClip(rect.X, rect.Y, rect.Width, rect.Height);

            var (sx, sy, zoom) = viewer.FrameToScreen(_guideState, selection.X, selection.Y);
            var half = (int)Math.Round(GuideStarReadout.Radius * zoom);
            var color = State.Readout is { Saturated: true } ? AlertText : State.Readout is null ? DimText : CrosshairColor;
            var cx = (int)sx;
            var cy = (int)sy;
            DrawLine(cx - half, cy - half, cx + half, cy - half, color);
            DrawLine(cx + half, cy - half, cx + half, cy + half, color);
            DrawLine(cx + half, cy + half, cx - half, cy + half, color);
            DrawLine(cx - half, cy + half, cx - half, cy - half, color);
            DrawGuideStarCrosshair(cx, cy);
            PopClip();
        }

        /// <summary>The small fixed crosshair with a centre gap, on the guide star: the session's mark before guiding, and the chosen star's while looping.</summary>
        private void DrawGuideStarCrosshair(int cx, int cy)
        {
            var crossLen = (int)(15 * DpiScale);
            var crossGap = (int)(4 * DpiScale);

            FillRect(cx - crossLen, cy, crossLen - crossGap, 1, CrosshairColor);
            FillRect(cx + crossGap, cy, crossLen - crossGap, 1, CrosshairColor);
            FillRect(cx, cy - crossLen, 1, crossLen - crossGap, CrosshairColor);
            FillRect(cx, cy + crossGap, 1, crossLen - crossGap, CrosshairColor);
        }

        /// <summary>
        /// The whole tab as one design-unit tree:
        /// <code>
        /// ┌─────────────────┬──────────┬────────┐
        /// │  Guide Camera   │ Profile  │ Stats  │  top half of right panel
        /// │  (large left)   ├──────────┴────────┤
        /// │                 │   Target View     │  bottom half of right panel
        /// ├─────────────────┴───────────────────┤
        /// │        Guide Error Graph            │
        /// └─────────────────────────────────────┘
        /// </code>
        /// Raster panes are keyed Fill leaves (painted in the drawFill callback); a pane with no
        /// data yet becomes a centred Text leaf instead, so empty states are layout too.
        /// </summary>
        private Layout.Node BuildFrameTree(float contentHDesign, string? quiet)
        {
            // Header: guider state + RMS. A lost guide star renders in alert red -- a silent
            // flatline with a healthy-looking header is how star loss went unnoticed before.
            var guiderLabelColor = State.GuiderState is "LostLock" ? AlertText : HeaderText;
            var headerItems = new List<Layout.Node>
            {
                Layout.Builder.Text($"[{FormatGuiderStateLabel()}]", BaseFontSize, guiderLabelColor)
                    .ColW(BaseGuiderLabelWidth),
            };
            if (quiet is not null)
            {
                headerItems.Add(Layout.Builder.Text(quiet, BaseFontSize * 0.9f, GuiTheme.Palette.Warn).Stretch());
            }
            headerItems.Add(Layout.Builder.Text(GuiderActions.FormatRmsSummary(State.LastGuideStats),
                BaseFontSize * 0.9f, BodyText, TextAlign.Far).Stretch());
            var header = Layout.Builder.HStack([.. headerItems]).Pad(BasePadding).Bg(HeaderBg);

            var camera = (GuideCameraViewer is not null && _displayedGuideFrame is not null
                    ? Layout.Builder.Fill(key: CameraFillKey)
                    : Layout.Builder.Text(State.IsRunning ? "Waiting for guide frame…" : "No guide camera",
                        BaseFontSize, DimText, TextAlign.Center, TextAlign.Center))
                .Stretch().Bg(CameraBg);

            var profilePlot = (State.GuideStarProfile is not null
                    ? Layout.Builder.Fill(key: ProfileFillKey)
                    : Layout.Builder.Text("Awaiting data…", BaseFontSize * 0.85f, DimText,
                        TextAlign.Center, TextAlign.Center))
                .Stretch();
            var profile = Layout.Builder.VStack(
                    Layout.Builder.Text("Star Profile", BaseFontSize * 0.85f, HeaderText,
                        TextAlign.Near, TextAlign.Near).WStar(),
                    profilePlot)
                .Stretch().Pad(BasePadding).Bg(ProfileBg);

            var samples = State.GuideSamples;
            var target = (samples.Length < 2 && State.CalibrationOverlay is null
                    ? Layout.Builder.Text("Target View", BaseFontSize, DimText, TextAlign.Center, TextAlign.Center)
                    : Layout.Builder.Fill(key: TargetFillKey))
                .Stretch().Bg(TargetBg);

            // Right panel: top half = profile + stats, bottom half = target view.
            var right = Layout.Builder.VStack(
                Layout.Builder.HStack(profile, BuildStatsPanel().ColW(BaseStatsWidth)).Stretch(),
                target);

            // The graph pane's background is painted by GuideGraphRenderer.Render itself (it owns its
            // paint), so the Fill leaf carries no .Bg -- adding one would just fill the same colour twice.
            var graph = samples.Length < 2
                ? Layout.Builder.Text("Waiting for guide data…", BaseFontSize, DimText,
                    TextAlign.Center, TextAlign.Center)
                : Layout.Builder.Fill(key: GraphFillKey);

            var bodyH = contentHDesign - BaseHeaderHeight;
            var graphH = MathF.Max(bodyH * 0.2f, 80f);

            return Layout.Builder.VStack(
                    header.RowH(BaseHeaderHeight),
                    Layout.Builder.HStack(camera, right.ColW(BaseProfileWidth + BaseStatsWidth)).Stretch(),
                    graph.RowH(graphH))
                .Bg(ContentBg);
        }

        /// <summary>Placeholder chrome: the reason in the header strip plus a large centred copy in
        /// the body, as one tree. A quiet rig's word (<paramref name="quiet"/>) takes the header, since the reason is then
        /// only what the node last said.</summary>
        private static Layout.Node BuildPlaceholderTree(string text, string? quiet)
            => Layout.Builder.Dock(
                    Layout.Builder.Text(text, BaseFontSize * 1.5f, PlaceholderText,
                        TextAlign.Center, TextAlign.Center).Stretch(),
                    Layout.Builder.Top(
                        Layout.Builder.HStack(
                                Layout.Builder.Text(quiet ?? text, BaseFontSize, quiet is null ? PlaceholderText : GuiTheme.Palette.Warn).Stretch())
                            .Pad(BasePadding).Bg(HeaderBg),
                        BaseHeaderHeight))
                .Bg(ContentBg);

        /// <summary>
        /// The "Guide Stats" panel as a tree: header + label/value rows with group gaps. Rows whose
        /// data is absent (no last error, no exposure, settle done) simply don't join the tree --
        /// the old cursor arithmetic becomes list building.
        /// </summary>
        private Layout.Node BuildStatsPanel()
        {
            var rows = new List<Layout.Node>
            {
                StatsPitch(Layout.Builder.Text("Guide Stats", BaseFontSize, HeaderText)),
                Layout.Builder.Spacer().RowH(BaseStatsLineH * 0.2f),
            };

            if (State.LastGuideStats is not { } stats)
            {
                rows.Add(StatsPitch(Layout.Builder.Text("No data", BaseFontSize * 0.9f, DimText)));
            }
            else
            {
                rows.Add(StatsRow("Total RMS:", $"{stats.TotalRMS:F2}\""));
                rows.Add(StatsRow("RA RMS:", $"{stats.RaRMS:F2}\"", GuideGraphRenderer.RaColor));
                rows.Add(StatsRow("Dec RMS:", $"{stats.DecRMS:F2}\"", GuideGraphRenderer.DecColor));
                rows.Add(Layout.Builder.Spacer().RowH(BaseStatsLineH * 0.3f));
                rows.Add(StatsRow("Peak RA:", $"{stats.PeakRa:F2}\""));
                rows.Add(StatsRow("Peak Dec:", $"{stats.PeakDec:F2}\""));
                rows.Add(Layout.Builder.Spacer().RowH(BaseStatsLineH * 0.3f));

                if (stats.LastRaErr.HasValue)
                {
                    rows.Add(StatsRow("Last RA:", $"{stats.LastRaErr.Value:+0.00;-0.00}\"", GuideGraphRenderer.RaColor));
                    rows.Add(StatsRow("Last Dec:", $"{stats.LastDecErr ?? 0:+0.00;-0.00}\"", GuideGraphRenderer.DecColor));
                    rows.Add(Layout.Builder.Spacer().RowH(BaseStatsLineH * 0.3f));
                }

                if (State.GuideExposure > TimeSpan.Zero)
                {
                    rows.Add(StatsRow("Exposure:", $"{State.GuideExposure.TotalSeconds:F1}s"));
                }

                if (State.GuiderSettleProgress is { Done: false } settle)
                {
                    rows.Add(StatsRow("Settle:", $"{settle.Distance:F2}\" / {settle.SettlePx:F2}\""));
                }
            }

            return Layout.Builder.VStack(CollectionsMarshal.AsSpan(rows)).Pad(BasePadding).Bg(PanelBg);
        }

        private static Layout.Node StatsRow(string label, string value, RGBAColor32? valueColor = null)
            => StatsPitch(
                Layout.Builder.Text(label, BaseFontSize * 0.9f, DimText).ColW(BaseStatsLabelWidth),
                Layout.Builder.Text(value, BaseFontSize * 0.9f, valueColor ?? BodyText).Stretch());

        // The stats are a table with a row PITCH, so each row is a container of that height with its text centred in it:
        // the text measures its own line box, and nothing here restates it.
        private static Layout.Node StatsPitch(params Layout.Node[] cells)
            => Layout.Builder.HStack(cells).RowH(BaseStatsLineH).CrossCenter();

        // Marks drawn ON the guide frame, not on chrome. The image is never recoloured (a sub of a faint
        // thing is already dark), so these sit on pixels whose brightness the theme does not control and
        // they stay fixed on purpose. RA/Dec additionally keep their hues by the same decision the guide
        // graph does: see docs/plans/colour-theme.md.
        private static RGBAColor32 CrosshairColor => GuiTheme.Palette.Accent.WithAlpha(0xaa);
        private static readonly RGBAColor32 CalRaColor = new RGBAColor32(0xff, 0x88, 0x22, 0xcc); // orange for RA
        private static readonly RGBAColor32 CalDecColor = new RGBAColor32(0x22, 0x88, 0xff, 0xcc); // blue for Dec
        private static readonly RGBAColor32 CalOriginColor = new RGBAColor32(0xff, 0xff, 0xff, 0xcc);
        private static RGBAColor32 CameraBg => GuiTheme.Palette.ContentBg;

        private void RenderGuideCamera(RectF32 rect, float fontSize, IPreviewSource source)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            // The frame tree only emits this Fill leaf when both are present (else the pane is a
            // centred empty-state Text leaf), so this is a belt-and-braces guard.
            if (GuideCameraViewer is not { } viewer || source.Width <= 0 || source.Height <= 0)
            {
                return;
            }

            viewer.SetSurfaceSize((uint)Renderer.Width, (uint)Renderer.Height);
            viewer.SetContentRegion(rect);
            // The texture upload NeedsTextureUpdate asks for happens inside Render's PrepareFrame, before
            // anything in the frame samples the channel views; see the remarks there.
            viewer.Render(source, _guideState);

            // Overlays are placed through the viewer's own frame-to-screen mapping (the one the star pick and the
            // selection box use), never an origin worked out here.
            var imageCentre = viewer.FrameToScreen(_guideState, (source.Width - 1) / 2.0, (source.Height - 1) / 2.0);

            // Guide-star overlay. During guiding, draw a full RA/Dec-aligned
            // reticle (orange RA / blue Dec) spanning the frame with a centre gap
            // so the star stays visible; before guiding, the small fixed crosshair.
            if (State.GuideStarPosition is var (starX, starY))
            {
                var (screenX, screenY, _) = viewer.FrameToScreen(_guideState, starX, starY);
                var cx = (int)screenX;
                var cy = (int)screenY;

                if (State.GuideSamples.Length >= 2 && State.CalibrationOverlay is { } reticleCal)
                {
                    RenderGuidingReticle(reticleCal, cx, cy, rect);
                }
                else
                {
                    DrawGuideStarCrosshair(cx, cy);
                }
            }

            // L-shaped calibration overlay: auto-scaled, centered on image.
            // Hide once guiding is active (guide samples accumulating).
            if (State.CalibrationOverlay is { } cal && State.GuideSamples.Length < 2)
            {
                RenderCalibrationOverlayOnCamera(cal, imageCentre.X, imageCentre.Y);
            }

            // SNR + frame count in corner
            var infoText = State.GuideStarSNR is { } snr
                ? $"SNR: {snr:F0}  #{_guideFrameCount}"
                : $"#{_guideFrameCount}";
            DrawText(infoText, fontPath,
                rect.X + 4 * dpiScale, rect.Y + rect.Height - fontSize * 1.4f,
                200 * dpiScale, fontSize * 1.2f,
                fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Far);
        }

        /// <summary>
        /// Full-frame RA/Dec reticle centred on the guide star, drawn during guiding. Arms run
        /// along the calibrated RA axis (orange) and the perpendicular Dec axis (blue), clipped
        /// to the camera rect, with a centre gap so the star itself stays visible. Persists for
        /// the whole guiding session as an orientation reference (the calibration L-shape only
        /// shows before guiding starts).
        /// </summary>
        private void RenderGuidingReticle(CalibrationOverlayData cal, int starCx, int starCy, RectF32 rect)
        {
            var dpiScale = DpiScale;
            var theta = cal.CameraAngleRad;
            var raUx = (float)Math.Cos(theta);
            var raUy = (float)Math.Sin(theta);
            // Dec axis = RA axis rotated 90deg on the sensor.
            var decUx = -raUy;
            var decUy = raUx;
            var gap = 14f * dpiScale;

            DrawReticleArm(starCx, starCy, raUx, raUy, rect, gap, CalRaColor);
            DrawReticleArm(starCx, starCy, -raUx, -raUy, rect, gap, CalRaColor);
            DrawReticleArm(starCx, starCy, decUx, decUy, rect, gap, CalDecColor);
            DrawReticleArm(starCx, starCy, -decUx, -decUy, rect, gap, CalDecColor);

            // Tiny centre marker so the exact lock position reads clearly inside the gap.
            FillRect(starCx - 1, starCy - 1, 3, 3, CrosshairColor);
        }

        /// <summary>
        /// Draws one reticle arm from the star (offset outward by <paramref name="gap"/>) along
        /// (ux, uy) to where the ray leaves <paramref name="rect"/> (slab method), so the arm
        /// always clips to the guide-camera area instead of bleeding into neighbouring panels.
        /// </summary>
        private void DrawReticleArm(int cx, int cy, float ux, float uy, RectF32 rect, float gap, RGBAColor32 color)
        {
            var tExit = float.MaxValue;
            if (MathF.Abs(ux) > 1e-4f)
            {
                var t1 = (rect.X - cx) / ux;
                var t2 = (rect.X + rect.Width - cx) / ux;
                tExit = MathF.Min(tExit, MathF.Max(t1, t2));
            }
            if (MathF.Abs(uy) > 1e-4f)
            {
                var t1 = (rect.Y - cy) / uy;
                var t2 = (rect.Y + rect.Height - cy) / uy;
                tExit = MathF.Min(tExit, MathF.Max(t1, t2));
            }
            if (tExit <= gap)
            {
                return; // star sits at the frame edge along this arm
            }

            var x0 = (int)(cx + ux * gap);
            var y0 = (int)(cy + uy * gap);
            var x1 = (int)(cx + ux * tExit);
            var y1 = (int)(cy + uy * tExit);
            DrawLine(x0, y0, x1, y1, color);
        }

        /// <summary>
        /// Renders auto-scaled calibration vectors on the guide camera image, centered on the guide star.
        /// The L-shape is scaled to fill a fixed screen area (~80px arms) so it's always visible
        /// regardless of sensor resolution or actual pixel displacement.
        /// </summary>
        private void RenderCalibrationOverlayOnCamera(
            CalibrationOverlayData cal,
            double centerX, double centerY)
        {
            var dpiScale = DpiScale;
            var scx = (int)centerX;
            var scy = (int)centerY;

            // Find max displacement across both arms to compute auto-scale
            var maxDisp = 1.0;
            foreach (var step in cal.RaSteps)
            {
                var dx = step.X - cal.RaOrigin.X;
                var dy = step.Y - cal.RaOrigin.Y;
                maxDisp = Math.Max(maxDisp, Math.Sqrt(dx * dx + dy * dy));
            }
            foreach (var step in cal.DecSteps)
            {
                var dx = step.X - cal.DecOrigin.X;
                var dy = step.Y - cal.DecOrigin.Y;
                maxDisp = Math.Max(maxDisp, Math.Sqrt(dx * dx + dy * dy));
            }

            // Scale so the longest arm is ~200 screen pixels
            var armLength = 200.0 * dpiScale;
            var autoScale = armLength / maxDisp;

            var dotR = Math.Max(2, (int)(2 * dpiScale));

            // RA arm (orange)
            var prevX = scx;
            var prevY = scy;
            foreach (var step in cal.RaSteps)
            {
                var dx = (step.X - cal.RaOrigin.X) * autoScale;
                var dy = (step.Y - cal.RaOrigin.Y) * autoScale;
                var sx = scx + (int)dx;
                var sy = scy + (int)dy;
                DrawLineOverlay(prevX, prevY, sx, sy, CalRaColor);
                FillRect(sx - dotR, sy - dotR, dotR * 2 + 1, dotR * 2 + 1, CalRaColor);
                prevX = sx;
                prevY = sy;
            }

            // Dec arm (blue)
            prevX = scx;
            prevY = scy;
            foreach (var step in cal.DecSteps)
            {
                var dx = (step.X - cal.DecOrigin.X) * autoScale;
                var dy = (step.Y - cal.DecOrigin.Y) * autoScale;
                var sx = scx + (int)dx;
                var sy = scy + (int)dy;
                DrawLineOverlay(prevX, prevY, sx, sy, CalDecColor);
                FillRect(sx - dotR, sy - dotR, dotR * 2 + 1, dotR * 2 + 1, CalDecColor);
                prevX = sx;
                prevY = sy;
            }

            // Origin dot (white)
            FillRect(scx - dotR, scy - dotR, dotR * 2 + 1, dotR * 2 + 1, CalOriginColor);
        }

        private string FormatGuiderStateLabel()
        {
            var state = State.GuiderState;
            if (state is "LostLock")
            {
                return "GUIDE STAR LOST";
            }
            if (state is "Settling" && State.GuiderSettleProgress is { Distance: var dist } && !double.IsNaN(dist))
            {
                return $"Settling {dist:F2}px";
            }
            if (state is "Settling")
            {
                return "Settling";
            }
            if (state is "Stopped" && State.Phase is SessionPhase.Observing)
            {
                return "Paused (Slewing)";
            }
            if (state is "Guiding" && State.LastGuideStats is { } gs)
            {
                var ra = FormatPulse(gs.LastRaPulseMs, "←", "→"); // ← West, → East
                var dec = FormatPulse(gs.LastDecPulseMs, "↓", "↑"); // ↓ South, ↑ North
                if (ra.Length > 0 || dec.Length > 0)
                {
                    return $"Guiding {ra}{(ra.Length > 0 && dec.Length > 0 ? " " : "")}{dec}";
                }
            }
            return state ?? "Guiding";

            static string FormatPulse(double? pulseMs, string negArrow, string posArrow)
            {
                if (pulseMs is not { } p || p == 0) return "";
                var arrow = p > 0 ? posArrow : negArrow;
                return $"{arrow}{Math.Abs(p):F0}ms";
            }
        }

        /// <summary>
        /// Renders calibration summary text in the target view area: angle, rates, ortho error, backlash.
        /// </summary>
        private void RenderCalibrationText(
            CalibrationOverlayData cal,
            RectF32 rect, float fontSize)
        {
            var fontPath = FontPath;
            var padding = BasePadding * DpiScale;
            var lineH = fontSize * 1.3f;
            var labelW = rect.Width * 0.55f;
            var valueW = rect.Width * 0.4f;
            var x = rect.X + padding;
            var y = rect.Y + rect.Height - padding; // anchor from bottom

            var smallFont = fontSize * 0.7f;

            // Build lines bottom-up so they sit above the ±12" label
            var lines = new (string Label, string Value, RGBAColor32 Color)[]
            {
                ("Angle:", $"{cal.CameraAngleDeg:F1}°", BodyText),
                ("RA rate:", $"{cal.RaRateArcsecPerSec:F2}\"/s", CalRaColor),
                ("Dec rate:", $"{cal.DecRateArcsecPerSec:F2}\"/s", CalDecColor),
                ("Ortho err:", $"{cal.OrthoErrorDeg:F1}°", cal.OrthoErrorDeg > 5 ? CalRaColor : BodyText),
                ("BL RA:", cal.BacklashClearingStepsRa > 0 ? $"{cal.BacklashClearingStepsRa} steps" : "off", DimText),
                ("BL Dec:", cal.BacklashClearingStepsDec > 0 ? $"{cal.BacklashClearingStepsDec} steps" : "off", DimText),
            };

            y -= lines.Length * lineH;
            foreach (var (label, value, color) in lines)
            {
                DrawText(label, fontPath, x, y, labelW, lineH, smallFont, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(value, fontPath, x + labelW, y, valueW, lineH, smallFont, color, TextAlign.Near, TextAlign.Center);
                y += lineH;
            }
        }

        /// <summary>
        /// Draws a 1px line between two points via the renderer's DrawLine primitive.
        /// </summary>
        private void DrawLineOverlay(int x0, int y0, int x1, int y1, RGBAColor32 color)
            => DrawLine(x0, y0, x1, y1, color);


        private static RGBAColor32 ProfileBg => GuiTheme.Palette.ContentBg;

        /// <summary>
        /// Star profile: 1D intensity cross-section through the guide-star centre (horizontal + vertical
        /// overlaid, each with a Gaussian fit). Delegates to the paint-owning
        /// <see cref="StarProfilePlotRenderer"/>; receives the PLOT rect (the pane's padding + title row
        /// are layout).
        /// </summary>
        private void RenderStarProfilePlot(RectF32 rect, float fontSize)
        {
            if (State.GuideStarProfile is not var (hProfile, vProfile))
            {
                return; // the frame tree only emits this Fill leaf with profile data present
            }

            StarProfilePlotRenderer.Render(Renderer, rect, hProfile, vProfile, DpiScale, FontPath, fontSize);
        }

        private static RGBAColor32 TargetBg => GuiTheme.Palette.ContentBg;

        /// <summary>
        /// PHD2-style target view: 2D scatter of RA (X) vs Dec (Y) error with RMS circle. Delegates the
        /// scatter chart to the paint-owning <see cref="GuideScatterRenderer"/>; the calibration summary
        /// text (a readout, not the chart) stays here and overlays the pane while calibrating.
        /// </summary>
        private void RenderTargetView(RectF32 rect, float fontSize)
        {
            GuideScatterRenderer.Render(Renderer, rect, State.GuideSamples, State.LastGuideStats, DpiScale, FontPath, fontSize);

            // Calibration data text (angle, rates, backlash) -- hide once guiding starts
            if (State.CalibrationOverlay is { } cal && State.GuideSamples.Length < 2)
            {
                RenderCalibrationText(cal, rect, fontSize);
            }
        }


        /// <summary>
        /// Full PHD2-style guide graph (axis labels + RA/Dec legend). Delegates to the shared
        /// paint-owning <see cref="GuideGraphRenderer.Render{TSurface}"/>; the compact strip in
        /// <see cref="LiveSessionTab{TSurface}"/> is the same control with labels + legend off.
        /// </summary>
        private void RenderGraph(RectF32 rect, float fontSize)
            => GuideGraphRenderer.Render(Renderer, rect, State.GuideSamples, State.LastGuideStats,
                DpiScale, FontPath, fontSize, showAxisLabels: true, showLegend: true);
    }
}
