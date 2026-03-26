using System;
using System.Collections.Immutable;
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

        /// <summary>Last mouse position for drag panning.</summary>
        private (float X, float Y)? _dragStart;

        // Layout constants (at 1x scale)
        private const float BaseFontSize       = 14f;
        private const float BaseTopStripHeight = 36f;
        private const float BaseTimelineHeight = 60f;
        private const float BaseBotStripHeight = 100f;
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
        private static readonly RGBAColor32 StatusSlewing    = new RGBAColor32(0xcc, 0xcc, 0x44, 0xff); // yellow
        private static readonly RGBAColor32 StatusSolving    = new RGBAColor32(0x44, 0xaa, 0xcc, 0xff); // cyan
        private static readonly RGBAColor32 StatusTracking   = new RGBAColor32(0x44, 0xcc, 0x44, 0xff); // green

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

            // Layout dimensions
            var otaCount = state.ActiveSession?.Setup.Telescopes.Length ?? 1;
            var otaTotalW = BaseOtaPanelW * dpiScale * otaCount;
            var logW = BaseRightPanelW * dpiScale;
            var viewerX = contentRect.X + otaTotalW;
            var viewerW = contentRect.Width - otaTotalW - logW;

            // Center: mini viewer — rendered FIRST so panels paint over any overflow
            if (viewerW > 100 && MiniViewer is { } viewer)
            {
                // Check if a new frame arrived for the selected camera
                var images = state.LastCapturedImages;
                var selectedIdx = viewer.State.SelectedCameraIndex;
                Image? latestImage = null;
                if (selectedIdx >= 0 && selectedIdx < images.Length)
                {
                    latestImage = images[selectedIdx];
                }
                else
                {
                    // Auto: show first available
                    for (var i = 0; i < images.Length; i++)
                    {
                        if (images[i] is { } img)
                        {
                            latestImage = img;
                            break;
                        }
                    }
                }

                if (latestImage is not null && !ReferenceEquals(latestImage, _displayedImage))
                {
                    _displayedImage = latestImage;
                    viewer.QueueImage(latestImage);
                }

                // Image area (below where toolbar will go)
                var toolbarH = BaseRowHeight * dpiScale;
                var imageRect = new RectF32(viewerX, mainY + toolbarH, viewerW, mainH - toolbarH);
                viewer.Render(imageRect, Renderer.Width, Renderer.Height);
            }
            else if (viewerW > 0)
            {
                FillRect(viewerX, mainY, viewerW, mainH, GraphBg);
                if (state.Phase is SessionPhase.Observing)
                {
                    DrawText("Waiting for first frame\u2026", fontPath,
                        viewerX, mainY, viewerW, mainH,
                        fs, DimText, TextAlign.Center, TextAlign.Center);
                }
            }

            // Left: per-OTA panels (paints over viewer overflow on the left)
            var otaRect = new RectF32(contentRect.X, mainY, otaTotalW, mainH);
            RenderOTAPanels(state, otaRect, fontPath, fs, dpiScale, pad, rowH, timeProvider);

            // Right: exposure log (paints over viewer overflow on the right)
            var logX = contentRect.X + contentRect.Width - logW;
            if (logW > 0)
            {
                var rightRect = new RectF32(logX, mainY, logW, mainH);
                RenderExposureLog(state, rightRect, fontPath, fs, pad, rowH);
            }

            // Mini viewer toolbar (on top of the image, after panels)
            if (viewerW > 100 && MiniViewer is { } viewer2)
            {
                var toolbarH = BaseRowHeight * dpiScale;
                var toolbarRect = new RectF32(viewerX, mainY, viewerW, toolbarH);
                RenderMiniViewerToolbar(viewer2.State, toolbarRect, fontPath, fs, dpiScale);
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

            // OTA selector buttons (right-aligned) — only for multi-OTA setups
            var otaButtonCount = State?.ActiveSession?.Setup.Telescopes.Length ?? 0;
            if (otaButtonCount > 1)
            {
                var otaBtnX = rect.X + rect.Width - (btnW * 0.8f + pad) * otaButtonCount - pad;
                for (var oi = 0; oi < otaButtonCount; oi++)
                {
                    var idx = oi; // capture
                    var isSelected = vs.SelectedCameraIndex == idx;
                    RenderButton($"#{idx + 1}", otaBtnX, btnY, btnW * 0.8f, btnH, fontPath, btnFs,
                        isSelected ? activeBg : inactiveBg, BodyText, $"ViewerOTA{idx}",
                        _ => { vs.SelectedCameraIndex = vs.SelectedCameraIndex == idx ? -1 : idx; });
                    otaBtnX += btnW * 0.8f + pad;
                }
            }

            // Status text: stretch info
            var infoText = $"{vs.StretchMode} {vs.StretchParameters}";
            if (vs.CurvesBoost > 0)
            {
                infoText += $" Boost:{vs.CurvesBoost:F2}";
            }
            var infoW = rect.X + rect.Width - x - pad;
            if (otaButtonCount > 1)
            {
                infoW -= (btnW * 0.8f + pad) * otaButtonCount;
            }
            DrawText(infoText, fontPath,
                x, rect.Y, infoW, rect.Height,
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

                case InputEvent.Scroll(var scrollY, _, _, _) when MiniViewer is { State: { ZoomToFit: false } vs }:
                    // Scroll to zoom in the mini viewer
                    var zoomFactor = scrollY > 0 ? 1.15f : 1f / 1.15f;
                    vs.Zoom = MathF.Max(0.1f, MathF.Min(vs.Zoom * zoomFactor, 16f));
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.Scroll(var scrollY2, _, _, _):
                    state.ExposureLogScrollOffset = Math.Max(0, state.ExposureLogScrollOffset + (scrollY2 > 0 ? -1 : 1));
                    state.NeedsRedraw = true;
                    return true;

                // Mini viewer mouse drag for panning
                case InputEvent.MouseDown(var mx, var my, _, _, _) when MiniViewer is { State: { ZoomToFit: false } }:
                    _dragStart = (mx, my);
                    return true;

                case InputEvent.MouseMove(var mx, var my) when _dragStart is { } drag && MiniViewer is { State: { } vs }:
                    var dx = mx - drag.X;
                    var dy = my - drag.Y;
                    vs.PanOffset = (vs.PanOffset.X + dx, vs.PanOffset.Y + dy);
                    _dragStart = (mx, my);
                    state.NeedsRedraw = true;
                    return true;

                case InputEvent.MouseUp(_, _, _):
                    if (_dragStart is not null)
                    {
                        _dragStart = null;
                        return true;
                    }
                    return false;

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
            DrawText(label, fontPath,
                rect.X + pad, rect.Y, pillW, rect.Height,
                fontSize * 0.9f, AbortText, TextAlign.Center, TextAlign.Center);

            // Activity text
            var targetLabel = LiveSessionActions.PhaseStatusText(state, timeProvider);
            DrawText(targetLabel, fontPath,
                rect.X + pillW + pad * 2, rect.Y, rect.Width * 0.45f, rect.Height,
                fontSize, BodyText, TextAlign.Near, TextAlign.Center);

            // Obs / frame count / exposure time (top right)
            var obsIdx = state.CurrentObservationIndex;
            var obsCount = state.ActiveSession?.Observations.Count ?? 0;
            var progressParts = $"Obs: {(obsIdx >= 0 ? obsIdx + 1 : 0)}/{obsCount}";
            if (state.ActiveObservation is { } topObs)
            {
                var subSec = topObs.SubExposure.TotalSeconds;
                var estimatedFrames = subSec > 0 ? (int)(topObs.Duration.TotalSeconds / (subSec + 10)) : 0;
                progressParts += $"  Frames: {state.TotalFramesWritten}/~{estimatedFrames}";
            }
            progressParts += $"  Exp: {LiveSessionActions.FormatDuration(state.TotalExposureTime)}";
            DrawText(progressParts, fontPath,
                rect.X + rect.Width * 0.5f, rect.Y, rect.Width * 0.45f, rect.Height,
                fontSize, DimText, TextAlign.Far, TextAlign.Center);
        }

        // -----------------------------------------------------------------------
        // Timeline: phase bars + now needle + time axis
        // -----------------------------------------------------------------------

        private void RenderTimeline(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, TimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, TimelineBg);

            var timeline = state.PhaseTimeline;
            if (timeline.Length == 0)
            {
                DrawText("No timeline data", fontPath,
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
            for (var i = 0; i < timeline.Length; i++)
            {
                var phaseStart = timeline[i].StartTime;
                var phaseEnd = i + 1 < timeline.Length ? timeline[i + 1].StartTime : now;
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
                        DrawText(phaseLabel, fontPath,
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
                    DrawText(t.ToOffset(state.SiteTimeZone).ToString("HH:mm"), fontPath,
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
            DrawText(rmsText, fontPath,
                rect.X + guideW + pad * 2, rect.Y, rmsW, rect.Height,
                fontSize * 0.9f, BodyText, TextAlign.Center, TextAlign.Center);

            // ABORT button (right)
            if (state.IsRunning)
            {
                var abortX = rect.X + rect.Width - abortW - pad;
                RenderButton("ABORT", abortX, rect.Y + 4 * dpiScale, abortW, rect.Height - 8 * dpiScale,
                    fontPath, fontSize, AbortBg, AbortText, "AbortSession",
                    _ => { state.ShowAbortConfirm = true; state.NeedsRedraw = true; });
            }
        }

        /// <summary>
        /// PHD2-style guide graph using shared <see cref="GuideGraphRenderer"/> helpers.
        /// </summary>
        private void RenderCompactGuideGraph(LiveSessionState state, RectF32 rect, float dpiScale)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, GuideGraphRenderer.GraphBg);

            var samples = state.GuideSamples;
            var yScale = GuideGraphRenderer.ComputeYScale(state.LastGuideStats);
            var halfH = rect.Height / 2;
            var zeroY = rect.Y + halfH;

            // Grid lines
            for (var arcsec = 1; arcsec < (int)yScale; arcsec++)
            {
                var gridY = (float)(arcsec / yScale) * halfH;
                FillRect(rect.X, zeroY - gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
                FillRect(rect.X, zeroY + gridY, rect.Width, 1, GuideGraphRenderer.GridColor);
            }
            FillRect(rect.X, zeroY, rect.Width, 1, GuideGraphRenderer.ZeroLineColor);

            if (samples.Length < 2) return;

            var (startIdx, visibleCount, spacing) = GuideGraphRenderer.ComputeWindow(samples.Length, rect.Width, dpiScale);
            var lineW = Math.Max(dpiScale, 1f);

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
                DrawText("No session", fontPath,
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
                if (ota.Focuser is not null && i < cameraStates.Length)
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
                if (ota.FilterWheel is not null)
                {
                    var filterName = (i < cameraStates.Length && cameraStates[i].FilterName is { Length: > 0 } fn) ? fn : "--";
                    DrawText($"FW: {filterName}", fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                    y += rowH;
                }

                // Exposure state + progress bar
                y += pad;
                if (i < cameraStates.Length)
                {
                    var cs = cameraStates[i];
                    RenderExposureState(cs, px + pad, y, textW, progressH, rowH, fontPath, fontSize, smallFs, dpiScale, timeProvider);
                }
                else
                {
                    DrawText("Idle", fontPath,
                        px + pad, y, textW, rowH,
                        smallFs, DimText, TextAlign.Near, TextAlign.Center);
                }
            }

            // Mount status section (below OTAs, full width)
            var mountY = rect.Y + rect.Height - rowH * 5 - pad;
            if (mountY > rect.Y + rect.Height * 0.35f) // only show if there's room
            {
                FillRect(rect.X, mountY, rect.Width, 1, SeparatorColor);
                mountY += pad;

                // Mount name + status dot
                var mountName = session.Setup.Mount.Device.DisplayName;
                var ms = state.MountState;
                var dotColor = ms.IsSlewing ? StatusSlewing
                    : ms.IsTracking ? StatusTracking
                    : DimText;
                var dotSize = rowH * 0.4f;
                FillRect(rect.X + pad, mountY + (rowH - dotSize) / 2, dotSize, dotSize, dotColor);
                var pierLabel = ms.PierSide is Lib.Devices.PointingState.Normal ? "E" : ms.PierSide is Lib.Devices.PointingState.ThroughThePole ? "W" : "";
                var mountStatus = ms.IsSlewing ? "Slewing" : ms.IsTracking ? "Tracking" : "Idle";
                DrawText($"{mountName}  {mountStatus}  {pierLabel}", fontPath,
                    rect.X + pad + dotSize + pad, mountY, rect.Width - pad * 3 - dotSize, rowH,
                    smallFs, HeaderText, TextAlign.Near, TextAlign.Center);
                mountY += rowH;

                // RA + HA on one row
                var raStr = Lib.Astrometry.CoordinateUtils.HoursToHMS(ms.RightAscension, withFrac: false);
                var haStr = $"HA {ms.HourAngle:+0.00;-0.00}h";
                DrawText($"RA {raStr}  {haStr}", fontPath,
                    rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                    smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                mountY += rowH;

                // Dec on separate row
                var decStr = Lib.Astrometry.CoordinateUtils.DegreesToDMS(ms.Declination, withFrac: false);
                DrawText($"Dec {decStr}", fontPath,
                    rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                    smallFs, BodyText, TextAlign.Near, TextAlign.Center);
                mountY += rowH;

                // Target name (if observing)
                if (state.ActiveObservation is { Target: var target })
                {
                    DrawText($"\u2609 {target.Name}", fontPath,
                        rect.X + pad, mountY, rect.Width - pad * 2, rowH,
                        smallFs, DimText, TextAlign.Near, TextAlign.Center);
                }
            }
        }

        private void RenderExposureState(CameraExposureState cs, float x, float y, float w, float progressH, float rowH,
            string fontPath, float fontSize, float smallFs, float dpiScale, TimeProvider timeProvider)
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
            var filterLabel = cs.FilterName is { Length: > 0 } fn ? fn : "L";
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
        // Right panel: exposure log
        // -----------------------------------------------------------------------

        private void RenderExposureLog(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float pad, float rowH)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, PanelBg);

            // Separator on left edge
            FillRect(rect.X, rect.Y, 1, rect.Height, SeparatorColor);

            // Header
            DrawText("Exposure Log", fontPath,
                rect.X + pad, rect.Y, rect.Width - pad * 2, rowH,
                fontSize * 0.85f, HeaderText, TextAlign.Near, TextAlign.Center);

            // Column headers
            var colY = rect.Y + rowH;
            FillRect(rect.X, colY, rect.Width, rowH, HeaderBg);
            DrawText("Time  Target       Filter  HFD  \u2605", fontPath,
                rect.X + pad, colY, rect.Width - pad * 2, rowH,
                fontSize * 0.75f, DimText, TextAlign.Near, TextAlign.Center);

            var log = state.ExposureLog;
            if (log.Length == 0)
            {
                DrawText("No frames yet", fontPath,
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

            var startIdx = Math.Max(0, log.Length - visibleRows - state.ExposureLogScrollOffset);
            if (startIdx < 0)
            {
                startIdx = 0;
            }

            for (var i = startIdx; i < log.Length && y < rect.Y + rect.Height - rowH; i++)
            {
                var row = LiveSessionActions.FormatExposureLogRow(log[i]);
                var bg = (i % 2 == 0) ? PanelBg : RowAltBg;
                FillRect(rect.X, y, rect.Width, rowH, bg);
                DrawText(row, fontPath,
                    rect.X + pad, y, rect.Width - pad * 2, rowH,
                    fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
                y += rowH;
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
                    var row = LiveSessionActions.FormatFocusHistoryRow(history[i]);
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

        private void RenderAbortConfirm(RectF32 contentRect, string fontPath, float fontSize, float dpiScale)
        {
            var stripH = 40f * dpiScale;
            var stripY = contentRect.Y + (contentRect.Height - stripH) / 2;

            // Semi-transparent backdrop (darken)
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                new RGBAColor32(0x00, 0x00, 0x00, 0x88));

            // Confirm strip
            FillRect(contentRect.X, stripY, contentRect.Width, stripH, ConfirmStripBg);
            DrawText("Abort session? Press Enter to confirm, Escape to cancel", fontPath,
                contentRect.X, stripY, contentRect.Width, stripH,
                fontSize, AbortText, TextAlign.Center, TextAlign.Center);
        }
    }
}
