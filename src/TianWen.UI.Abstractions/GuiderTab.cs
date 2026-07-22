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
            string fontPath,
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
                    contentRect, fontPath);
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
            RenderLayout(BuildFrameTree(contentRect.Height / dpiScale), contentRect, fontPath,
                drawFill: (fill, rect) =>
                {
                    switch (fill.Key)
                    {
                        case CameraFillKey:
                            CameraRect = rect;
                            RenderGuideCamera(rect, fontPath, fontSize);
                            break;
                        case ProfileFillKey:
                            ProfilePlotRect = rect;
                            RenderStarProfilePlot(rect, fontPath, fontSize);
                            break;
                        case TargetFillKey:
                            TargetViewRect = rect;
                            RenderTargetView(rect, fontPath, fontSize);
                            break;
                        case GraphFillKey:
                            GraphRect = rect;
                            RenderGraph(rect, fontPath, fontSize);
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

            var graph = samples.Length < 2
                ? Layout.Builder.Text("Waiting for guide data…", BaseFontSize, DimText,
                    TextAlign.Center, TextAlign.Center)
                : Layout.Builder.Fill(key: GraphFillKey).Bg(GuideGraphRenderer.GraphBg);

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

        private void RenderGuideCamera(RectF32 rect, string fontPath, float fontSize)
        {
            var dpiScale = DpiScale;
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
            RectF32 rect, string fontPath, float fontSize)
        {
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
        private static readonly RGBAColor32 ProfileLineColor = new RGBAColor32(0x44, 0x99, 0x44, 0x88);
        private static readonly RGBAColor32 ProfileVLineColor = new RGBAColor32(0x44, 0x88, 0x99, 0x88);
        private static readonly RGBAColor32 ProfileFitColor = new RGBAColor32(0x66, 0xff, 0x66, 0xff);
        private static readonly RGBAColor32 ProfileVFitColor = new RGBAColor32(0x66, 0xdd, 0xff, 0xff);

        /// <summary>
        /// Star profile: 1D intensity cross-section through the guide star center.
        /// Shows horizontal (green) and vertical (cyan) profiles overlaid.
        /// Receives the PLOT rect (the pane's padding + title row are layout); the FWHM readout and
        /// the legend draw over the plot's top and bottom bands.
        /// </summary>
        private void RenderStarProfilePlot(RectF32 rect, string fontPath, float fontSize)
        {
            var dpiScale = DpiScale;
            if (State.GuideStarProfile is not var (hProfile, vProfile))
            {
                return; // the frame tree only emits this Fill leaf with profile data present
            }

            var plotX = rect.X;
            var plotY = rect.Y;
            var plotW = rect.Width;
            var plotH = rect.Height;

            if (plotW < 10 || plotH < 10) return;

            // Find max value across both profiles for shared Y scale
            var maxVal = 1f;
            for (var i = 0; i < hProfile.Length; i++)
            {
                if (hProfile[i] > maxVal) maxVal = hProfile[i];
            }
            for (var i = 0; i < vProfile.Length; i++)
            {
                if (vProfile[i] > maxVal) maxVal = vProfile[i];
            }

            // Draw raw profiles as step-style line charts
            var lineW = Math.Max(1f, dpiScale);
            DrawProfileLine(hProfile, plotX, plotY, plotW, plotH, maxVal, lineW, ProfileLineColor);
            DrawProfileLine(vProfile, plotX, plotY, plotW, plotH, maxVal, lineW, ProfileVLineColor);

            // Gaussian fit overlay (moment estimation — no iterative solver)
            var hFit = FitGaussian(hProfile);
            var vFit = FitGaussian(vProfile);

            if (hFit is var (hA, hMu, hSigma))
            {
                DrawGaussianCurve(hA, hMu, hSigma, hProfile.Length, plotX, plotY, plotW, plotH, maxVal, lineW, ProfileFitColor);
            }
            if (vFit is var (vA, vMu, vSigma))
            {
                DrawGaussianCurve(vA, vMu, vSigma, vProfile.Length, plotX, plotY, plotW, plotH, maxVal, lineW, ProfileVFitColor);
            }

            // FWHM text
            var fwhmText = "";
            if (hFit is var (_, _, hs)) fwhmText += $"H:{2.355 * hs:F1}px";
            if (vFit is var (_, _, vs)) fwhmText += (fwhmText.Length > 0 ? "  " : "") + $"V:{2.355 * vs:F1}px";
            if (fwhmText.Length > 0)
            {
                DrawText(fwhmText, fontPath,
                    plotX, plotY, plotW, fontSize,
                    fontSize * 0.75f, BodyText, TextAlign.Far, TextAlign.Near);
            }

            // Legend
            var legendY = rect.Y + rect.Height - fontSize;
            FillRect((int)plotX, (int)legendY, (int)(6 * dpiScale), (int)(2 * dpiScale), ProfileLineColor);
            DrawText("H", fontPath, plotX + 8 * dpiScale, legendY - fontSize * 0.2f, 15 * dpiScale, fontSize,
                fontSize * 0.7f, ProfileLineColor, TextAlign.Near, TextAlign.Center);
            FillRect((int)(plotX + 25 * dpiScale), (int)legendY, (int)(6 * dpiScale), (int)(2 * dpiScale), ProfileVLineColor);
            DrawText("V", fontPath, plotX + 33 * dpiScale, legendY - fontSize * 0.2f, 15 * dpiScale, fontSize,
                fontSize * 0.7f, ProfileVLineColor, TextAlign.Near, TextAlign.Center);
        }

        private void DrawProfileLine(float[] profile, float plotX, float plotY, float plotW, float plotH,
            float maxVal, float lineW, RGBAColor32 color)
        {
            if (profile.Length < 2) return;

            var step = plotW / (profile.Length - 1);
            for (var i = 1; i < profile.Length; i++)
            {
                var x1 = plotX + (i - 1) * step;
                var x2 = plotX + i * step;
                var y1 = plotY + plotH - (profile[i - 1] / maxVal * plotH);
                var y2 = plotY + plotH - (profile[i] / maxVal * plotH);

                // Horizontal segment then vertical connector (step-style)
                FillRect(x1, y1, x2 - x1, lineW, color);
                FillRect(x2, Math.Min(y1, y2), lineW, Math.Abs(y2 - y1) + lineW, color);
            }
        }

        /// <summary>
        /// Fits a Gaussian to a 1D profile via moment estimation (no iteration).
        /// Returns (amplitude, center, sigma) or default if the profile is flat.
        /// </summary>
        private static (float A, float Mu, float Sigma) FitGaussian(float[] profile)
        {
            var sumI = 0.0;
            var sumIX = 0.0;
            var peak = 0f;

            for (var i = 0; i < profile.Length; i++)
            {
                var v = profile[i];
                sumI += v;
                sumIX += v * i;
                if (v > peak) peak = v;
            }

            if (sumI <= 0 || peak <= 0)
            {
                return default;
            }

            var mu = sumIX / sumI;

            var sumIXX = 0.0;
            for (var i = 0; i < profile.Length; i++)
            {
                var d = i - mu;
                sumIXX += profile[i] * d * d;
            }

            var sigma = Math.Sqrt(sumIXX / sumI);
            if (sigma < 0.5) sigma = 0.5; // minimum width

            return ((float)peak, (float)mu, (float)sigma);
        }

        private void DrawGaussianCurve(float amplitude, float mu, float sigma, int profileLen,
            float plotX, float plotY, float plotW, float plotH, float maxVal, float lineW, RGBAColor32 color)
        {
            var steps = (int)plotW;
            if (steps < 2) return;

            var twoSigmaSq = 2.0 * sigma * sigma;
            for (var i = 1; i < steps; i++)
            {
                var t0 = (float)(i - 1) / steps * (profileLen - 1);
                var t1 = (float)i / steps * (profileLen - 1);
                var g0 = amplitude * Math.Exp(-((t0 - mu) * (t0 - mu)) / twoSigmaSq);
                var g1 = amplitude * Math.Exp(-((t1 - mu) * (t1 - mu)) / twoSigmaSq);

                var x1 = plotX + (float)(i - 1) / steps * plotW;
                var x2 = plotX + (float)i / steps * plotW;
                var y1 = plotY + plotH - (float)(g0 / maxVal) * plotH;
                var y2 = plotY + plotH - (float)(g1 / maxVal) * plotH;

                FillRect(x1, y1, x2 - x1, lineW, color);
                FillRect(x2, Math.Min(y1, y2), lineW, Math.Abs(y2 - y1) + lineW, color);
            }
        }

        private static readonly RGBAColor32 TargetBg = new RGBAColor32(0x10, 0x10, 0x18, 0xff);
        private static readonly RGBAColor32 TargetRingColor = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
        private static readonly RGBAColor32 SweetSpotColor = new RGBAColor32(0x18, 0x30, 0x18, 0xff);
        private static readonly RGBAColor32 RmsRingColor = new RGBAColor32(0x44, 0x66, 0x44, 0xff);
        private static readonly RGBAColor32 RecentDotColor = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 OldDotColor = new RGBAColor32(0x66, 0x66, 0x88, 0x88);

        /// <summary>
        /// PHD2-style target view: 2D scatter of RA (X) vs Dec (Y) error with RMS circle.
        /// </summary>
        private void RenderTargetView(RectF32 rect, string fontPath, float fontSize)
        {
            var dpiScale = DpiScale;
            var padding = BasePadding * dpiScale;
            var side = Math.Min(rect.Width, rect.Height) - padding * 2;
            var cx = rect.X + rect.Width / 2;
            var cy = rect.Y + rect.Height / 2;
            var halfSide = side / 2;

            var samples = State.GuideSamples;

            // Fixed scale: rings at 3", 6", 9", 12" (outer ring = 12")
            const double targetScaleArcsec = 12.0;
            const double ringStepArcsec = 3.0;
            const double sweetSpotArcsec = 1.0;

            // Sweet spot — filled disc showing acceptable guiding tolerance
            var sweetR = (float)(sweetSpotArcsec / targetScaleArcsec * halfSide);
            FillCircle(cx, cy, sweetR, SweetSpotColor);

            // Concentric rings at fixed arcsec intervals
            for (var ring = 1; ring <= 4; ring++)
            {
                var r = (float)(ring * ringStepArcsec / targetScaleArcsec * halfSide);
                DrawCircle(cx, cy, r, ring == 4 ? GuideGraphRenderer.ZeroLineColor : TargetRingColor);
            }

            // Crosshair — short marks at center only
            var crossLen = 8f * dpiScale;
            FillRect(cx - crossLen, cy, crossLen * 2, 1, TargetRingColor);
            FillRect(cx, cy - crossLen, 1, crossLen * 2, TargetRingColor);

            // Axis labels
            var labelSize = fontSize * 0.7f;
            DrawText("RA", fontPath, rect.X + rect.Width - padding - 20 * dpiScale, cy + 2, 20 * dpiScale, labelSize,
                labelSize, GuideGraphRenderer.RaColor, TextAlign.Far, TextAlign.Near);
            DrawText("Dec", fontPath, cx + 2, rect.Y + padding, 30 * dpiScale, labelSize,
                labelSize, GuideGraphRenderer.DecColor, TextAlign.Near, TextAlign.Near);

            // Scale label
            DrawText($"±{targetScaleArcsec:F0}\"", fontPath,
                rect.X + padding, rect.Y + rect.Height - labelSize * 1.5f, 50 * dpiScale, labelSize,
                labelSize, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Far);

            // RMS circle
            if (State.LastGuideStats is { TotalRMS: > 0 } stats)
            {
                var rmsR = (float)(stats.TotalRMS / targetScaleArcsec * halfSide);
                if (rmsR > 2)
                {
                    DrawCircle(cx, cy, Math.Min(rmsR, halfSide), RmsRingColor);
                }
            }

            // Plot dots — recent samples brighter, older samples dimmer
            var recentCount = Math.Min(samples.Length, 50);
            var startIdx = samples.Length - recentCount;

            for (var i = startIdx; i < samples.Length; i++)
            {
                var s = samples[i];
                var px = cx + (float)(Math.Clamp(s.RaError / targetScaleArcsec, -1, 1) * halfSide);
                var py = cy - (float)(Math.Clamp(s.DecError / targetScaleArcsec, -1, 1) * halfSide);

                // Fade: newest = white, oldest = dim
                var age = (float)(i - startIdx) / recentCount;
                var dotColor = age > 0.8f ? RecentDotColor : OldDotColor;
                var dotSize = age > 0.8f ? 3 : 2;

                FillRect((int)px - dotSize / 2, (int)py - dotSize / 2, dotSize, dotSize, dotColor);
            }

            // Latest point as larger bright dot
            if (samples.Length > 0)
            {
                var last = samples[^1];
                var lx = cx + (float)(Math.Clamp(last.RaError / targetScaleArcsec, -1, 1) * halfSide);
                var ly = cy - (float)(Math.Clamp(last.DecError / targetScaleArcsec, -1, 1) * halfSide);
                FillRect((int)lx - 2, (int)ly - 2, 5, 5, CrosshairColor);
            }

            // Calibration data text (angle, rates, backlash) — hide once guiding starts
            if (State.CalibrationOverlay is { } cal && samples.Length < 2)
            {
                RenderCalibrationText(cal, rect, fontPath, fontSize);
            }
        }


        private void RenderGraph(RectF32 rect, string fontPath, float fontSize)
        {
            var dpiScale = DpiScale;
            var samples = State.GuideSamples;
            if (samples.Length < 2)
            {
                return; // the frame tree shows the "Waiting for guide data…" text leaf instead
            }

            var padding = BasePadding * dpiScale;
            var halfH = rect.Height / 2;
            var zeroY = rect.Y + halfH;

            // Compute window first so Y scale can use visible samples
            var (startIdx, visibleCount, spacing) = GuideGraphRenderer.ComputeWindow(samples.Length, rect.Width, dpiScale);
            var yScale = GuideGraphRenderer.ComputeYScale(State.LastGuideStats, samples, startIdx, visibleCount);

            // Grid lines
            for (var arcsec = 1; arcsec < (int)yScale; arcsec++)
            {
                var gridY = (float)(arcsec / yScale) * halfH;
                FillRect(rect.X, zeroY - gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
                FillRect(rect.X, zeroY + gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
            }
            FillRect(rect.X, zeroY, rect.Width, 1, GuideGraphRenderer.ZeroLineColor);

            // Y-axis labels
            var labelW = 40f * dpiScale;
            DrawText($"+{yScale:F0}\"", fontPath,
                rect.X, rect.Y, labelW, fontSize * 1.2f,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Near);
            DrawText("0\"", fontPath,
                rect.X, zeroY - fontSize * 0.5f, labelW, fontSize,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Center);
            DrawText($"-{yScale:F0}\"", fontPath,
                rect.X, rect.Y + rect.Height - fontSize * 1.2f, labelW, fontSize * 1.2f,
                fontSize * 0.75f, GuideGraphRenderer.ZeroLineColor, TextAlign.Near, TextAlign.Far);

            // Connected step-style lines with settling shading and dither markers
            var lineW = Math.Max(dpiScale, 1f);

            // First pass: settling shading (behind everything)
            for (var i = 0; i < visibleCount; i++)
            {
                var sample = samples[startIdx + i];
                if (sample.IsSettling)
                {
                    var sx = rect.X + i * spacing;
                    FillRect(sx, rect.Y, spacing + 1, rect.Height, GuideGraphRenderer.SettlingShadeColor);
                }
            }

            // Second pass: correction bars (behind error lines, on top of shading)
            var barW = Math.Max(spacing * 0.3f, 1f);
            for (var i = 0; i < visibleCount; i++)
            {
                var sample = samples[startIdx + i];
                var bx = rect.X + i * spacing;

                if (sample.RaCorrectionMs != 0)
                {
                    // Bar extends up for positive (West), down for negative (East)
                    var barH = GuideGraphRenderer.CorrectionBarFraction(sample.RaCorrectionMs) * halfH;
                    if (sample.RaCorrectionMs > 0)
                        FillRect(bx, zeroY - barH, barW, barH, GuideGraphRenderer.RaCorrectionColor);
                    else
                        FillRect(bx, zeroY, barW, barH, GuideGraphRenderer.RaCorrectionColor);
                }
                if (sample.DecCorrectionMs != 0)
                {
                    var barH = GuideGraphRenderer.CorrectionBarFraction(sample.DecCorrectionMs) * halfH;
                    var dbx = bx + barW + 1; // offset Dec bars slightly right of RA bars
                    if (sample.DecCorrectionMs > 0)
                        FillRect(dbx, zeroY - barH, barW, barH, GuideGraphRenderer.DecCorrectionColor);
                    else
                        FillRect(dbx, zeroY, barW, barH, GuideGraphRenderer.DecCorrectionColor);
                }
            }

            // Second pass: error lines
            for (var i = 1; i < visibleCount; i++)
            {
                var x1 = rect.X + (i - 1) * spacing;
                var x2 = rect.X + i * spacing;

                var raY1 = GuideGraphRenderer.ErrorToY(samples[startIdx + i - 1].RaError, yScale, zeroY, halfH);
                var raY2 = GuideGraphRenderer.ErrorToY(samples[startIdx + i].RaError, yScale, zeroY, halfH);
                FillRect(x1, raY1, x2 - x1, lineW, GuideGraphRenderer.RaColor);
                FillRect(x2, Math.Min(raY1, raY2), lineW, Math.Abs(raY2 - raY1) + lineW, GuideGraphRenderer.RaColor);

                var decY1 = GuideGraphRenderer.ErrorToY(samples[startIdx + i - 1].DecError, yScale, zeroY, halfH);
                var decY2 = GuideGraphRenderer.ErrorToY(samples[startIdx + i].DecError, yScale, zeroY, halfH);
                FillRect(x1, decY1, x2 - x1, lineW, GuideGraphRenderer.DecColor);
                FillRect(x2, Math.Min(decY1, decY2), lineW, Math.Abs(decY2 - decY1) + lineW, GuideGraphRenderer.DecColor);
            }

            // Third pass: dither markers (on top of everything)
            for (var i = 0; i < visibleCount; i++)
            {
                if (samples[startIdx + i].IsDither)
                {
                    var dx = rect.X + i * spacing;
                    // Dashed vertical line
                    for (var dy = rect.Y; dy < rect.Y + rect.Height; dy += 6 * dpiScale)
                    {
                        FillRect(dx, dy, Math.Max(1, lineW), 3 * dpiScale, GuideGraphRenderer.DitherMarkerColor);
                    }
                }
            }

            // Legend
            var legendY = rect.Y + rect.Height - padding * 2;
            FillRect((int)(rect.X + padding), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), GuideGraphRenderer.RaColor);
            DrawText("RA", fontPath,
                rect.X + padding + 10 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, GuideGraphRenderer.RaColor, TextAlign.Near, TextAlign.Center);
            FillRect((int)(rect.X + padding + 50 * dpiScale), (int)legendY, (int)(8 * dpiScale), (int)(3 * dpiScale), GuideGraphRenderer.DecColor);
            DrawText("Dec", fontPath,
                rect.X + padding + 60 * dpiScale, legendY - fontSize * 0.3f, 30 * dpiScale, fontSize,
                fontSize * 0.8f, GuideGraphRenderer.DecColor, TextAlign.Near, TextAlign.Center);
        }
    }
}
