using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic live session monitor tab. Shows session phase, device status,
    /// guide graph, guide RMS, focus history, and exposure log.
    /// </summary>
    public class LiveSessionTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        /// <summary>The live session state for keyboard handling. Set during Render.</summary>
        public LiveSessionState? State { get; set; }

        // Layout constants (at 1x scale)
        private const float BaseFontSize       = 14f;
        private const float BaseTopStripHeight = 36f;
        private const float BaseBotStripHeight = 28f;
        private const float BaseLeftPanelW     = 220f;
        private const float BaseRightPanelW    = 260f;
        private const float BaseGuideRmsH      = 20f;
        private const float BasePadding        = 6f;
        private const float BaseRowHeight      = 20f;

        // Colors
        private static readonly RGBAColor32 ContentBg        = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PanelBg          = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 HeaderBg         = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
        private static readonly RGBAColor32 HeaderText       = new RGBAColor32(0x88, 0xaa, 0xdd, 0xff);
        private static readonly RGBAColor32 BodyText         = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 DimText          = new RGBAColor32(0x88, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 SeparatorColor   = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
        private static readonly RGBAColor32 GraphBg          = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);
        private static readonly RGBAColor32 RaColor          = new RGBAColor32(0x44, 0x88, 0xff, 0xff); // blue
        private static readonly RGBAColor32 DecColor         = new RGBAColor32(0xff, 0x88, 0x44, 0xff); // orange
        private static readonly RGBAColor32 AbortBg          = new RGBAColor32(0xcc, 0x33, 0x33, 0xff);
        private static readonly RGBAColor32 AbortText        = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 ConfirmStripBg   = new RGBAColor32(0x88, 0x22, 0x22, 0xff);
        private static readonly RGBAColor32 RowAltBg         = new RGBAColor32(0x1a, 0x1a, 0x24, 0xff);

        /// <summary>Render the complete live session tab.</summary>
        public void Render(
            LiveSessionState state,
            RectF32 contentRect,
            float dpiScale,
            string fontPath,
            TimeProvider timeProvider)
        {
            State = state;
            BeginFrame();

            var fs = BaseFontSize * dpiScale;
            var topH = BaseTopStripHeight * dpiScale;
            var botH = BaseBotStripHeight * dpiScale;
            var leftW = BaseLeftPanelW * dpiScale;
            var rightW = BaseRightPanelW * dpiScale;
            var guideRmsH = BaseGuideRmsH * dpiScale;
            var pad = BasePadding * dpiScale;
            var rowH = BaseRowHeight * dpiScale;

            // Poll session to update cached fields
            state.PollSession();

            // Background
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            // Top strip
            var topRect = new RectF32(contentRect.X, contentRect.Y, contentRect.Width, topH);
            RenderTopStrip(state, topRect, fontPath, fs, dpiScale, timeProvider);

            // Bottom strip
            var botRect = new RectF32(contentRect.X, contentRect.Y + contentRect.Height - botH, contentRect.Width, botH);
            RenderBottomStrip(state, botRect, fontPath, fs, dpiScale);

            // Main area between top and bottom strips
            var mainY = contentRect.Y + topH;
            var mainH = contentRect.Height - topH - botH;

            // Left panel: device status
            var leftRect = new RectF32(contentRect.X, mainY, leftW, mainH);
            RenderDeviceStatusPanel(state, leftRect, fontPath, fs, pad, rowH);

            // Right panel: exposure log
            var rightRect = new RectF32(contentRect.X + contentRect.Width - rightW, mainY, rightW, mainH);
            RenderExposureLog(state, rightRect, fontPath, fs, pad, rowH);

            // Center area: phase-dependent content
            var centerX = contentRect.X + leftW;
            var centerW = contentRect.Width - leftW - rightW;
            if (centerW > 0)
            {
                if (state.Phase is SessionPhase.Cooling or SessionPhase.Finalising && state.CoolingSamples.Count > 0)
                {
                    // During cooling: show cooling graph (full center area)
                    var coolingRect = new RectF32(centerX, mainY, centerW, mainH);
                    RenderCoolingGraph(state, coolingRect, fontPath, fs, pad);
                }
                else
                {
                    // Guide graph: top 55%
                    var guideH = mainH * 0.55f;
                    var guideRect = new RectF32(centerX, mainY, centerW, guideH);
                    RenderGuideGraph(state, guideRect, fontPath, fs, pad);

                    // Guide RMS strip
                    var rmsRect = new RectF32(centerX, mainY + guideH, centerW, guideRmsH);
                    RenderGuideRmsStrip(state, rmsRect, fontPath, fs * 0.9f);

                    // Focus history: remaining space
                    var focusY = mainY + guideH + guideRmsH;
                    var focusH = mainH - guideH - guideRmsH;
                    var focusRect = new RectF32(centerX, focusY, centerW, focusH);
                    RenderFocusHistory(state, focusRect, fontPath, fs, pad, rowH);
                }
            }

            // Abort confirmation overlay
            if (state.ShowAbortConfirm)
            {
                RenderAbortConfirm(contentRect, fontPath, fs * 1.1f, dpiScale);
            }
        }

        /// <inheritdoc/>
        public override bool HandleInput(InputEvent evt)
        {
            if (State is not { } state)
            {
                return false;
            }

            switch (evt)
            {
                case InputEvent.KeyDown(InputKey.Escape, _) when state.ShowAbortConfirm:
                    state.ShowAbortConfirm = false;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.Enter, _) when state.ShowAbortConfirm:
                    PostSignal(new ConfirmAbortSessionSignal());
                    state.ShowAbortConfirm = false;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.Escape, _) when state.IsRunning:
                    state.ShowAbortConfirm = true;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.Scroll(var scrollY, _, _, _):
                    state.ExposureLogScrollOffset = Math.Max(0, state.ExposureLogScrollOffset + (scrollY > 0 ? -1 : 1));
                    state.NeedsRedraw = true;
                    return true;

                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        // Top strip: [Phase pill]  Target: ...   [progress]
        // -----------------------------------------------------------------------

        private void RenderTopStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, TimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);

            var pad = BasePadding * dpiScale;
            var pillW = 140f * dpiScale;
            var pillH = rect.Height - pad * 2;
            var pillColor = LiveSessionActions.PhaseColor(state.Phase);
            var label = LiveSessionActions.PhaseLabel(state.Phase);

            // Phase pill
            FillRect(rect.X + pad, rect.Y + pad, pillW, pillH, pillColor);
            DrawText(label.AsSpan(), fontPath,
                rect.X + pad, rect.Y, pillW, rect.Height,
                fontSize * 0.9f, AbortText, TextAlign.Center, TextAlign.Center);

            // Phase-specific status with countdowns and details
            var targetLabel = LiveSessionActions.PhaseStatusText(state, timeProvider);
            DrawText(targetLabel.AsSpan(), fontPath,
                rect.X + pillW + pad * 2, rect.Y, rect.Width * 0.4f, rect.Height,
                fontSize, BodyText, TextAlign.Near, TextAlign.Center);

            // Progress: frames + exposure time
            var progressText = $"Frames: {state.TotalFramesWritten}  Exp: {LiveSessionActions.FormatDuration(state.TotalExposureTime)}";
            DrawText(progressText.AsSpan(), fontPath,
                rect.X + rect.Width * 0.55f, rect.Y, rect.Width * 0.4f, rect.Height,
                fontSize, DimText, TextAlign.Far, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Bottom strip: stats + ABORT button
        // -----------------------------------------------------------------------

        private void RenderBottomStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);

            var pad = BasePadding * dpiScale;

            // Stats left
            var obsIdx = state.CurrentObservationIndex;
            var obsCount = state.ActiveSession?.Observations.Count ?? 0;
            var statsText = $"Observation: {(obsIdx >= 0 ? obsIdx + 1 : 0)}/{obsCount}  Frames: {state.TotalFramesWritten}  Exp: {LiveSessionActions.FormatDuration(state.TotalExposureTime)}";
            DrawText(statsText.AsSpan(), fontPath,
                rect.X + pad, rect.Y, rect.Width * 0.7f, rect.Height,
                fontSize, BodyText, TextAlign.Near, TextAlign.Center);

            // ABORT button (only when running)
            if (state.IsRunning)
            {
                var abortW = 80f * dpiScale;
                var abortX = rect.X + rect.Width - abortW - pad;
                RenderButton("ABORT", abortX, rect.Y + 2 * dpiScale, abortW, rect.Height - 4 * dpiScale,
                    fontPath, fontSize, AbortBg, AbortText, "AbortSession",
                    _ => { state.ShowAbortConfirm = true; state.NeedsRedraw = true; });
            }
        }

        // -----------------------------------------------------------------------
        // Left panel: device status
        // -----------------------------------------------------------------------

        private void RenderDeviceStatusPanel(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float pad, float rowH)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            // Separator on right edge
            FillRect(rect.X + rect.Width - 1, rect.Y, 1, rect.Height, SeparatorColor);

            var y = rect.Y + pad;
            var textW = rect.Width - pad * 2;
            var smallFs = fontSize * 0.85f;

            // Header
            DrawText("Devices".AsSpan(), fontPath,
                rect.X + pad, y, textW, rowH,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
            y += rowH + pad;

            if (state.ActiveSession is not { } session)
            {
                DrawText("No session".AsSpan(), fontPath,
                    rect.X + pad, y, textW, rowH,
                    smallFs, DimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            var setup = session.Setup;

            // Mount
            DrawText("\u2316 Mount".AsSpan(), fontPath,
                rect.X + pad, y, textW, rowH,
                smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
            y += rowH;
            DrawText($"  {setup.Mount.Device.DisplayName}".AsSpan(), fontPath,
                rect.X + pad, y, textW, rowH,
                smallFs * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
            y += rowH;

            // Telescopes/cameras
            for (var i = 0; i < setup.Telescopes.Length && y < rect.Y + rect.Height - rowH; i++)
            {
                var ota = setup.Telescopes[i];
                y += pad;
                DrawText($"\u2609 {ota.Name}".AsSpan(), fontPath,
                    rect.X + pad, y, textW, rowH,
                    smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                DrawText($"  Cam: {ota.Camera.Device.DisplayName}".AsSpan(), fontPath,
                    rect.X + pad, y, textW, rowH,
                    smallFs * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                if (ota.Focuser is { } focuser)
                {
                    DrawText($"  Foc: {focuser.Device.DisplayName}".AsSpan(), fontPath,
                        rect.X + pad, y, textW, rowH,
                        smallFs * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                if (ota.FilterWheel is { } fw)
                {
                    DrawText($"  FW: {fw.Device.DisplayName}".AsSpan(), fontPath,
                        rect.X + pad, y, textW, rowH,
                        smallFs * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }
            }

            // Guider
            y += pad;
            DrawText("\u272A Guider".AsSpan(), fontPath,
                rect.X + pad, y, textW, rowH,
                smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
            y += rowH;
            DrawText($"  {setup.Guider.Device.DisplayName}".AsSpan(), fontPath,
                rect.X + pad, y, textW, rowH,
                smallFs * 0.9f, DimText, TextAlign.Near, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Center: guide graph (RA blue, Dec orange)
        // -----------------------------------------------------------------------

        private void RenderGuideGraph(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float pad)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

            // Header
            DrawText("Guide Graph".AsSpan(), fontPath,
                rect.X + pad, rect.Y, rect.Width - pad * 2, fontSize * 1.5f,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

            var samples = state.GuideSamples;
            if (samples.Count == 0)
            {
                DrawText("No guide data".AsSpan(), fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            // Graph area (inset)
            var graphX = rect.X + pad * 3;
            var graphY = rect.Y + fontSize * 2;
            var graphW = rect.Width - pad * 6;
            var graphH = rect.Height - fontSize * 2 - pad;

            if (graphW <= 0 || graphH <= 0)
            {
                return;
            }

            // Find max error for scaling (clamped to at least ±2 arcsec)
            var maxErr = 2.0;
            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                maxErr = Math.Max(maxErr, Math.Max(Math.Abs(s.RaError), Math.Abs(s.DecError)));
            }

            // Zero line
            var zeroY = graphY + graphH / 2;
            FillRect(graphX, zeroY, graphW, 1, SeparatorColor);

            // Draw axis labels
            DrawText($"+{maxErr:F1}\"".AsSpan(), fontPath,
                graphX, graphY, 50, fontSize,
                fontSize * 0.7f, DimText, TextAlign.Near, TextAlign.Center);
            DrawText($"-{maxErr:F1}\"".AsSpan(), fontPath,
                graphX, graphY + graphH - fontSize, 50, fontSize,
                fontSize * 0.7f, DimText, TextAlign.Near, TextAlign.Center);

            // Plot RA and Dec as thin FillRect segments
            var count = samples.Count;
            var stepX = graphW / Math.Max(count - 1, 1);

            for (var i = 0; i < count; i++)
            {
                var s = samples[i];
                var x = graphX + i * stepX;

                // RA (blue)
                var raYNorm = (float)(s.RaError / maxErr);
                var raPixY = zeroY - raYNorm * (graphH / 2);
                FillRect(x, Math.Min(raPixY, zeroY), Math.Max(stepX, 1), Math.Max(Math.Abs(raPixY - zeroY), 1), RaColor);

                // Dec (orange)
                var decYNorm = (float)(s.DecError / maxErr);
                var decPixY = zeroY - decYNorm * (graphH / 2);
                FillRect(x + stepX * 0.3f, Math.Min(decPixY, zeroY), Math.Max(stepX * 0.4f, 1), Math.Max(Math.Abs(decPixY - zeroY), 1), DecColor);
            }

            // Legend
            var legendY = rect.Y + pad;
            var legendX = rect.X + rect.Width - 120;
            FillRect(legendX, legendY, 10, 10, RaColor);
            DrawText("RA".AsSpan(), fontPath, legendX + 14, legendY, 30, 10,
                fontSize * 0.7f, RaColor, TextAlign.Near, TextAlign.Center);
            FillRect(legendX + 50, legendY, 10, 10, DecColor);
            DrawText("Dec".AsSpan(), fontPath, legendX + 64, legendY, 30, 10,
                fontSize * 0.7f, DecColor, TextAlign.Near, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Center: cooling graph (Y = temp + power%, X = time)
        // -----------------------------------------------------------------------

        // Per-camera color palette (temp = solid, power = same hue lighter)
        private static readonly RGBAColor32[] CameraTempColors =
        [
            new RGBAColor32(0x44, 0x88, 0xff, 0xff), // blue
            new RGBAColor32(0x44, 0xcc, 0x44, 0xff), // green
            new RGBAColor32(0xcc, 0x44, 0xcc, 0xff), // magenta
            new RGBAColor32(0xff, 0xcc, 0x44, 0xff), // yellow
        ];
        private static readonly RGBAColor32[] CameraPowerColors =
        [
            new RGBAColor32(0xff, 0x66, 0x44, 0xff), // orange-red
            new RGBAColor32(0xff, 0x88, 0x66, 0xff), // light orange
            new RGBAColor32(0xff, 0x66, 0x88, 0xff), // pink
            new RGBAColor32(0xff, 0xaa, 0x66, 0xff), // peach
        ];

        private void RenderCoolingGraph(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float pad)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

            // Header
            DrawText("Cooling Ramp".AsSpan(), fontPath,
                rect.X + pad, rect.Y, rect.Width - pad * 2, fontSize * 1.5f,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

            var samples = state.CoolingSamples;
            if (samples.Count < 2)
            {
                DrawText("Collecting data...".AsSpan(), fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            // Determine number of cameras from samples
            var maxCameraIndex = 0;
            for (var i = 0; i < samples.Count; i++)
            {
                if (samples[i].CameraIndex > maxCameraIndex) maxCameraIndex = samples[i].CameraIndex;
            }
            var cameraCount = maxCameraIndex + 1;

            // Legend — one row per camera
            var legendY = rect.Y + pad;
            var legendX = rect.X + rect.Width - 220;
            for (var cam = 0; cam < cameraCount; cam++)
            {
                var tempCol = CameraTempColors[cam % CameraTempColors.Length];
                var powCol = CameraPowerColors[cam % CameraPowerColors.Length];
                var ly = legendY + cam * (fontSize + 2);
                FillRect(legendX, ly + 2, 8, 8, tempCol);
                var camLabel = cameraCount > 1 ? $"Cam{cam} \u00B0C" : "Temp \u00B0C";
                DrawText(camLabel.AsSpan(), fontPath, legendX + 12, ly, 70, fontSize,
                    fontSize * 0.65f, tempCol, TextAlign.Near, TextAlign.Center);
                FillRect(legendX + 90, ly + 2, 8, 8, powCol);
                var powLabel = cameraCount > 1 ? $"Cam{cam} %" : "Power %";
                DrawText(powLabel.AsSpan(), fontPath, legendX + 102, ly, 70, fontSize,
                    fontSize * 0.65f, powCol, TextAlign.Near, TextAlign.Center);
            }

            // Graph area (inset)
            var graphX = rect.X + pad * 6;
            var graphY = rect.Y + fontSize * 2 + Math.Max(cameraCount - 1, 0) * (fontSize + 2);
            var graphW = rect.Width - pad * 8;
            var graphH = rect.Height - (graphY - rect.Y) - pad * 2;

            if (graphW <= 0 || graphH <= 0)
            {
                return;
            }

            // Find min/max temp and time range across all cameras
            var minTemp = double.MaxValue;
            var maxTemp = double.MinValue;
            var firstTime = samples[0].Timestamp;
            var lastTime = samples[^1].Timestamp;

            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (s.TemperatureC < minTemp) minTemp = s.TemperatureC;
                if (s.TemperatureC > maxTemp) maxTemp = s.TemperatureC;
            }

            // Pad temp range
            var tempRange = Math.Max(maxTemp - minTemp, 5.0);
            minTemp -= 2;
            maxTemp = minTemp + tempRange + 4;
            var timeRange = (lastTime - firstTime).TotalSeconds;
            if (timeRange < 1) timeRange = 1;

            // Y-axis labels for temperature (left)
            var firstTempCol = CameraTempColors[0];
            DrawText($"{maxTemp:F0}\u00B0".AsSpan(), fontPath,
                rect.X + pad, graphY, pad * 5, fontSize,
                fontSize * 0.7f, firstTempCol, TextAlign.Far, TextAlign.Center);
            DrawText($"{minTemp:F0}\u00B0".AsSpan(), fontPath,
                rect.X + pad, graphY + graphH - fontSize, pad * 5, fontSize,
                fontSize * 0.7f, firstTempCol, TextAlign.Far, TextAlign.Center);

            // Y-axis labels for power (right: 0-100%)
            var firstPowCol = CameraPowerColors[0];
            DrawText("100%".AsSpan(), fontPath,
                rect.X + rect.Width - pad * 5, graphY, pad * 4, fontSize,
                fontSize * 0.7f, firstPowCol, TextAlign.Near, TextAlign.Center);
            DrawText("0%".AsSpan(), fontPath,
                rect.X + rect.Width - pad * 5, graphY + graphH - fontSize, pad * 4, fontSize,
                fontSize * 0.7f, firstPowCol, TextAlign.Near, TextAlign.Center);

            // Plot samples — connect consecutive same-camera points with filled segments
            // Track last position per camera for line drawing
            var lastTempX = new float[cameraCount];
            var lastTempY = new float[cameraCount];
            var lastPowX = new float[cameraCount];
            var lastPowY = new float[cameraCount];
            var hasLast = new bool[cameraCount];

            for (var i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                var cam = s.CameraIndex;
                if (cam >= cameraCount) continue;
                var tempCol = CameraTempColors[cam % CameraTempColors.Length];
                var powCol = CameraPowerColors[cam % CameraPowerColors.Length];

                var tNorm = (float)((s.Timestamp - firstTime).TotalSeconds / timeRange);
                var x = graphX + tNorm * graphW;

                // Temperature
                var tempNorm = (float)((s.TemperatureC - minTemp) / (maxTemp - minTemp));
                var tempPixY = graphY + graphH - tempNorm * graphH;

                // Power
                var powerNorm = (float)(s.CoolerPowerPercent / 100.0);
                var powerPixY = graphY + graphH - powerNorm * graphH;

                if (hasLast[cam])
                {
                    // Draw horizontal then vertical segment to connect (step-style)
                    var segW = Math.Max(x - lastTempX[cam], 1);
                    FillRect(lastTempX[cam], lastTempY[cam], segW, 2, tempCol);
                    FillRect(x, Math.Min(lastTempY[cam], tempPixY), 2, Math.Abs(tempPixY - lastTempY[cam]) + 2, tempCol);

                    FillRect(lastPowX[cam], lastPowY[cam], segW, 2, powCol);
                    FillRect(x, Math.Min(lastPowY[cam], powerPixY), 2, Math.Abs(powerPixY - lastPowY[cam]) + 2, powCol);
                }
                else
                {
                    // First point — just a dot
                    FillRect(x, tempPixY, 3, 3, tempCol);
                    FillRect(x, powerPixY, 3, 3, powCol);
                }

                lastTempX[cam] = x;
                lastTempY[cam] = tempPixY;
                lastPowX[cam] = x;
                lastPowY[cam] = powerPixY;
                hasLast[cam] = true;
            }

            // Setpoint line — dashed, from the latest sample's setpoint per camera
            for (var cam = 0; cam < cameraCount; cam++)
            {
                // Find last sample for this camera
                var lastSetpoint = double.NaN;
                for (var i = samples.Count - 1; i >= 0; i--)
                {
                    if (samples[i].CameraIndex == cam)
                    {
                        lastSetpoint = samples[i].SetpointTempC;
                        break;
                    }
                }

                if (!double.IsNaN(lastSetpoint) && lastSetpoint != 0)
                {
                    var spNorm = (float)((lastSetpoint - minTemp) / (maxTemp - minTemp));
                    if (spNorm is >= 0 and <= 1)
                    {
                        var spY = graphY + graphH - spNorm * graphH;
                        var col = CameraTempColors[cam % CameraTempColors.Length];
                        // Dashed line
                        for (var dx = graphX; dx < graphX + graphW; dx += 8)
                        {
                            FillRect(dx, spY, 4, 1, col);
                        }
                        DrawText($"{lastSetpoint:F0}\u00B0".AsSpan(), fontPath,
                            rect.X + rect.Width - pad * 6, spY - fontSize / 2, pad * 5, fontSize,
                            fontSize * 0.65f, col, TextAlign.Near, TextAlign.Center);
                    }
                }
            }

            // Zero line for temperature reference
            var zeroNorm = (float)((0 - minTemp) / (maxTemp - minTemp));
            if (zeroNorm is >= 0 and <= 1)
            {
                var zeroY = graphY + graphH - zeroNorm * graphH;
                FillRect(graphX, zeroY, graphW, 1, SeparatorColor);
                DrawText("0\u00B0".AsSpan(), fontPath,
                    rect.X + pad, zeroY - fontSize / 2, pad * 4, fontSize,
                    fontSize * 0.65f, DimText, TextAlign.Far, TextAlign.Center);
            }
        }

        // -----------------------------------------------------------------------
        // Center: guide RMS strip
        // -----------------------------------------------------------------------

        private void RenderGuideRmsStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);
            var rmsText = LiveSessionActions.FormatGuideRms(state.LastGuideStats);
            DrawText(rmsText.AsSpan(), fontPath,
                rect.X + BasePadding, rect.Y, rect.Width - BasePadding * 2, rect.Height,
                fontSize, BodyText, TextAlign.Center, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Center: focus history
        // -----------------------------------------------------------------------

        private void RenderFocusHistory(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float pad, float rowH)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            // Header
            DrawText("Focus History".AsSpan(), fontPath,
                rect.X + pad, rect.Y, rect.Width - pad * 2, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

            var history = state.FocusHistory;
            if (history.Count == 0)
            {
                DrawText("No focus runs".AsSpan(), fontPath,
                    rect.X, rect.Y + rowH, rect.Width, rowH,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var y = rect.Y + rowH + pad;
            var visibleRows = (int)((rect.Height - rowH - pad) / rowH);
            var startIdx = Math.Max(0, history.Count - visibleRows); // show latest

            for (var i = startIdx; i < history.Count && y < rect.Y + rect.Height - rowH; i++)
            {
                var row = LiveSessionActions.FormatFocusHistoryRow(history[i]);
                var bg = (i % 2 == 0) ? PanelBg : RowAltBg;
                FillRect(rect.X, y, rect.Width, rowH, bg);
                DrawText(row.AsSpan(), fontPath,
                    rect.X + pad, y, rect.Width - pad * 2, rowH,
                    fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
                y += rowH;
            }
        }

        // -----------------------------------------------------------------------
        // Right panel: exposure log
        // -----------------------------------------------------------------------

        private void RenderExposureLog(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float pad, float rowH)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            // Separator on left edge
            FillRect(rect.X, rect.Y, 1, rect.Height, SeparatorColor);

            // Header
            DrawText("Exposure Log".AsSpan(), fontPath,
                rect.X + pad, rect.Y, rect.Width - pad * 2, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

            // Column headers
            var colY = rect.Y + rowH;
            FillRect(rect.X, colY, rect.Width, rowH, HeaderBg);
            DrawText("Time  Target       Filter  HFD".AsSpan(), fontPath,
                rect.X + pad, colY, rect.Width - pad * 2, rowH,
                fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);

            var log = state.ExposureLog;
            if (log.Count == 0)
            {
                DrawText("No frames yet".AsSpan(), fontPath,
                    rect.X, colY + rowH, rect.Width, rowH * 2,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var y = colY + rowH + pad;
            var visibleRows = (int)((rect.Height - rowH * 2 - pad * 2) / rowH);

            // Auto-scroll to latest
            if (state.ExposureLogScrollOffset < 0)
            {
                state.ExposureLogScrollOffset = 0;
            }

            var maxScroll = Math.Max(0, log.Count - visibleRows);
            // Show latest by default — scroll to bottom
            var startIdx = Math.Max(0, log.Count - visibleRows - state.ExposureLogScrollOffset);
            if (startIdx < 0)
            {
                startIdx = 0;
            }

            for (var i = startIdx; i < log.Count && y < rect.Y + rect.Height - rowH; i++)
            {
                var row = LiveSessionActions.FormatExposureLogRow(log[i]);
                var bg = (i % 2 == 0) ? PanelBg : RowAltBg;
                FillRect(rect.X, y, rect.Width, rowH, bg);
                DrawText(row.AsSpan(), fontPath,
                    rect.X + pad, y, rect.Width - pad * 2, rowH,
                    fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
                y += rowH;
            }
        }

        // -----------------------------------------------------------------------
        // Abort confirmation overlay
        // -----------------------------------------------------------------------

        private void RenderAbortConfirm(RectF32 contentRect, string fontPath, float fontSize, float dpiScale)
        {
            var stripH = 40f * dpiScale;
            var stripY = contentRect.Y + (contentRect.Height - stripH) / 2;

            // Semi-transparent backdrop (darken)
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                new RGBAColor32(0x00, 0x00, 0x00, 0x88));

            // Confirm strip
            FillRect(contentRect.X, stripY, contentRect.Width, stripH, ConfirmStripBg);
            DrawText("Abort session? Press Enter to confirm, Escape to cancel".AsSpan(), fontPath,
                contentRect.X, stripY, contentRect.Width, stripH,
                fontSize, AbortText, TextAlign.Center, TextAlign.Center);
        }
    }
}
