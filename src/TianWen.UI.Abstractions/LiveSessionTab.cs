using System;
using System.Collections.Generic;
using DIR.Lib;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Renderer-agnostic live session monitor tab. Shows session phase, timeline,
    /// per-OTA panels with exposure countdown, guide graph, and exposure log.
    /// </summary>
    public class LiveSessionTab<TSurface>(Renderer<TSurface> renderer) : PixelWidgetBase<TSurface>(renderer)
    {
        /// <summary>The live session state for keyboard handling. Set during Render.</summary>
        public LiveSessionState? State { get; set; }

        /// <summary>Optional mini viewer widget for showing the last captured frame. Set by the host.</summary>
        public IMiniViewerWidget? MiniViewer { get; set; }

        /// <summary>Tracks which image reference is currently displayed to avoid redundant uploads.</summary>
        private Image? _displayedImage;

        // Layout constants (at 1x scale)
        private const float BaseFontSize       = 14f;
        private const float BaseTopStripHeight = 36f;
        private const float BaseTimelineHeight = 60f;
        private const float BaseBotStripHeight = 56f;
        private const float BaseOtaPanelW       = 240f;
        private const float BaseRightPanelW    = 260f;
        private const float BaseGuideRmsH      = 20f;
        private const float BasePadding        = 6f;
        private const float BaseRowHeight      = 20f;
        private const float BaseProgressBarH   = 14f;

        // Colors
        private static readonly RGBAColor32 ContentBg        = new RGBAColor32(0x16, 0x16, 0x1e, 0xff);
        private static readonly RGBAColor32 PanelBg          = new RGBAColor32(0x1e, 0x1e, 0x28, 0xff);
        private static readonly RGBAColor32 HeaderBg         = new RGBAColor32(0x22, 0x22, 0x30, 0xff);
        private static readonly RGBAColor32 HeaderText       = new RGBAColor32(0x88, 0xaa, 0xdd, 0xff);
        private static readonly RGBAColor32 BodyText         = new RGBAColor32(0xcc, 0xcc, 0xcc, 0xff);
        private static readonly RGBAColor32 DimText          = new RGBAColor32(0x88, 0x88, 0x88, 0xff);
        private static readonly RGBAColor32 BrightText       = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 SeparatorColor   = new RGBAColor32(0x33, 0x33, 0x44, 0xff);
        private static readonly RGBAColor32 GraphBg          = new RGBAColor32(0x12, 0x12, 0x1a, 0xff);
        private static readonly RGBAColor32 RaColor          = new RGBAColor32(0x44, 0x88, 0xff, 0xff); // blue
        private static readonly RGBAColor32 DecColor         = new RGBAColor32(0xff, 0x88, 0x44, 0xff); // orange
        private static readonly RGBAColor32 AbortBg          = new RGBAColor32(0xcc, 0x33, 0x33, 0xff);
        private static readonly RGBAColor32 AbortText        = new RGBAColor32(0xff, 0xff, 0xff, 0xff);
        private static readonly RGBAColor32 ConfirmStripBg   = new RGBAColor32(0x88, 0x22, 0x22, 0xff);
        private static readonly RGBAColor32 RowAltBg         = new RGBAColor32(0x1a, 0x1a, 0x24, 0xff);
        private static readonly RGBAColor32 ProgressBg       = new RGBAColor32(0x2a, 0x2a, 0x3a, 0xff);
        private static readonly RGBAColor32 ProgressFill     = new RGBAColor32(0x30, 0x88, 0x30, 0xff);
        private static readonly RGBAColor32 NowNeedleColor   = new RGBAColor32(0xff, 0xff, 0xff, 0xcc);
        private static readonly RGBAColor32 TimelineBg       = new RGBAColor32(0x18, 0x18, 0x22, 0xff);
        private static readonly RGBAColor32 TimelineTickColor = new RGBAColor32(0x55, 0x55, 0x66, 0xff);

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
            var timelineH = BaseTimelineHeight * dpiScale;
            var botH = BaseBotStripHeight * dpiScale;
            var rightW = BaseRightPanelW * dpiScale;
            var pad = BasePadding * dpiScale;
            var rowH = BaseRowHeight * dpiScale;

            // Poll session to update cached fields
            state.PollSession();

            // Background
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height, ContentBg);

            // Top strip: phase pill + activity + clock
            var topRect = new RectF32(contentRect.X, contentRect.Y, contentRect.Width, topH);
            RenderTopStrip(state, topRect, fontPath, fs, dpiScale, timeProvider);

            // Timeline: phase bars + observation segments + now needle
            var timelineRect = new RectF32(contentRect.X, contentRect.Y + topH, contentRect.Width, timelineH);
            RenderTimeline(state, timelineRect, fontPath, fs, dpiScale, timeProvider);

            // Bottom strip: compact guide graph + RMS + ABORT
            var botRect = new RectF32(contentRect.X, contentRect.Y + contentRect.Height - botH, contentRect.Width, botH);
            RenderBottomStrip(state, botRect, fontPath, fs, dpiScale, pad, timeProvider);

            // Main area between timeline and bottom strip
            var mainY = contentRect.Y + topH + timelineH;
            var mainH = contentRect.Height - topH - timelineH - botH;

            // Left: per-OTA panels (fixed width per OTA)
            var otaCount = state.ActiveSession?.Setup.Telescopes.Length ?? 1;
            var otaTotalW = BaseOtaPanelW * dpiScale * otaCount;
            var otaRect = new RectF32(contentRect.X, mainY, otaTotalW, mainH);
            RenderOTAPanels(state, otaRect, fontPath, fs, dpiScale, pad, rowH, timeProvider);

            // Right: exposure log (fixed width)
            var logW = BaseRightPanelW * dpiScale;
            var logX = contentRect.X + contentRect.Width - logW;
            if (logW > 0)
            {
                var rightRect = new RectF32(logX, mainY, logW, mainH);
                RenderExposureLog(state, rightRect, fontPath, fs, pad, rowH);
            }

            // Center: mini viewer (between OTA panels and exposure log)
            var viewerX = contentRect.X + otaTotalW;
            var viewerW = contentRect.Width - otaTotalW - logW;
            if (viewerW > 100 && MiniViewer is { } viewer)
            {
                // Check if a new frame arrived
                var images = state.LastCapturedImages;
                // Show first available camera image (TODO: allow cycling with keyboard)
                Image? latestImage = null;
                for (var i = 0; i < images.Length; i++)
                {
                    if (images[i] is { } img)
                    {
                        latestImage = img;
                        break;
                    }
                }

                if (latestImage is not null && !ReferenceEquals(latestImage, _displayedImage))
                {
                    _displayedImage = latestImage;
                    viewer.QueueImage(latestImage);
                }

                // Toolbar at top of viewer area
                var toolbarH = BaseRowHeight * dpiScale;
                var toolbarRect = new RectF32(viewerX, mainY, viewerW, toolbarH);
                RenderMiniViewerToolbar(viewer.State, toolbarRect, fontPath, fs, dpiScale);

                // Image below toolbar
                var imageRect = new RectF32(viewerX, mainY + toolbarH, viewerW, mainH - toolbarH);
                viewer.Render(imageRect, Renderer.Width, Renderer.Height);
            }
            else if (viewerW > 0)
            {
                // Placeholder when no viewer or no image
                FillRect(viewerX, mainY, viewerW, mainH, GraphBg);
                if (state.Phase is SessionPhase.Observing)
                {
                    DrawText("Waiting for first frame\u2026".AsSpan(), fontPath,
                        viewerX, mainY, viewerW, mainH,
                        fs, DimText, TextAlign.Center, TextAlign.Center);
                }
            }

            // Abort confirmation overlay
            if (state.ShowAbortConfirm)
            {
                RenderAbortConfirm(contentRect, fontPath, fs * 1.1f, dpiScale);
            }
        }

        // -----------------------------------------------------------------------
        // Mini viewer toolbar: [Fit] [1:1] [T] [S] [B]
        // -----------------------------------------------------------------------

        private void RenderMiniViewerToolbar(MiniViewerState vs, RectF32 rect, string fontPath, float fontSize, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);

            var pad = BasePadding * dpiScale;
            var btnW = 36f * dpiScale;
            var btnH = rect.Height - 4 * dpiScale;
            var btnY = rect.Y + 2 * dpiScale;
            var btnFs = fontSize * 0.8f;
            var x = rect.X + pad;

            var activeBg = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
            var inactiveBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);

            // [Fit] — zoom to fit
            RenderButton("Fit", x, btnY, btnW, btnH, fontPath, btnFs,
                vs.ZoomToFit ? activeBg : inactiveBg, BodyText, "ViewerFit",
                _ => { vs.ZoomToFit = true; });
            x += btnW + pad;

            // [1:1] — actual pixels
            RenderButton("1:1", x, btnY, btnW, btnH, fontPath, btnFs,
                !vs.ZoomToFit && MathF.Abs(vs.Zoom - 1f) < 0.01f ? activeBg : inactiveBg, BodyText, "Viewer1to1",
                _ => { vs.ZoomToFit = false; vs.Zoom = 1f; vs.PanOffset = (0, 0); });
            x += btnW + pad;

            // [T] — cycle stretch mode
            var stretchLabel = vs.StretchMode switch
            {
                StretchMode.None => "Raw",
                StretchMode.Linked => "Lnk",
                StretchMode.Unlinked => "Unl",
                StretchMode.Luma => "Lum",
                _ => "T"
            };
            RenderButton(stretchLabel, x, btnY, btnW, btnH, fontPath, btnFs,
                vs.StretchMode is not StretchMode.None ? activeBg : inactiveBg, BodyText, "ViewerStretch",
                _ => { vs.CycleStretch(); });
            x += btnW + pad;

            // [S] — cycle stretch preset
            var presetLabel = $"{vs.StretchParameters}";
            RenderButton("S", x, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                inactiveBg, BodyText, "ViewerPreset",
                _ => { vs.CycleStretchPreset(); });
            x += btnW * 0.8f + pad;

            // [B] — cycle boost
            var boostActive = vs.CurvesBoost > 0;
            RenderButton("B", x, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                boostActive ? activeBg : inactiveBg, BodyText, "ViewerBoost",
                _ => { vs.CycleBoost(); });
            x += btnW * 0.8f + pad;

            // Status text: stretch info
            var infoText = $"{vs.StretchMode} {vs.StretchParameters}";
            if (vs.CurvesBoost > 0)
            {
                infoText += $" Boost:{vs.CurvesBoost:F2}";
            }
            DrawText(infoText.AsSpan(), fontPath,
                x, rect.Y, rect.X + rect.Width - x - pad, rect.Height,
                fontSize * 0.7f, DimText, TextAlign.Near, TextAlign.Center);
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

                // Mini viewer keyboard shortcuts
                case InputEvent.KeyDown(InputKey.F, _) when MiniViewer is { State: { } vs }:
                    vs.ZoomToFit = true;
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.R, _) when MiniViewer is { State: { } vs }:
                    vs.ZoomToFit = false;
                    vs.Zoom = 1f;
                    vs.PanOffset = (0, 0);
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.T, _) when MiniViewer is { State: { } vs }:
                    vs.CycleStretch();
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.B, _) when MiniViewer is { State: { } vs }:
                    vs.CycleBoost();
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.KeyDown(InputKey.S, _) when MiniViewer is { State: { } vs }:
                    vs.CycleStretchPreset();
                    state.NeedsRedraw = true;
                    return true;

                default:
                    return false;
            }
        }

        // -----------------------------------------------------------------------
        // Top strip: phase pill + activity + progress + clock
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

            // Activity text
            var targetLabel = LiveSessionActions.PhaseStatusText(state, timeProvider);
            DrawText(targetLabel.AsSpan(), fontPath,
                rect.X + pillW + pad * 2, rect.Y, rect.Width * 0.45f, rect.Height,
                fontSize, BodyText, TextAlign.Near, TextAlign.Center);

            // Progress: frames + exposure time
            var progressText = $"Frames: {state.TotalFramesWritten}  Exp: {LiveSessionActions.FormatDuration(state.TotalExposureTime)}";
            DrawText(progressText.AsSpan(), fontPath,
                rect.X + rect.Width * 0.55f, rect.Y, rect.Width * 0.4f, rect.Height,
                fontSize, DimText, TextAlign.Far, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Timeline: phase bars + now needle + time axis
        // -----------------------------------------------------------------------

        private void RenderTimeline(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, TimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, TimelineBg);

            var timeline = state.PhaseTimeline;
            if (timeline.Count == 0)
            {
                DrawText("No timeline data".AsSpan(), fontPath,
                    rect.X, rect.Y, rect.Width, rect.Height,
                    fontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center);
                return;
            }

            var pad = BasePadding * dpiScale;
            var barH = 24f * dpiScale;
            var barY = rect.Y + pad;
            var axisY = barY + barH + 2 * dpiScale;
            var axisH = rect.Height - barH - pad * 2 - 2 * dpiScale;

            // Time range: session start to now + 30min lookahead
            var timeStart = timeline[0].StartTime;
            var now = timeProvider.GetUtcNow();
            var sessionEnd = now + TimeSpan.FromMinutes(30);

            // Don't let range be too narrow (10 minutes minimum)
            var totalSeconds = Math.Max((sessionEnd - timeStart).TotalSeconds, 600);

            float TimeToX(DateTimeOffset t)
            {
                var frac = (float)((t - timeStart).TotalSeconds / totalSeconds);
                return rect.X + pad + frac * (rect.Width - pad * 2);
            }

            // Draw phase bars
            for (var i = 0; i < timeline.Count; i++)
            {
                var phaseStart = timeline[i].StartTime;
                var phaseEnd = i + 1 < timeline.Count ? timeline[i + 1].StartTime : now;
                var color = LiveSessionActions.PhaseColor(timeline[i].Phase);

                var x1 = Math.Max(TimeToX(phaseStart), rect.X + pad);
                var x2 = Math.Min(TimeToX(phaseEnd), rect.X + rect.Width - pad);
                var w = x2 - x1;
                if (w > 0)
                {
                    FillRect(x1, barY, w, barH, color);

                    // Label if wide enough
                    if (w > 40 * dpiScale)
                    {
                        var phaseLabel = LiveSessionActions.PhaseLabel(timeline[i].Phase);
                        // Shorten long labels
                        if (phaseLabel.Length > 8 && w < 80 * dpiScale)
                        {
                            phaseLabel = phaseLabel[..7] + "\u2026";
                        }
                        DrawText(phaseLabel.AsSpan(), fontPath,
                            x1 + 2, barY, w - 4, barH,
                            fontSize * 0.8f, BrightText, TextAlign.Center, TextAlign.Center);
                    }
                }
            }

            // Now needle
            if (now >= timeStart && now <= sessionEnd)
            {
                var nowX = TimeToX(now);
                FillRect(nowX, barY - 2 * dpiScale, 2 * dpiScale, barH + axisH + 4 * dpiScale, NowNeedleColor);
            }

            // Time axis ticks (every 30 min)
            if (axisH > 4)
            {
                // Adaptive tick interval: 5min if range < 30min, 10min if < 2h, 30min otherwise
                var rangeMins = totalSeconds / 60.0;
                var tickMins = rangeMins < 30 ? 5 : rangeMins < 120 ? 10 : 30;
                var tickStart = new DateTimeOffset(timeStart.Year, timeStart.Month, timeStart.Day,
                    timeStart.Hour, (int)(timeStart.Minute / tickMins) * (int)tickMins, 0, timeStart.Offset);
                for (var t = tickStart; t <= sessionEnd; t = t.AddMinutes(tickMins))
                {
                    if (t < timeStart) continue;
                    var tx = TimeToX(t);
                    if (tx < rect.X + pad || tx > rect.X + rect.Width - pad) continue;

                    FillRect(tx, axisY, 1, axisH * 0.5f, TimelineTickColor);
                    DrawText(t.ToOffset(state.SiteTimeZone).ToString("HH:mm").AsSpan(), fontPath,
                        tx - 25 * dpiScale, axisY + axisH * 0.4f, 50 * dpiScale, axisH * 0.6f,
                        fontSize * 0.8f, DimText, TextAlign.Center, TextAlign.Center);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Bottom strip: compact guide graph + RMS + ABORT
        // -----------------------------------------------------------------------

        private void RenderBottomStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, float pad, TimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);
            FillRect(rect.X, rect.Y, rect.Width, 1, SeparatorColor);

            var abortW = 80f * dpiScale;
            var rmsW = 280f * dpiScale;
            var guideW = rect.Width - rmsW - abortW - pad * 4;

            // Mini guide graph (left portion)
            if (guideW > 40)
            {
                var guideRect = new RectF32(rect.X + pad, rect.Y + 2, guideW, rect.Height - 4);
                RenderCompactGuideGraph(state, guideRect, dpiScale);
            }

            // RMS stats (center)
            var rmsText = LiveSessionActions.FormatGuideRms(state.LastGuideStats);
            DrawText(rmsText.AsSpan(), fontPath,
                rect.X + guideW + pad * 2, rect.Y, rmsW, rect.Height,
                fontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center);

            // Observation counter
            var obsIdx = state.CurrentObservationIndex;
            var obsCount = state.ActiveSession?.Observations.Count ?? 0;
            var obsText = $"Obs: {(obsIdx >= 0 ? obsIdx + 1 : 0)}/{obsCount}";
            DrawText(obsText.AsSpan(), fontPath,
                rect.X + guideW + pad * 2, rect.Y, rmsW, rect.Height * 0.4f,
                fontSize * 0.75f, DimText, TextAlign.Far, TextAlign.Near);

            // ABORT button (right)
            if (state.IsRunning)
            {
                var abortX = rect.X + rect.Width - abortW - pad;
                RenderButton("ABORT", abortX, rect.Y + 4 * dpiScale, abortW, rect.Height - 8 * dpiScale,
                    fontPath, fontSize, AbortBg, AbortText, "AbortSession",
                    _ => { state.ShowAbortConfirm = true; state.NeedsRedraw = true; });
            }
        }

        /// <summary>Compact guide graph — just RA/Dec lines, no labels.</summary>
        private void RenderCompactGuideGraph(LiveSessionState state, RectF32 rect, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

            var samples = state.GuideSamples;
            if (samples.Count < 2)
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

            var zeroY = rect.Y + rect.Height / 2;
            FillRect(rect.X, zeroY, rect.Width, 1, SeparatorColor);

            var count = samples.Count;
            var stepX = rect.Width / Math.Max(count - 1, 1);

            for (var i = 0; i < count; i++)
            {
                var s = samples[i];
                var x = rect.X + i * stepX;

                // RA (blue)
                var raYNorm = (float)(s.RaError / maxErr);
                var raPixY = zeroY - raYNorm * (rect.Height / 2);
                FillRect(x, Math.Min(raPixY, zeroY), Math.Max(stepX, 1), Math.Max(Math.Abs(raPixY - zeroY), 1), RaColor);

                // Dec (orange)
                var decYNorm = (float)(s.DecError / maxErr);
                var decPixY = zeroY - decYNorm * (rect.Height / 2);
                FillRect(x + stepX * 0.3f, Math.Min(decPixY, zeroY), Math.Max(stepX * 0.4f, 1), Math.Max(Math.Abs(decPixY - zeroY), 1), DecColor);
            }
        }

        // -----------------------------------------------------------------------
        // Main: per-OTA panels with live exposure state
        // -----------------------------------------------------------------------

        private void RenderOTAPanels(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float dpiScale, float pad, float rowH, TimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            if (state.ActiveSession is not { } session)
            {
                DrawText("No session".AsSpan(), fontPath,
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
            var tinyFs = fontSize * 0.7f;

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

                // OTA header (camera name)
                DrawText(ota.Camera.Device.DisplayName.AsSpan(), fontPath,
                    px + pad, y, textW, rowH,
                    fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                // Temperature + power from latest cooling sample for this camera
                var lastTemp = double.NaN;
                var lastPower = double.NaN;
                var lastSetpoint = double.NaN;
                var coolingSamples = state.CoolingSamples;
                for (var j = coolingSamples.Count - 1; j >= 0; j--)
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
                    DrawText(tempText.AsSpan(), fontPath,
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

                // Focuser position
                if (ota.Focuser is not null)
                {
                    var focusPos = (i < cameraStates.Count) ? cameraStates[i].FocusPosition : 0;
                    DrawText($"Foc: {focusPos}".AsSpan(), fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // Filter
                if (ota.FilterWheel is not null)
                {
                    var filterName = (i < cameraStates.Count && cameraStates[i].FilterName is { Length: > 0 } fn) ? fn : "--";
                    DrawText($"FW: {filterName}".AsSpan(), fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // Exposure state + progress bar
                y += pad;
                if (i < cameraStates.Count)
                {
                    var cs = cameraStates[i];
                    RenderExposureState(cs, px + pad, y, textW, progressH, rowH, fontPath, fontSize, smallFs, dpiScale, timeProvider);
                }
                else
                {
                    DrawText("Idle".AsSpan(), fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, DimText, TextAlign.Near, TextAlign.Center);
                }
            }
        }

        private void RenderExposureState(CameraExposureState cs, float x, float y, float w, float progressH, float rowH,
            string fontPath, float fontSize, float smallFs, float dpiScale, TimeProvider timeProvider)
        {
            if (cs.State == CameraState.Idle)
            {
                DrawText("Idle".AsSpan(), fontPath,
                    x, y, w, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
                return;
            }

            if (cs.State == CameraState.Download || cs.State == CameraState.Reading)
            {
                DrawText($"Downloading #{cs.FrameNumber}\u2026".AsSpan(), fontPath,
                    x, y, w, rowH, smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
                return;
            }

            // Exposing — show countdown + progress bar
            var elapsed = timeProvider.GetUtcNow() - cs.ExposureStart;
            var totalSec = cs.SubExposure.TotalSeconds;
            var elapsedSec = Math.Min(elapsed.TotalSeconds, totalSec);
            var fraction = totalSec > 0 ? (float)(elapsedSec / totalSec) : 0f;

            // Filter + frame label
            var filterLabel = cs.FilterName is { Length: > 0 } fn ? fn : "L";
            var expLabel = $"{filterLabel} #{cs.FrameNumber} ({elapsedSec:F0}/{totalSec:F0}s)";
            DrawText(expLabel.AsSpan(), fontPath,
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
                DrawText(remText.AsSpan(), fontPath,
                    x, y, w, progressH,
                    fontSize * 0.65f, BrightText, TextAlign.Center, TextAlign.Center);
            }
        }

        /// <summary>Tiny sparkline of temperature + power for a single camera.</summary>
        private void RenderMiniSparkline(IReadOnlyList<CoolingSample> allSamples, int cameraIndex, RectF32 rect, RGBAColor32 tempColor, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GraphBg);

            var powerColor = CameraPowerColors[cameraIndex % CameraPowerColors.Length];

            // Collect last N samples for this camera
            const int maxPoints = 20;
            Span<float> temps = stackalloc float[maxPoints];
            Span<float> powers = stackalloc float[maxPoints];
            var count = 0;
            for (var i = allSamples.Count - 1; i >= 0 && count < maxPoints; i--)
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

            if (state.ExposureLogScrollOffset < 0)
            {
                state.ExposureLogScrollOffset = 0;
            }

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

            // Focus history below exposure log if space allows
            var remainH = rect.Y + rect.Height - y;
            if (remainH > rowH * 3 && state.FocusHistory.Count > 0)
            {
                FillRect(rect.X, y, rect.Width, 1, SeparatorColor);
                y += pad;

                DrawText("Focus History".AsSpan(), fontPath,
                    rect.X + pad, y, rect.Width - pad * 2, rowH,
                    fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                var history = state.FocusHistory;
                var focusStartIdx = Math.Max(0, history.Count - (int)((remainH - rowH * 2) / rowH));
                for (var i = focusStartIdx; i < history.Count && y < rect.Y + rect.Height - rowH; i++)
                {
                    var row = LiveSessionActions.FormatFocusHistoryRow(history[i]);
                    var bg = (i % 2 == 0) ? PanelBg : RowAltBg;
                    FillRect(rect.X, y, rect.Width, rowH, bg);
                    DrawText(row.AsSpan(), fontPath,
                        rect.X + pad, y, rect.Width - pad * 2, rowH,
                        fontSize * 0.75f, BodyText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }
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
