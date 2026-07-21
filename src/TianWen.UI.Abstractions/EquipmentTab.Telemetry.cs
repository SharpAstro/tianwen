using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Connected-device telemetry panes: camera cooler control + temperature sparkline and the
    /// mount status readout, both collapsible headers gated on hub connection.
    /// </summary>
    partial class EquipmentTab<TSurface>
    {
        /// <summary>
        /// Renders the camera cooler control + telemetry sparkline panel for the given URI,
        /// but only when the camera is currently connected via the device hub. Hidden otherwise.
        /// Layout: collapsible header -> readout row -> setpoint input + buttons -> temp sparkline.
        /// </summary>
        private float RenderCameraTelemetryIfAny(
            GuiAppState appState,
            Uri? cameraUri,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding)
        {
            if (cameraUri is null || cameraUri == NoneDevice.Instance.DeviceUri) return cursor;
            if (appState.DeviceHub is not { } hub || !hub.IsConnected(cameraUri)) return cursor;

            var key = cameraUri.GetLeftPart(UriPartial.Path);
            var rowH = itemH * 0.9f;
            var headerKey = $"CamCool_{key}";

            // Toggle header -- independent of the device-settings expand state.
            // Stored on EquipmentTabState as a separate sub-key so it doesn't clash.
            var paneKey = $"CoolerPane:{key}";
            var isOpen = State.ExpandedDeviceSettingsUri == paneKey;
            var headerLabel = isOpen ? "    Cooler Control [-]" : "    Cooler Control [+]";

            var camToggle = FormRowLayout.ToggleHeaderRow(
                headerLabel, rowH, FilterTableBg, HeaderText, BaseFontSize * 0.85f,
                new HitResult.ButtonHit(headerKey),
                _ => State.ExpandedDeviceSettingsUri = isOpen ? null : paneKey);
            RenderLayout(camToggle, new RectF32(x + padding, cursor, w - padding * 2f, rowH), fontPath, dpiScale);
            cursor += rowH;

            if (!isOpen) return cursor;

            // Latest sample (may be null on first frame after connect)
            State.CameraTelemetry.TryGetValue(key, out var buffer);
            var latest = buffer?.Latest;

            // ---- Readout row: 4 fixed-share cells so labels never overflow ----
            var readoutH = rowH;
            var readoutBg = FilterRowAlt;
            var rowX = x + padding;
            var rowW = w - padding * 2f;
            FillRect(rowX, cursor, rowW, readoutH, readoutBg);

            string Cell(double? v, string suffix) => v is { } d ? $"{d:F1}{suffix}" : "--";
            var cellW = rowW / 4f;
            var cellPad = padding;
            var cellFs = fontSize * 0.85f;
            var tempStr = $"CCD: {Cell(latest?.CcdTempC, "\u00b0C")}";
            var setStr  = $"Set: {Cell(latest?.SetpointC, "\u00b0C")}";
            var pwrStr  = latest?.CoolerPowerPct is { } p ? $"Power: {p:F0}%" : "Power: --";
            var stateStr = latest is null ? "\u2026"
                : (latest.Value.CoolerOn ? "Cooler ON" : "Cooler OFF");

            // Belt-and-suspenders: still ellipsize per cell in case the panel shrinks below threshold.
            var inner = cellW - cellPad;
            var t = TruncateToWidth(tempStr,  fontPath, cellFs, inner);
            var s = TruncateToWidth(setStr,   fontPath, cellFs, inner);
            var pw = TruncateToWidth(pwrStr,   fontPath, cellFs, inner);
            var st = TruncateToWidth(stateStr, fontPath, cellFs, inner);
            DrawText(t.AsSpan(),  fontPath, rowX + cellPad,                 cursor, inner, readoutH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            DrawText(s.AsSpan(),  fontPath, rowX + cellW + cellPad,         cursor, inner, readoutH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            DrawText(pw.AsSpan(), fontPath, rowX + cellW * 2f + cellPad,    cursor, inner, readoutH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            DrawText(st.AsSpan(), fontPath, rowX + cellW * 3f + cellPad,    cursor, inner, readoutH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            cursor += readoutH;

            // ---- Controls row: setpoint input + [Cool to Setpoint] + [Cooler Off] ----
            var controlsH = rowH;
            FillRect(x + padding, cursor, w - padding * 2f, controlsH, FilterTableBg);
            var labelW = 80f * dpiScale;
            DrawText("    Setpoint:".AsSpan(), fontPath,
                x + padding, cursor, labelW, controlsH,
                fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);

            var inputX = x + padding + labelW;
            var inputW = 70f * dpiScale;
            // Lazy-init the setpoint input for this URI; default text from the latest sample.
            if (!State.CameraSetpointInputs.TryGetValue(key, out var setpointInput))
            {
                setpointInput = new TextInputState { Placeholder = "-10" };
                if (latest?.SetpointC is { } sp) setpointInput.Text = sp.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                State.CameraSetpointInputs[key] = setpointInput;
            }
            RenderTextInput(setpointInput,
                (int)inputX, (int)cursor,
                (int)inputW, (int)controlsH,
                fontPath, fontSize * 0.85f);

            var btnGap = 4f * dpiScale;
            var coolBtnX = inputX + inputW + btnGap;
            var coolBtnW = 110f * dpiScale;
            var capUri = cameraUri;
            RenderButton("Cool to Setpoint", coolBtnX, cursor, coolBtnW, controlsH, fontPath, fontSize * 0.78f,
                CreateButton, BodyText, $"CoolTo_{key}",
                _ =>
                {
                    var txt = setpointInput.Text;
                    if (double.TryParse(txt, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var v))
                    {
                        PostSignal(new SetCoolerSetpointSignal(capUri, v));
                    }
                });

            var offBtnX = coolBtnX + coolBtnW + btnGap;
            var offBtnW = 80f * dpiScale;
            // Safety: tint the Cooler Off button red when the cooler is on, and route
            // the click to the confirmation strip instead of immediate cooler-off
            // (same condensation/thermal-shock concern as disconnect).
            var coolerUnsafe = latest is { } l && l.CoolerOn;
            var offBg = coolerUnsafe ? ConfirmDangerBg : EditButtonBg;
            RenderButton("Cooler Off", offBtnX, cursor, offBtnW, controlsH, fontPath, fontSize * 0.78f,
                offBg, BodyText, $"CoolerOff_{key}",
                _ =>
                {
                    if (coolerUnsafe)
                    {
                        State.PendingCoolerOffConfirm = capUri;
                        State.PendingCoolerOffForceConfirm = null;
                    }
                    else
                    {
                        PostSignal(new SetCoolerOffSignal(capUri));
                    }
                });
            cursor += controlsH;

            // Confirmation strip -- appears under the controls row when the user clicked
            // Cooler Off on a cooled camera. Two stages, mirror of the disconnect flow.
            if (DeviceBase.SameDevice(State.PendingCoolerOffForceConfirm, cameraUri))
            {
                cursor = RenderCoolerOffForceStrip(cameraUri, x + padding, cursor, w - padding * 2f, controlsH, fontPath, dpiScale);
            }
            else if (DeviceBase.SameDevice(State.PendingCoolerOffConfirm, cameraUri))
            {
                cursor = RenderCoolerOffConfirmStrip(cameraUri, x + padding, cursor, w - padding * 2f, controlsH, fontPath, dpiScale);
            }

            // ---- Sparkline graph ----
            var graphH = 60f * dpiScale;
            var graphX = x + padding;
            var graphW = w - padding * 2f;
            FillRect(graphX, cursor, graphW, graphH, FilterTableBg);
            if (buffer is not null && buffer.Count >= 2)
            {
                RenderTemperatureSparkline(buffer, graphX, cursor, graphW, graphH, fontPath, fontSize);
            }
            else
            {
                DrawText("(sampling -- graph appears after a few seconds)".AsSpan(), fontPath,
                    graphX, cursor, graphW, graphH,
                    fontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center);
            }
            cursor += graphH + padding * 0.5f;

            return cursor;
        }

        /// <summary>
        /// Renders the mount status panel (RA/Dec/Slewing/Tracking) for the given mount URI,
        /// but only when the mount is currently hub-connected. Hidden otherwise.
        /// State comes from the single canonical <see cref="LiveSessionState.MountState"/>, populated
        /// by <c>AppSignalHandler.PollPreviewTelemetry</c> while idle (equipment / sky-map / live-session
        /// tabs visible) and by the running session's poll otherwise. The expander gives the user a
        /// visible answer to "is the mount tracking?" without having to leave the equipment tab.
        /// </summary>
        private float RenderMountTelemetryIfAny(
            GuiAppState appState,
            Uri? mountUri,
            LiveSessionState? liveSessionState,
            float x, float cursor, float w, float itemH,
            float dpiScale, string fontPath, float fontSize, float padding)
        {
            if (mountUri is null || mountUri == NoneDevice.Instance.DeviceUri) return cursor;
            if (appState.DeviceHub is not { } hub || !hub.IsConnected(mountUri)) return cursor;
            if (liveSessionState is null) return cursor;

            var key = mountUri.GetLeftPart(UriPartial.Path);
            var rowH = itemH * 0.9f;
            var headerKey = $"MountStatus_{key}";
            var paneKey = $"MountStatusPane:{key}";
            var isOpen = State.ExpandedDeviceSettingsUri == paneKey;
            var headerLabel = isOpen ? "    Mount Status [-]" : "    Mount Status [+]";

            var mountToggle = FormRowLayout.ToggleHeaderRow(
                headerLabel, rowH, FilterTableBg, HeaderText, BaseFontSize * 0.85f,
                new HitResult.ButtonHit(headerKey),
                _ => State.ExpandedDeviceSettingsUri = isOpen ? null : paneKey);
            RenderLayout(mountToggle, new RectF32(x + padding, cursor, w - padding * 2f, rowH), fontPath, dpiScale);
            cursor += rowH;

            if (!isOpen) return cursor;

            // Snapshot the mount state once -- the field is published atomically via Interlocked.Exchange.
            var ms = liveSessionState.MountState;

            // ---- Status badge row: Slewing | Tracking | Idle, colour-coded.
            var badgeRowH = rowH;
            var rowX = x + padding;
            var rowW = w - padding * 2f;
            FillRect(rowX, cursor, rowW, badgeRowH, FilterRowAlt);

            string statusLabel;
            RGBAColor32 statusColor;
            if (ms.IsSlewing)
            {
                statusLabel = "Slewing";
                statusColor = new RGBAColor32(0xff, 0xc8, 0x66, 0xff); // amber
            }
            else if (ms.IsTracking)
            {
                statusLabel = "Tracking";
                statusColor = new RGBAColor32(0x66, 0xdd, 0x66, 0xff); // green
            }
            else
            {
                statusLabel = "Idle";
                statusColor = DimText;
            }

            var labelW = 80f * dpiScale;
            DrawText("    Status:".AsSpan(), fontPath,
                rowX, cursor, labelW, badgeRowH,
                fontSize * 0.85f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText(statusLabel.AsSpan(), fontPath,
                rowX + labelW, cursor, rowW - labelW, badgeRowH,
                fontSize * 0.95f, statusColor, TextAlign.Near, TextAlign.Center);
            cursor += badgeRowH;

            // ---- Coordinate readout row: 3 cells (RA / Dec / HA) ----
            var coordRowH = rowH;
            FillRect(rowX, cursor, rowW, coordRowH, FilterTableBg);

            string FormatRa(double hours)
            {
                if (double.IsNaN(hours)) return "--";
                var h = (int)hours;
                var mFloat = (hours - h) * 60.0;
                var m = (int)mFloat;
                var s = (mFloat - m) * 60.0;
                return $"{h:00}h{m:00}m{s:00.0}s";
            }
            string FormatDec(double deg)
            {
                if (double.IsNaN(deg)) return "--";
                var sign = deg < 0 ? "-" : "+";
                deg = Math.Abs(deg);
                var d = (int)deg;
                var mFloat = (deg - d) * 60.0;
                var m = (int)mFloat;
                var s = (mFloat - m) * 60.0;
                return $"{sign}{d:00}\u00b0{m:00}'{s:00.0}\"";
            }
            string FormatHa(double hours)
            {
                if (double.IsNaN(hours)) return "--";
                var sign = hours < 0 ? "-" : "+";
                hours = Math.Abs(hours);
                var h = (int)hours;
                var m = (int)((hours - h) * 60.0);
                return $"{sign}{h:00}h{m:00}m";
            }

            var raStr = $"RA: {FormatRa(ms.RightAscension)}";
            var decStr = $"Dec: {FormatDec(ms.Declination)}";
            var haStr = $"HA: {FormatHa(ms.HourAngle)}";
            var cellW = rowW / 3f;
            var cellPad = padding;
            var cellFs = fontSize * 0.85f;
            var inner = cellW - cellPad;
            var ra = TruncateToWidth(raStr, fontPath, cellFs, inner);
            var dc = TruncateToWidth(decStr, fontPath, cellFs, inner);
            var ha = TruncateToWidth(haStr, fontPath, cellFs, inner);
            DrawText(ra.AsSpan(), fontPath, rowX + cellPad,                  cursor, inner, coordRowH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            DrawText(dc.AsSpan(), fontPath, rowX + cellW + cellPad,          cursor, inner, coordRowH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            DrawText(ha.AsSpan(), fontPath, rowX + cellW * 2f + cellPad,     cursor, inner, coordRowH, cellFs, BodyText, TextAlign.Near, TextAlign.Center);
            cursor += coordRowH + padding * 0.5f;

            return cursor;
        }

        /// <summary>
        /// Draws a CCD-temperature line + setpoint reference line in the given rect.
        /// Y-axis spans observed min/max with a small margin; X-axis is sample order.
        /// </summary>
        private void RenderTemperatureSparkline(
            CameraTelemetryBuffer buffer,
            float x, float y, float w, float h,
            string fontPath, float fontSize)
        {
            // Collect points
            var samples = new List<CameraTelemetrySample>(buffer.Count);
            foreach (var s in buffer.InOrder()) samples.Add(s);
            if (samples.Count < 2) return;

            double minT = double.PositiveInfinity, maxT = double.NegativeInfinity;
            foreach (var s in samples)
            {
                if (s.CcdTempC is { } t) { if (t < minT) minT = t; if (t > maxT) maxT = t; }
                if (s.SetpointC is { } sp) { if (sp < minT) minT = sp; if (sp > maxT) maxT = sp; }
            }
            if (double.IsInfinity(minT) || double.IsInfinity(maxT)) return;
            if (Math.Abs(maxT - minT) < 0.5) { var mid = (minT + maxT) * 0.5; minT = mid - 0.5; maxT = mid + 0.5; }

            // Add a small vertical margin.
            var range = maxT - minT;
            minT -= range * 0.1; maxT += range * 0.1; range = maxT - minT;

            // Inset for axis labels on the left
            var labelW = 36f;
            var plotX = x + labelW;
            var plotW = w - labelW - 4f;
            var plotY = y + 4f;
            var plotH = h - 8f;

            // Axis label (min and max temperature)
            DrawText($"{maxT:F1}".AsSpan(), fontPath, x, plotY - 6f, labelW - 4f, 14f, fontSize * 0.7f, DimText, TextAlign.Far, TextAlign.Center);
            DrawText($"{minT:F1}".AsSpan(), fontPath, x, plotY + plotH - 8f, labelW - 4f, 14f, fontSize * 0.7f, DimText, TextAlign.Far, TextAlign.Center);

            // Setpoint reference line (dashed effect via short rects)
            var lastSp = samples[samples.Count - 1].SetpointC;
            if (lastSp is { } setPt)
            {
                var spY = plotY + (float)((maxT - setPt) / range) * plotH;
                for (var dx = 0f; dx < plotW; dx += 6f)
                {
                    FillRect(plotX + dx, spY, 3f, 1f, ReachDisconnected);
                }
            }

            // Plot CCD temp as connected line segments (one tiny rect per pair).
            for (var i = 1; i < samples.Count; i++)
            {
                if (samples[i - 1].CcdTempC is not { } t0) continue;
                if (samples[i].CcdTempC is not { } t1) continue;
                var x0 = plotX + (i - 1) * plotW / (samples.Count - 1);
                var x1 = plotX + i * plotW / (samples.Count - 1);
                var y0 = plotY + (float)((maxT - t0) / range) * plotH;
                var y1 = plotY + (float)((maxT - t1) / range) * plotH;
                DrawLineSegment(x0, y0, x1, y1, ReachConnected);
            }
        }

        /// <summary>
        /// Cheap line segment via a chain of 1px FillRects -- adequate for a sparkline.
        /// </summary>
        private void DrawLineSegment(float x0, float y0, float x1, float y1, RGBAColor32 color)
        {
            var dx = x1 - x0; var dy = y1 - y0;
            var steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy)));
            if (steps < 1) steps = 1;
            for (var i = 0; i <= steps; i++)
            {
                var t = (float)i / steps;
                FillRect(x0 + dx * t, y0 + dy * t, 1.5f, 1.5f, color);
            }
        }

    }
}
