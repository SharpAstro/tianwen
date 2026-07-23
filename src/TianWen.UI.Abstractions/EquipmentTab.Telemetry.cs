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
        /// Builds the camera cooler control + telemetry sparkline as one profile-panel section node,
        /// but only when the camera is currently connected via the device hub (else null = no section).
        /// Layout: collapsible header -> readout row -> setpoint input + buttons -> optional confirm
        /// strip -> temp sparkline. The setpoint text-input and the sparkline are keyed <c>Fill</c>
        /// leaves painted through <see cref="_profilePanelFills"/> from the single panel RenderLayout.
        /// </summary>
        private Layout.Node? BuildCameraTelemetry(
            GuiAppState appState, Uri? cameraUri, float innerW)
        {
            if (cameraUri is null || cameraUri == NoneDevice.Instance.DeviceUri) return null;
            if (appState.DeviceHub is not { } hub || !hub.IsConnected(cameraUri)) return null;

            var dpiScale = DpiScale;
            var fontPath = FontPath;
            var key = cameraUri.GetLeftPart(UriPartial.Path);
            var fontSize = BaseFontSize * dpiScale;
            float rowH = BaseItemHeight * 0.9f;   // design units
            var headerKey = $"CamCool_{key}";

            // Toggle header -- independent of the device-settings expand state.
            // Stored on EquipmentTabState as a separate sub-key so it doesn't clash.
            var paneKey = $"CoolerPane:{key}";
            var isOpen = State.ExpandedDeviceSettingsUri == paneKey;
            var headerLabel = isOpen ? "    Cooler Control [-]" : "    Cooler Control [+]";

            var toggle = FormRowLayout.ToggleHeaderRow(
                headerLabel, rowH, FilterTableBg, HeaderText, BaseFontSize * 0.85f,
                new HitResult.ButtonHit(headerKey),
                _ => State.ExpandedDeviceSettingsUri = isOpen ? null : paneKey);

            if (!isOpen) return toggle;

            var rows = new List<Layout.Node> { toggle };

            // Latest sample (may be null on first frame after connect)
            State.CameraTelemetry.TryGetValue(key, out var buffer);
            var latest = buffer?.Latest;

            // ---- Readout row: 4 equal cells (CCD / setpoint / power / state) as one HStack. Cells stay
            // truncated so a long coord can't bleed into the next cell (the engine's Text leaf never clips). ----
            string Fmt(double? v, string suffix) => v is { } d ? $"{d:F1}{suffix}" : "--";
            var cellW = innerW / 4f;
            var cellFs = fontSize * 0.85f;
            Layout.Node Cell(string s) =>
                Layout.Builder.Text(TruncateToWidth(s, cellFs, cellW), BaseFontSize * 0.85f, BodyText).WStar().HStar();

            rows.Add(Layout.Builder.HStack(
                    Cell($"CCD: {Fmt(latest?.CcdTempC, "\u00b0C")}"),
                    Cell($"Set: {Fmt(latest?.SetpointC, "\u00b0C")}"),
                    Cell(latest?.CoolerPowerPct is { } p ? $"Power: {p:F0}%" : "Power: --"),
                    Cell(latest is null ? "\u2026" : latest.Value.CoolerOn ? "Cooler ON" : "Cooler OFF"))
                .RowH(rowH).Bg(FilterRowAlt));

            // ---- Controls row: [Setpoint: | input | Cool to Setpoint | Cooler Off] as one HStack. ----
            var capUri = cameraUri;
            // Lazy-init the setpoint input for this URI; default text from the latest sample.
            if (!State.CameraSetpointInputs.TryGetValue(key, out var setpointInput))
            {
                setpointInput = new TextInputState { Placeholder = "-10" };
                if (latest?.SetpointC is { } sp) setpointInput.Text = sp.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                State.CameraSetpointInputs[key] = setpointInput;
            }

            // Safety: tint the Cooler Off button red when the cooler is on, and route the click to the
            // confirmation strip instead of immediate cooler-off (condensation/thermal-shock concern).
            var coolerUnsafe = latest is { } l && l.CoolerOn;
            var setpointFillKey = $"coolerSetpoint:{key}";
            _profilePanelFills[setpointFillKey] =
                r => RenderTextInput(setpointInput, r, fontPath, fontSize * 0.85f);

            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text("    Setpoint:", BaseFontSize * 0.85f, DimText).WFixed(80f).HStar(),
                    Layout.Builder.Fill(key: setpointFillKey).WFixed(70f).HStar(),
                    Layout.Builder.Text("Cool to Setpoint", BaseFontSize * 0.78f, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(110f).HStar().Bg(CreateButton)
                        .Clickable(new HitResult.ButtonHit($"CoolTo_{key}"), _ =>
                        {
                            if (double.TryParse(setpointInput.Text, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                            {
                                PostSignal(new SetCoolerSetpointSignal(capUri, v));
                            }
                        }),
                    Layout.Builder.Text("Cooler Off", BaseFontSize * 0.78f, BodyText, TextAlign.Center, TextAlign.Center)
                        .WFixed(80f).HStar().Bg(coolerUnsafe ? ConfirmDangerBg : EditButtonBg)
                        .Clickable(new HitResult.ButtonHit($"CoolerOff_{key}"), _ =>
                        {
                            if (coolerUnsafe) { State.PendingCoolerOffConfirm = capUri; State.PendingCoolerOffForceConfirm = null; }
                            else PostSignal(new SetCoolerOffSignal(capUri));
                        }))
                .WithGap(4f).RowH(rowH).Bg(FilterTableBg));

            // Confirmation strip -- appears under the controls row when the user clicked
            // Cooler Off on a cooled camera. Two stages, mirror of the disconnect flow.
            if (DeviceBase.SameDevice(State.PendingCoolerOffForceConfirm, cameraUri))
            {
                rows.Add(BuildCoolerOffForceStrip(cameraUri).RowH(rowH));
            }
            else if (DeviceBase.SameDevice(State.PendingCoolerOffConfirm, cameraUri))
            {
                rows.Add(BuildCoolerOffConfirmStrip(cameraUri).RowH(rowH));
            }

            // ---- Sparkline graph (keyed Fill leaf; raster painter draws the graph or the sampling hint) ----
            var graphFs = fontSize;
            var sparkKey = $"camSpark:{key}";
            _profilePanelFills[sparkKey] = r =>
            {
                // Background is the Fill leaf's own .Bg (painted by the engine before this callback);
                // never FillRect it here -- RenderLayout/paint must not re-enter inside a drawFill callback.
                if (buffer is not null && buffer.Count >= 2)
                {
                    RenderTemperatureSparkline(buffer, r.X, r.Y, r.Width, r.Height, graphFs);
                }
                else
                {
                    DrawText("(sampling -- graph appears after a few seconds)".AsSpan(), fontPath,
                        r.X, r.Y, r.Width, r.Height, graphFs * 0.8f, DimText, TextAlign.Center, TextAlign.Center);
                }
            };
            rows.Add(Layout.Builder.Fill(key: sparkKey).RowH(60f).Bg(FilterTableBg));
            rows.Add(Layout.Builder.Spacer().RowH(BasePadding * 0.5f));

            return Layout.Builder.VStack([.. rows]).WStar();
        }

        /// <summary>
        /// Builds the mount status section (RA/Dec/Slewing/Tracking) for the given mount URI as one
        /// profile-panel node, but only when the mount is currently hub-connected (else null).
        /// State comes from the single canonical <see cref="LiveSessionState.MountState"/>, populated
        /// by <c>AppSignalHandler.PollPreviewTelemetry</c> while idle (equipment / sky-map / live-session
        /// tabs visible) and by the running session's poll otherwise. The expander gives the user a
        /// visible answer to "is the mount tracking?" without having to leave the equipment tab.
        /// </summary>
        private Layout.Node? BuildMountTelemetry(
            GuiAppState appState, Uri? mountUri, LiveSessionState? liveSessionState,
            float innerW)
        {
            if (mountUri is null || mountUri == NoneDevice.Instance.DeviceUri) return null;
            if (appState.DeviceHub is not { } hub || !hub.IsConnected(mountUri)) return null;
            if (liveSessionState is null) return null;

            var dpiScale = DpiScale;
            var key = mountUri.GetLeftPart(UriPartial.Path);
            var fontSize = BaseFontSize * dpiScale;
            float rowH = BaseItemHeight * 0.9f;   // design units
            var headerKey = $"MountStatus_{key}";
            var paneKey = $"MountStatusPane:{key}";
            var isOpen = State.ExpandedDeviceSettingsUri == paneKey;
            var headerLabel = isOpen ? "    Mount Status [-]" : "    Mount Status [+]";

            var toggle = FormRowLayout.ToggleHeaderRow(
                headerLabel, rowH, FilterTableBg, HeaderText, BaseFontSize * 0.85f,
                new HitResult.ButtonHit(headerKey),
                _ => State.ExpandedDeviceSettingsUri = isOpen ? null : paneKey);

            if (!isOpen) return toggle;

            // Snapshot the mount state once -- the field is published atomically via Interlocked.Exchange.
            var ms = liveSessionState.MountState;

            var (statusLabel, statusColor) =
                  ms.IsSlewing  ? ("Slewing",  new RGBAColor32(0xff, 0xc8, 0x66, 0xff))  // amber
                : ms.IsTracking ? ("Tracking", new RGBAColor32(0x66, 0xdd, 0x66, 0xff))  // green
                :                 ("Idle",     DimText);

            // Status badge row + RA/Dec/HA readout. Cells stay truncated so a long coord can't bleed.
            var cellW = innerW / 3f;
            var cellFs = fontSize * 0.85f;
            Layout.Node Coord(string s) =>
                Layout.Builder.Text(TruncateToWidth(s, cellFs, cellW), BaseFontSize * 0.85f, BodyText).WStar().HStar();

            var readout = Layout.Builder.VStack(
                toggle,
                Layout.Builder.HStack(
                        Layout.Builder.Text("    Status:", BaseFontSize * 0.85f, DimText).WFixed(80f).HStar(),
                        Layout.Builder.Text(statusLabel, BaseFontSize * 0.95f, statusColor).Stretch())
                    .RowH(rowH).Bg(FilterRowAlt),
                Layout.Builder.HStack(
                        Coord($"RA: {FormatRa(ms.RightAscension)}"),
                        Coord($"Dec: {FormatDec(ms.Declination)}"),
                        Coord($"HA: {FormatHa(ms.HourAngle)}"))
                    .RowH(rowH).Bg(FilterTableBg),
                Layout.Builder.Spacer().RowH(BasePadding * 0.5f)).WStar();

            return readout;

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
        }

        /// <summary>
        /// Draws a CCD-temperature line + setpoint reference line in the given rect.
        /// Y-axis spans observed min/max with a small margin; X-axis is sample order.
        /// </summary>
        private void RenderTemperatureSparkline(
            CameraTelemetryBuffer buffer,
            float x, float y, float w, float h,
            float fontSize)
        {
            var fontPath = FontPath;

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
