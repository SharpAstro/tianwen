using System;
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
        private void RenderOTAPanels(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float dpiScale, float pad, float rowH, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            if (!state.IsRunning)
            {
                RenderPreviewOTAPanels(state, rect, fontPath, fontSize, dpiScale, pad, rowH, timeProvider);
                return;
            }

            if (state.ActiveSession is not { } session)
            {
                DrawText("Starting\u2026", fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var telescopes = session.Setup.Telescopes;
            var cameraStates = state.CameraStates;
            var otaCount = telescopes.Length;

            if (otaCount == 0)
            {
                return;
            }

            // Split horizontally for multiple OTAs
            var panelW = rect.Width / otaCount;
            var progressH = BaseProgressBarH * dpiScale;
            var smallFs = fontSize * 0.85f;

            for (var i = 0; i < otaCount; i++)
            {
                var ota = telescopes[i];
                var px = rect.X + i * panelW;

                // Separator between OTAs
                if (i > 0)
                {
                    FillRect(px, rect.Y, 1, rect.Height, SeparatorColor);
                }

                var y = rect.Y + pad;
                var textW = panelW - pad * 2;
                // Mount status is pinned to the bottom; stop rendering OTA items before they overlap
                var maxY = rect.Y + rect.Height - rowH * 6;

                // OTA header (camera name)
                DrawText(ota.Camera.Device.DisplayName, fontPath,
                    px + pad, y, textW, rowH,
                    fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                // Temperature + power from latest cooling sample for this camera
                var lastTemp = double.NaN;
                var lastPower = double.NaN;
                var lastSetpoint = double.NaN;
                var coolingSamples = state.CoolingSamples;
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
                    var tempText = $"{lastTemp:F0}\u00B0C  {lastPower:F0}%";
                    if (!double.IsNaN(lastSetpoint))
                    {
                        tempText += $"  \u2192 {lastSetpoint:F0}\u00B0C";
                    }
                    DrawText(tempText, fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, tempColor, TextAlign.Near, TextAlign.Center);
                    y += rowH;

                    // Mini cooling sparkline (last 20 samples for this camera)
                    var sparkH = 60f * dpiScale;
                    RenderMiniSparkline(coolingSamples, i, new RectF32(px + pad, y, textW, sparkH), tempColor, dpiScale);
                    y += sparkH + pad;
                }
                else
                {
                    y += pad;
                }

                // Focuser position + temperature + moving state
                if (y < maxY && ota.Focuser is not null && i < cameraStates.Length)
                {
                    var cs = cameraStates[i];
                    var focLabel = $"Foc: {cs.FocusPosition}";
                    if (!double.IsNaN(cs.FocuserTemperature))
                    {
                        focLabel += $"  {cs.FocuserTemperature:F1}\u00B0C";
                    }
                    if (cs.FocuserIsMoving)
                    {
                        focLabel += "  \u21C4 Moving";
                    }
                    var focColor = cs.FocuserIsMoving ? StatusSlewing : BodyText;
                    DrawText(focLabel, fontPath,
                        px + pad, y, textW, rowH,
                        fontSize, focColor, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // Filter
                if (y < maxY && ota.FilterWheel is not null)
                {
                    var filterName = i < cameraStates.Length ? LiveSessionActions.FilterDisplayLabel(cameraStates[i].FilterName, "--") : "--";
                    DrawText($"FW: {filterName}", fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // Exposure state + progress bar
                y += pad;
                if (y < maxY && i < cameraStates.Length)
                {
                    var cs = cameraStates[i];
                    RenderExposureState(cs, px + pad, y, textW, progressH, rowH, fontPath, fontSize, smallFs, dpiScale, timeProvider);
                    y += rowH + progressH + pad;
                }
                else if (y < maxY)
                {
                    DrawText("Idle", fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, DimText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // V-curve chart for this OTA (below its exposure state)
                var activeSamples = state.ActiveFocusSamples;
                var lastFocusRun = state.FocusHistory is { Length: > 0 } fh ? fh[^1] : default(FocusRunRecord?);
                var showVCurve = y < maxY
                    && (activeSamples.Length >= 2
                        || (lastFocusRun?.Curve.Length >= 2
                            && state.Phase is SessionPhase.AutoFocus or SessionPhase.CalibratingGuider or SessionPhase.RoughFocus));
                if (showVCurve)
                {
                    var chartSamples = activeSamples.Length >= 2 ? activeSamples : lastFocusRun!.Value.Curve;
                    var chartH = maxY - y - pad;
                    if (chartH > 40)
                    {
                        RenderVCurveChart(chartSamples, lastFocusRun, new RectF32(px + pad, y, textW, chartH), fontPath, smallFs, dpiScale);
                    }
                }
            }

            // Mount status section (below OTAs, full width) as one padded VStack: name row (status dot +
            // name), status+pier, RA/HA, Dec, and an optional target row. Was six hand-positioned draws.
            var mountY = rect.Y + rect.Height - rowH * 6 - pad;
            if (mountY > rect.Y + rect.Height * 0.35f) // only show if there's room
            {
                FillRect(rect.X, mountY, rect.Width, 1, SeparatorColor); // hairline divider stays a single pixel line

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

                var mountTree = state.ActiveObservation is { Target: var target }
                    ? Layout.Builder.VStack(nameRow, statusRow, raHaRow, decRow,
                        Layout.Builder.Text($"\u2609 {target.Name}", BaseFontSize * 0.85f, DimText).RowH(BaseRowHeight)).Pad(BasePadding)
                    : Layout.Builder.VStack(nameRow, statusRow, raHaRow, decRow).Pad(BasePadding);
                RenderLayout(mountTree, new RectF32(rect.X, mountY, rect.Width, rect.Y + rect.Height - mountY), fontPath, dpiScale);
            }
        }

        private void RenderExposureState(CameraExposureState cs, float x, float y, float w, float progressH, float rowH,
            string fontPath, float fontSize, float smallFs, float dpiScale, ITimeProvider timeProvider)
        {
            if (cs.State == CameraState.Idle)
            {
                DrawText("Idle", fontPath,
                    x, y, w, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            if (cs.State == CameraState.Download || cs.State == CameraState.Reading)
            {
                DrawText($"Downloading #{cs.FrameNumber}\u2026", fontPath,
                    x, y, w, rowH, smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
                return;
            }

            // Exposing — show countdown + progress bar
            var elapsed = timeProvider.GetUtcNow() - cs.ExposureStart;
            var totalSec = cs.SubExposure.TotalSeconds;
            var elapsedSec = Math.Min(elapsed.TotalSeconds, totalSec);
            var fraction = totalSec > 0 ? (float)(elapsedSec / totalSec) : 0f;

            // Filter + frame label
            var filterLabel = LiveSessionActions.FilterDisplayLabel(cs.FilterName, "L");
            var expLabel = $"{filterLabel} #{cs.FrameNumber} ({elapsedSec:F0}/{totalSec:F0}s)";
            DrawText(expLabel, fontPath,
                x, y, w, rowH, smallFs, BodyText, TextAlign.Near, TextAlign.Center);
            y += rowH;

            // Progress bar
            FillRect(x, y, w, progressH, ProgressBg);
            var fillW = w * Math.Clamp(fraction, 0f, 1f);
            if (fillW > 0)
            {
                FillRect(x, y, fillW, progressH, ProgressFill);
            }

            // Remaining time overlay on bar
            var remaining = cs.SubExposure - elapsed;
            if (remaining.TotalSeconds > 0)
            {
                var remText = $"{remaining.TotalSeconds:F0}s";
                DrawText(remText, fontPath,
                    x, y, w, progressH,
                    fontSize * 0.65f, BrightText, TextAlign.Center, TextAlign.Center);
            }
        }

        /// <summary>Tiny sparkline of temperature + power for a single camera.</summary>
        private void RenderMiniSparkline(ImmutableArray<CoolingSample> allSamples, int cameraIndex, RectF32 rect, RGBAColor32 tempColor, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

            var powerColor = CameraPowerColors[cameraIndex % CameraPowerColors.Length];

            // Collect last N samples for this camera
            const int maxPoints = 20;
            Span<float> temps = stackalloc float[maxPoints];
            Span<float> powers = stackalloc float[maxPoints];
            var count = 0;
            for (var i = allSamples.Length - 1; i >= 0 && count < maxPoints; i--)
            {
                if (allSamples[i].CameraIndex == cameraIndex)
                {
                    temps[maxPoints - 1 - count] = (float)allSamples[i].TemperatureC;
                    powers[maxPoints - 1 - count] = (float)allSamples[i].CoolerPowerPercent;
                    count++;
                }
            }

            if (count < 2)
            {
                return;
            }

            var start = maxPoints - count;
            var tempSlice = temps.Slice(start, count);
            var powerSlice = powers.Slice(start, count);

            // Find temp range
            var minT = float.MaxValue;
            var maxT = float.MinValue;
            for (var i = 0; i < count; i++)
            {
                if (tempSlice[i] < minT) minT = tempSlice[i];
                if (tempSlice[i] > maxT) maxT = tempSlice[i];
            }
            var range = Math.Max(maxT - minT, 2f);
            minT -= 1;
            maxT = minT + range + 2;

            var stepX = rect.Width / Math.Max(count - 1, 1);

            // Draw power line first (behind temp)
            for (var i = 1; i < count; i++)
            {
                var x1 = rect.X + (i - 1) * stepX;
                var x2 = rect.X + i * stepX;
                var y1 = rect.Y + rect.Height - (powerSlice[i - 1] / 100f) * rect.Height;
                var y2 = rect.Y + rect.Height - (powerSlice[i] / 100f) * rect.Height;

                FillRect(x1, y1, x2 - x1, Math.Max(1, dpiScale), powerColor);
                FillRect(x2, Math.Min(y1, y2), Math.Max(1, dpiScale), Math.Abs(y2 - y1) + dpiScale, powerColor);
            }

            // Draw temp line on top
            for (var i = 1; i < count; i++)
            {
                var x1 = rect.X + (i - 1) * stepX;
                var x2 = rect.X + i * stepX;
                var y1 = rect.Y + rect.Height - ((tempSlice[i - 1] - minT) / (maxT - minT)) * rect.Height;
                var y2 = rect.Y + rect.Height - ((tempSlice[i] - minT) / (maxT - minT)) * rect.Height;

                FillRect(x1, y1, x2 - x1, Math.Max(1, dpiScale), tempColor);
                FillRect(x2, Math.Min(y1, y2), Math.Max(1, dpiScale), Math.Abs(y2 - y1) + dpiScale, tempColor);
            }
        }

        // -----------------------------------------------------------------------
        // Preview mode: OTA panels from profile + hub telemetry
        // -----------------------------------------------------------------------

        /// <summary>
        /// Exposure-log scroll (DIR.Lib atom model, one atom = one log row): bottom-anchored
        /// tail-follow — pinned to the newest entry until the user wheels up into history, and
        /// re-pinned automatically when the log resets (a session restart clears it, the content
        /// fits again, and the controller's fits-again rule restores the pin — no bootstrapper
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

        private void RenderExposureLog(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float dpiScale, float pad, float rowH)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            // Separator on left edge
            FillRect(rect.X, rect.Y, 1, rect.Height, SeparatorColor);

            if (!state.IsRunning && state.Mode == LiveSessionMode.PolarAlign)
            {
                RenderPolarSidePanel(state, rect, fontPath, fontSize, dpiScale, pad, rowH);
                return;
            }

            // Flats mode never sets IsRunning (RunFlatsOnlyAsync is not the full RunAsync), so the tab
            // keeps the preview layout + mode pill; the run's live state is tracked via FlatsCts and the
            // session snapshot mirrored by PollSession.
            if (state.Mode == LiveSessionMode.Flats)
            {
                RenderFlatsSidePanel(state, rect, fontPath, fontSize, dpiScale, pad, rowH);
                return;
            }

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
                        DrawText($"Dec {wcs.CenterDec:F3}\u00B0", fontPath,
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

            // Column layout — fixed pixel positions for alignment with proportional fonts
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

            FillRect(rect.X, colY, rect.Width, rowH, HeaderBg);
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
                rowH, log.Length, dpiScale);

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
                FillRect(rowRect.X, rowY, rowRect.Width, rowRect.Height, bg);

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
                FillRect(rect.X, y, rect.Width, 1, SeparatorColor);
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
                    FillRect(rect.X, y, rect.Width, rowH, bg);
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
