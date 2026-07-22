using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;
using TianWen.Lib.Sequencing.PolarAlignment;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Per-OTA panels: exposure countdown/state, cooling mini-sparkline, exposure log.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        private void RenderOTAPanels(LiveSessionState state, RectF32 rect,
            float fontSize, float pad, float rowH, ITimeProvider timeProvider)
        {
            RenderLayout(Layout.Builder.Spacer().Bg(PanelBg), rect);

            if (!state.IsRunning)
            {
                RenderPreviewOTAPanels(state, rect, fontSize, pad, rowH, timeProvider);
                return;
            }

            var fontPath = FontPath;
            var dpiScale = DpiScale;

            if (state.ActiveSession is not { } session)
            {
                DrawText("Starting\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var otaCount = session.Setup.Telescopes.Length;
            if (otaCount == 0)
            {
                return;
            }

            _otaPanelFills.Clear();

            // Columns: one VStack per OTA, 1px full-height dividers between them (a coloured Box node, not a
            // Fill painter). Mirrors RenderPreviewOTAPanels.
            var columns = new List<Layout.Node>(otaCount * 2);
            for (var i = 0; i < otaCount; i++)
            {
                if (i > 0)
                {
                    columns.Add(Layout.Builder.Spacer().WFixed(1f).HStar().Bg(SeparatorColor));
                }
                columns.Add(BuildRunningOtaColumn(state, session, i, fontSize, timeProvider).WStar());
            }
            var columnsRow = Layout.Builder.HStack([.. columns]);

            // Mount status docked to the bottom (full width), gated on enough vertical room (mirrors the old
            // "only show if there's room" guard against the rowH*6 reservation). Up to five rows (incl. the
            // optional target line) + padding + the hairline divider.
            const float mountHDesign = BaseRowHeight * 5 + BasePadding * 2 + 1f;
            var mountHpx = mountHDesign * dpiScale;
            var showMount = rect.Y + rect.Height - mountHpx > rect.Y + rect.Height * 0.35f;

            var tree = showMount
                ? Layout.Builder.Dock(columnsRow, Layout.Builder.Bottom(BuildRunningMountSection(state, session), mountHDesign))
                : columnsRow;

            Renderer.PushClip(new RectInt(
                new PointInt((int)(rect.X + rect.Width), (int)(rect.Y + rect.Height)),
                new PointInt((int)rect.X, (int)rect.Y)));
            RenderLayout(tree, rect, drawFill: DispatchOtaPanelFill);
            Renderer.PopClip();
        }

        /// <summary>
        /// Builds one running-session per-OTA column as a padded VStack: camera name, temperature + cooling
        /// sparkline (a keyed <see cref="Layout.Content.Fill"/> raster), focuser + filter readouts, the
        /// exposure state (label + progress bar), and the V-curve chart (raster) filling the remaining height
        /// during focus phases.
        /// </summary>
        private Layout.Node BuildRunningOtaColumn(LiveSessionState state, ISession session, int i,
            float fontSize, ITimeProvider timeProvider)
        {
            var ota = session.Setup.Telescopes[i];
            var cameraStates = state.CameraStates;
            var coolingSamples = state.CoolingSamples;
            var smallFsDevice = fontSize * 0.85f; // device px for the V-curve raster painter
            var rows = new List<Layout.Node>();

            // OTA header (camera name)
            rows.Add(Layout.Builder.Text(ota.Camera.Device.DisplayName, BaseFontSize, HeaderText).RowH(BaseRowHeight));

            // Temperature + power from the latest cooling sample for this camera + a mini sparkline.
            var lastTemp = double.NaN;
            var lastPower = double.NaN;
            var lastSetpoint = double.NaN;
            for (var j = coolingSamples.Length - 1; j >= 0; j--)
            {
                if (coolingSamples[j].CameraIndex == i)
                {
                    lastTemp = coolingSamples[j].TemperatureC;
                    lastPower = coolingSamples[j].CoolerPowerPercent;
                    lastSetpoint = coolingSamples[j].SetpointTempC;
                    break;
                }
            }

            if (!double.IsNaN(lastTemp))
            {
                var tempColor = CameraTempColors[i % CameraTempColors.Length];
                var tempText = $"{lastTemp:F0}\u00b0C  {lastPower:F0}%";
                if (!double.IsNaN(lastSetpoint))
                {
                    tempText += $"  \u2192 {lastSetpoint:F0}\u00b0C";
                }
                rows.Add(Layout.Builder.Text(tempText, BaseFontSize * 0.85f, tempColor).RowH(BaseRowHeight));

                var sparkKey = $"otaSpark:{i}";
                _otaPanelFills[sparkKey] = r => CoolingSparklineRenderer.Render(Renderer, r, coolingSamples, i, DpiScale,
                    tempColor, CameraPowerColors[i % CameraPowerColors.Length]);
                rows.Add(Layout.Builder.Fill(key: sparkKey).RowH(60f));
            }
            rows.Add(Layout.Builder.Spacer().RowH(BasePadding));

            // Focuser position + temperature + moving state
            if (ota.Focuser is not null && i < cameraStates.Length)
            {
                var cs = cameraStates[i];
                var focLabel = $"Foc: {cs.FocusPosition}";
                if (!double.IsNaN(cs.FocuserTemperature))
                {
                    focLabel += $"  {cs.FocuserTemperature:F1}\u00b0C";
                }
                if (cs.FocuserIsMoving)
                {
                    focLabel += "  \u21c4 Moving";
                }
                rows.Add(Layout.Builder.Text(focLabel, BaseFontSize, cs.FocuserIsMoving ? StatusSlewing : BodyText).RowH(BaseRowHeight));
            }

            // Filter
            if (ota.FilterWheel is not null)
            {
                var filterName = i < cameraStates.Length
                    ? LiveSessionActions.FilterDisplayLabel(cameraStates[i].FilterName, "--")
                    : "--";
                rows.Add(Layout.Builder.Text($"FW: {filterName}", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight));
            }

            // Exposure state (label + progress bar) or Idle
            rows.Add(Layout.Builder.Spacer().RowH(BasePadding));
            rows.Add(i < cameraStates.Length
                ? BuildExposureState(cameraStates[i], fontSize, timeProvider)
                : Layout.Builder.Text("Idle", BaseFontSize * 0.85f, DimText).RowH(BaseRowHeight));

            // V-curve chart (fills the remaining column height) during focus phases.
            var activeSamples = state.ActiveFocusSamples;
            var lastFocusRun = state.FocusHistory is { Length: > 0 } fh ? fh[^1] : default(FocusRunRecord?);
            var showVCurve = activeSamples.Length >= 2
                || (lastFocusRun?.Curve.Length >= 2
                    && state.Phase is SessionPhase.AutoFocus or SessionPhase.CalibratingGuider or SessionPhase.RoughFocus);
            if (showVCurve)
            {
                var chartSamples = activeSamples.Length >= 2 ? activeSamples : lastFocusRun!.Value.Curve;
                var vcurveKey = $"otaVCurve:{i}";
                _otaPanelFills[vcurveKey] = r =>
                {
                    if (r.Height > 40)
                    {
                        VCurveChartRenderer.Render(Renderer, r, chartSamples, lastFocusRun, DpiScale, FontPath, smallFsDevice);
                    }
                };
                rows.Add(Layout.Builder.Fill(key: vcurveKey).Stretch());
            }

            return Layout.Builder.VStack([.. rows]).Pad(BasePadding);
        }

        /// <summary>
        /// Builds the exposure-state node for one OTA: "Idle" / "Downloading #n" text, or (while exposing) a
        /// VStack of a countdown label + a <see cref="FormRowLayout.ProgressBar"/> with the remaining seconds
        /// centred on it. The bar is a declarative node (track + fractional fill + label), not a hand-drawn
        /// FillRect gauge.
        /// </summary>
        private Layout.Node BuildExposureState(CameraExposureState cs, float fontSize, ITimeProvider timeProvider)
        {
            if (cs.State == CameraState.Idle)
            {
                return Layout.Builder.Text("Idle", BaseFontSize * 0.85f, DimText).RowH(BaseRowHeight);
            }

            if (cs.State == CameraState.Download || cs.State == CameraState.Reading)
            {
                return Layout.Builder.Text($"Downloading #{cs.FrameNumber}\u2026", BaseFontSize * 0.85f, HeaderText).RowH(BaseRowHeight);
            }

            // Exposing -- countdown label + progress bar with the remaining time overlaid.
            var elapsed = timeProvider.GetUtcNow() - cs.ExposureStart;
            var totalSec = cs.SubExposure.TotalSeconds;
            var elapsedSec = Math.Min(elapsed.TotalSeconds, totalSec);
            var fraction = totalSec > 0 ? (float)(elapsedSec / totalSec) : 0f;

            var filterLabel = LiveSessionActions.FilterDisplayLabel(cs.FilterName, "L");
            var expLabel = $"{filterLabel} #{cs.FrameNumber} ({elapsedSec:F0}/{totalSec:F0}s)";
            var remaining = cs.SubExposure - elapsed;
            var remText = remaining.TotalSeconds > 0 ? $"{remaining.TotalSeconds:F0}s" : null;

            return Layout.Builder.VStack(
                    Layout.Builder.Text(expLabel, BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight),
                    FormRowLayout.ProgressBar(fraction, ProgressBg, ProgressFill, remText, BaseFontSize * 0.65f, BrightText)
                        .RowH(BaseProgressBarH))
                .WStar();
        }

        /// <summary>
        /// Builds the bottom-pinned mount status block for a running session (dot + name, status/pier, RA/HA,
        /// Dec, and an optional target row) as a padded VStack, prefixed by a full-width hairline divider (a
        /// coloured Box node, not a Fill painter). Docked full-width at the panel bottom by
        /// <see cref="RenderOTAPanels"/>.
        /// </summary>
        private Layout.Node BuildRunningMountSection(LiveSessionState state, ISession session)
        {
            var ms = state.MountState;
            var dotColor = ms.IsSlewing ? StatusSlewing : ms.IsTracking ? StatusTracking : DimText;
            var pierLabel = ms.PierSide is Lib.Devices.PointingState.Normal ? "E" : ms.PierSide is Lib.Devices.PointingState.ThroughThePole ? "W" : "";
            var mountStatus = ms.IsSlewing ? "Slewing" : ms.IsTracking ? "Tracking" : "Idle";
            var statusColor = ms.IsSlewing ? StatusSlewing : ms.IsTracking ? StatusTracking : DimText;
            var raStr = Lib.Astrometry.CoordinateUtils.HoursToHMS(ms.RightAscension, withFrac: false);
            var haStr = $"HA {ms.HourAngle:+0.00;-0.00}h";
            var decStr = Lib.Astrometry.CoordinateUtils.DegreesToDMS(ms.Declination, withFrac: false);

            var nameRow = Layout.Builder.HStack(
                    Layout.Builder.Text("\u25cf", BaseFontSize * 0.7f, dotColor, TextAlign.Center, TextAlign.Center).WFixed(BaseRowHeight * 0.6f).HStar(),
                    Layout.Builder.Text(session.Setup.Mount.Device.DisplayName, BaseFontSize * 0.85f, HeaderText).WStar().HStar())
                .RowH(BaseRowHeight);
            var statusRow = Layout.Builder.Text($"{mountStatus}  {pierLabel}", BaseFontSize * 0.85f, statusColor).RowH(BaseRowHeight);
            var raHaRow = Layout.Builder.Text($"RA {raStr}  {haStr}", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight);
            var decRow = Layout.Builder.Text($"Dec {decStr}", BaseFontSize * 0.85f, BodyText).RowH(BaseRowHeight);

            var content = state.ActiveObservation is { Target: var target }
                ? Layout.Builder.VStack(nameRow, statusRow, raHaRow, decRow,
                    Layout.Builder.Text($"\u2609 {target.Name}", BaseFontSize * 0.85f, DimText).RowH(BaseRowHeight)).Pad(BasePadding)
                : Layout.Builder.VStack(nameRow, statusRow, raHaRow, decRow).Pad(BasePadding);

            // Full-width hairline divider above the block (a coloured Box node, not a Fill painter).
            return Layout.Builder.VStack(
                Layout.Builder.Spacer().RowH(1f).Bg(SeparatorColor),
                content);
        }

        // Tiny temperature + power cooling sparkline moved to the paint-owning CoolingSparklineRenderer.

        // -----------------------------------------------------------------------
        // Preview mode: OTA panels from profile + hub telemetry
        // -----------------------------------------------------------------------

        /// <summary>
        /// Exposure-log scroll (DIR.Lib atom model, one atom = one log row): bottom-anchored
        /// tail-follow -- pinned to the newest entry until the user wheels up into history, and
        /// re-pinned automatically when the log resets (a session restart clears it, the content
        /// fits again, and the controller's fits-again rule restores the pin -- no bootstrapper
        /// reset needed). Mode=None keeps the historical no-scrollbar look; wheel AND body
        /// drag-to-scroll are viewport-gated by the controller (the old code scrolled the log from
        /// anywhere on the tab, and a press on the log could grab the preview pan).
        /// </summary>
        private readonly ListScrollController _logScroll = new ListScrollController
        {
            Anchor = ScrollAnchor.Bottom,
            SnapToAtom = true,
            Mode = ScrollBarMode.None,
        };

        private void RenderExposureLog(LiveSessionState state, RectF32 rect,
            float fontSize, float pad, float rowH)
        {
            RenderLayout(Layout.Builder.Spacer().Bg(PanelBg), rect);

            // Separator on left edge
            RenderLayout(Layout.Builder.Spacer().Bg(SeparatorColor), new RectF32(rect.X, rect.Y, 1, rect.Height));

            if (!state.IsRunning && state.Mode == LiveSessionMode.PolarAlign)
            {
                RenderPolarSidePanel(state, rect, fontSize, pad, rowH);
                return;
            }

            // Flats mode never sets IsRunning (RunFlatsOnlyAsync is not the full RunAsync), so the tab
            // keeps the preview layout + mode pill; the run's live state is tracked via FlatsCts and the
            // session snapshot mirrored by PollSession.
            if (state.Mode == LiveSessionMode.Flats)
            {
                RenderFlatsSidePanel(state, rect, fontSize, pad, rowH);
                return;
            }

            var fontPath = FontPath;

            if (!state.IsRunning)
            {
                // Preview mode: show header + plate solve result if available
                DrawText("Preview Mode", fontPath,
                    rect.X + pad, rect.Y, rect.Width - pad * 2, rowH,
                    fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

                if (state.PreviewPlateSolveResult is { } solveResult)
                {
                    var solveY = rect.Y + rowH + pad;
                    if (solveResult.Solution is { } wcs)
                    {
                        DrawText($"RA  {wcs.CenterRA:F4}h", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
                        solveY += rowH;
                        DrawText($"Dec {wcs.CenterDec:F3}\u00b0", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
                        solveY += rowH;
                        DrawText($"Scale {wcs.PixelScaleArcsec:F2}\"/px", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
                        solveY += rowH;
                        DrawText($"Solved in {solveResult.Elapsed.TotalSeconds:F1}s  {solveResult.MatchedStars} matched", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);
                    }
                    else
                    {
                        DrawText("Solve: no match", fontPath,
                            rect.X + pad, solveY, rect.Width - pad * 2, rowH,
                            fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
                    }
                }
                return;
            }

            // Header
            DrawText("Exposure Log", fontPath,
                rect.X + pad, rect.Y, rect.Width - pad * 2, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

            // Column layout -- fixed pixel positions for alignment with proportional fonts
            var colY = rect.Y + rowH;
            var x0 = rect.X + pad;
            var w = rect.Width;
            var colTime = x0;
            var colTarget = x0 + w * 0.14f;
            var colFilter = x0 + w * 0.55f;
            var colHfd = x0 + w * 0.73f;
            var colStars = x0 + w * 0.88f;
            var smallFs = fontSize * 0.75f;
            var rowFs = fontSize * 0.8f;

            RenderLayout(Layout.Builder.Spacer().Bg(HeaderBg), new RectF32(rect.X, colY, rect.Width, rowH));
            DrawText("Time", fontPath, colTime, colY, colTarget - colTime, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Target", fontPath, colTarget, colY, colFilter - colTarget, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("Filter", fontPath, colFilter, colY, colHfd - colFilter, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("HFD", fontPath, colHfd, colY, colStars - colHfd, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            DrawText("\u2605", fontPath, colStars, colY, rect.X + rect.Width - colStars - pad, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);

            var log = state.ExposureLog;

            // Hand the controller this frame's rows viewport (below the two header rows). Geometry
            // refreshes even when the log is empty so a stale viewport can never eat wheel input;
            // the bottom anchor + fits-again re-pin do the tail-follow (see the field doc).
            var rowsTop = colY + rowH + pad;
            _logScroll.SetExtent(
                new RectF32(rect.X, rowsTop, rect.Width, rect.Height - rowH * 2 - pad * 2),
                rowH, log.Length, DpiScale);

            if (log.Length == 0)
            {
                DrawText("No frames yet", fontPath,
                    rect.X, colY + rowH, rect.Width, rowH * 2,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var y = rowsTop;
            foreach (var (i, rowRect) in _logScroll.VisibleRows())
            {
                var entry = log[i];
                var rowY = rowRect.Y;
                var bg = (i % 2 == 0) ? PanelBg : RowAltBg;
                RenderLayout(Layout.Builder.Spacer().Bg(bg), new RectF32(rowRect.X, rowY, rowRect.Width, rowRect.Height));

                var target = entry.TargetName.Length > 10 ? entry.TargetName[..10] : entry.TargetName;
                var filterRaw = LiveSessionActions.FilterDisplayLabel(entry.FilterName, "L");
                var filter = filterRaw.Length > 6 ? filterRaw[..6] : filterRaw;
                var hfd = entry.MedianHfd > 0 ? $"{entry.MedianHfd:F1}\"" : "--";
                var stars = entry.StarCount > 0 ? $"{entry.StarCount}" : "--";

                DrawText(entry.Timestamp.ToOffset(state.SiteTimeZone).ToString("HH:mm"), fontPath, colTime, rowY, colTarget - colTime, rowH, rowFs, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(target, fontPath, colTarget, rowY, colFilter - colTarget, rowH, rowFs, BodyText, TextAlign.Near, TextAlign.Center);
                DrawText(filter, fontPath, colFilter, rowY, colHfd - colFilter, rowH, rowFs, DimText, TextAlign.Near, TextAlign.Center);
                DrawText(hfd, fontPath, colHfd, rowY, colStars - colHfd, rowH, rowFs, BodyText, TextAlign.Far, TextAlign.Center);
                DrawText(stars, fontPath, colStars, rowY, rect.X + rect.Width - colStars - pad, rowH, rowFs, BodyText, TextAlign.Far, TextAlign.Center);
                y = rowY + rowH;
            }

            // Focus history below exposure log if space allows
            var remainH = rect.Y + rect.Height - y;
            if (remainH > rowH * 3 && state.FocusHistory.Length > 0)
            {
                RenderLayout(Layout.Builder.Spacer().Bg(SeparatorColor), new RectF32(rect.X, y, rect.Width, 1));
                y += pad;

                DrawText("Focus History", fontPath,
                    rect.X + pad, y, rect.Width - pad * 2, rowH,
                    fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                var history = state.FocusHistory;
                var focusStartIdx = Math.Max(0, history.Length - (int)((remainH - rowH * 2) / rowH));
                for (var i = focusStartIdx; i < history.Length && y < rect.Y + rect.Height - rowH; i++)
                {
                    var row = LiveSessionActions.FormatFocusHistoryRow(history[i], state.SiteTimeZone);
                    var bg = (i % 2 == 0) ? PanelBg : RowAltBg;
                    RenderLayout(Layout.Builder.Spacer().Bg(bg), new RectF32(rect.X, y, rect.Width, rowH));
                    DrawText(row, fontPath,
                        rect.X + pad, y, rect.Width - pad * 2, rowH,
                        fontSize * 0.75f, BodyText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Abort confirmation overlay
        // -----------------------------------------------------------------------
    }
}
