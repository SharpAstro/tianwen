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

        // Colors
        private static readonly RGBAColor32 ContentBg = GuiTheme.Palette.ContentBg;
        private static readonly RGBAColor32 PanelBg = GuiTheme.Palette.PanelBg;
        private static readonly RGBAColor32 HeaderBg = GuiTheme.Palette.HeaderBg;
        private static readonly RGBAColor32 HeaderText = GuiTheme.Palette.HeaderText;
        private static readonly RGBAColor32 BodyText = GuiTheme.Palette.BodyText;
        private static readonly RGBAColor32 DimText = GuiTheme.Palette.DimText;
        private static readonly RGBAColor32 PlaceholderText = new RGBAColor32(0x66, 0x66, 0x88, 0xff);
        private static readonly RGBAColor32 AlertText = new RGBAColor32(0xff, 0x55, 0x44, 0xff);

        public GuiderTabState State { get; } = new GuiderTabState();

        /// <summary>
        /// The shared full image viewer (same widget as the FITS viewer) used to show the guide camera frame.
        /// Set by the host. Configured chromeless with a lightweight <see cref="LiveFramePreviewSource"/> feed;
        /// the guide-star crosshair + calibration L-shape are drawn by this tab ON TOP after the viewer renders.
        /// </summary>
        public ImageRendererBase<TSurface>? GuideCameraViewer { get; set; }

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

            if (State.PlaceholderReason is { } reason)
            {
                RenderLayout(BuildPlaceholderTree(GuiderActions.PlaceholderText(reason)),
                    contentRect);
                return;
            }

            // Feed a new guide frame to the shared viewer if changed. State sync, done BEFORE the
            // tree is built so the camera pane knows whether it has an image to show.
            if (GuideCameraViewer is not null && State.LastGuideFrame is { } frame
                && !ReferenceEquals(frame, _displayedGuideFrame))
            {
                _displayedGuideFrame = frame;
                _guideFrameCount++;
                _guideSource.AcceptFrame(frame, freezeStats: false);
                _guideState.NeedsTextureUpdate = true;
            }

            var fontSize = BaseFontSize * dpiScale;
            RenderLayout(BuildFrameTree(contentRect.Height / dpiScale), contentRect,
                drawFill: (fill, rect) =>
                {
                    switch (fill.Key)
                    {
                        case CameraFillKey:
                            CameraRect = rect;
                            RenderGuideCamera(rect, fontSize);
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
        private Layout.Node BuildFrameTree(float contentHDesign)
        {
            // Header: guider state + RMS. A lost guide star renders in alert red -- a silent
            // flatline with a healthy-looking header is how star loss went unnoticed before.
            var guiderLabelColor = State.GuiderState is "LostLock" ? AlertText : HeaderText;
            var header = Layout.Builder.HStack(
                    Layout.Builder.Text($"[{FormatGuiderStateLabel()}]", BaseFontSize, guiderLabelColor)
                        .ColW(BaseGuiderLabelWidth),
                    Layout.Builder.Text(GuiderActions.FormatRmsSummary(State.LastGuideStats),
                        BaseFontSize * 0.9f, BodyText, TextAlign.Far).Stretch())
                .Pad(BasePadding).Bg(HeaderBg);

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
                        TextAlign.Near, TextAlign.Near).RowH(BaseFontSize * 1.4f),
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
        /// the body, as one tree.</summary>
        private static Layout.Node BuildPlaceholderTree(string text)
            => Layout.Builder.Dock(
                    Layout.Builder.Text(text, BaseFontSize * 1.5f, PlaceholderText,
                        TextAlign.Center, TextAlign.Center).Stretch(),
                    Layout.Builder.Top(
                        Layout.Builder.HStack(
                                Layout.Builder.Text(text, BaseFontSize, PlaceholderText).Stretch())
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
                Layout.Builder.Text("Guide Stats", BaseFontSize, HeaderText).RowH(BaseStatsLineH),
                Layout.Builder.Spacer().RowH(BaseStatsLineH * 0.2f),
            };

            if (State.LastGuideStats is not { } stats)
            {
                rows.Add(Layout.Builder.Text("No data", BaseFontSize * 0.9f, DimText).RowH(BaseStatsLineH));
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

            static Layout.Node StatsRow(string label, string value, RGBAColor32? valueColor = null)
                => Layout.Builder.HStack(
                        Layout.Builder.Text(label, BaseFontSize * 0.9f, DimText).ColW(BaseStatsLabelWidth),
                        Layout.Builder.Text(value, BaseFontSize * 0.9f, valueColor ?? BodyText).Stretch())
                    .RowH(BaseStatsLineH);
        }

        private static readonly RGBAColor32 CrosshairColor = new RGBAColor32(0x00, 0xff, 0x00, 0xaa);
        private static readonly RGBAColor32 CalRaColor = new RGBAColor32(0xff, 0x88, 0x22, 0xcc); // orange for RA
        private static readonly RGBAColor32 CalDecColor = new RGBAColor32(0x22, 0x88, 0xff, 0xcc); // blue for Dec
        private static readonly RGBAColor32 CalOriginColor = new RGBAColor32(0xff, 0xff, 0xff, 0xcc);
        private static readonly RGBAColor32 CameraBg = new RGBAColor32(0x0a, 0x0a, 0x0a, 0xff);

        private void RenderGuideCamera(RectF32 rect, float fontSize)
        {
            var dpiScale = DpiScale;
            var fontPath = FontPath;
            // The frame tree only emits this Fill leaf when both are present (else the pane is a
            // centred empty-state Text leaf), so this is a belt-and-braces guard.
            if (GuideCameraViewer is not { } viewer || _displayedGuideFrame is not { } image)
            {
                return;
            }

            viewer.SetSurfaceSize((uint)Renderer.Width, (uint)Renderer.Height);
            viewer.SetContentRegion(rect);
            if (_guideState.NeedsTextureUpdate && _guideSource.Width > 0)
            {
                viewer.UploadDocumentTextures(_guideSource, _guideState);
            }
            viewer.Render(_guideSource, _guideState);

            // Compute image→screen transform for overlays
            var imgW = image.Width;
            var imgH = image.Height;
            var fitScale = Math.Min(rect.Width / imgW, rect.Height / imgH);
            var drawW = imgW * fitScale;
            var drawH = imgH * fitScale;
            var offsetX = rect.X + (rect.Width - drawW) / 2;
            var offsetY = rect.Y + (rect.Height - drawH) / 2;

            // Guide-star overlay. During guiding, draw a full RA/Dec-aligned
            // reticle (orange RA / blue Dec) spanning the frame with a centre gap
            // so the star stays visible; before guiding, the small fixed crosshair.
            if (State.GuideStarPosition is var (starX, starY))
            {
                var cx = (int)(offsetX + starX * fitScale);
                var cy = (int)(offsetY + starY * fitScale);

                if (State.GuideSamples.Length >= 2 && State.CalibrationOverlay is { } reticleCal)
                {
                    RenderGuidingReticle(reticleCal, cx, cy, rect);
                }
                else
                {
                    var crossLen = (int)(15 * dpiScale);
                    var crossGap = (int)(4 * dpiScale);

                    FillRect(cx - crossLen, cy, crossLen - crossGap, 1, CrosshairColor);
                    FillRect(cx + crossGap, cy, crossLen - crossGap, 1, CrosshairColor);
                    FillRect(cx, cy - crossLen, 1, crossLen - crossGap, CrosshairColor);
                    FillRect(cx, cy + crossGap, 1, crossLen - crossGap, CrosshairColor);
                }
            }

            // L-shaped calibration overlay: auto-scaled, centered on image.
            // Hide once guiding is active (guide samples accumulating).
            if (State.CalibrationOverlay is { } cal && State.GuideSamples.Length < 2)
            {
                RenderCalibrationOverlayOnCamera(cal,
                    offsetX + drawW / 2, offsetY + drawH / 2);
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


        private static readonly RGBAColor32 ProfileBg = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);

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

        private static readonly RGBAColor32 TargetBg = new RGBAColor32(0x10, 0x10, 0x18, 0xff);

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
