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
    /// Session-mode chrome: top status strip, schedule timeline, bottom strip, abort-confirm overlay.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        private void RenderTopStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);

            var pad = BasePadding * dpiScale;
            var pillW = 140f * dpiScale;
            var pillH = rect.Height - pad * 2;

            if (!state.IsRunning)
            {
                // Mode pill -- doubles as a dropdown trigger so the user can switch
                // between Preview and Polar Align without hunting for a separate
                // toolbar button. The polar-align entry posts the standard signal
                // (re-validated by AppSignalHandler); selecting Preview while polar
                // is active posts a Cancel. Caret hint indicates the click affordance.
                var inPolar = state.Mode == LiveSessionMode.PolarAlign;
                var inPlanetary = state.Mode == LiveSessionMode.Planetary;
                var modePillColor = inPolar
                    ? StatusSolving                                    // cyan while PA running
                    : inPlanetary
                        ? new RGBAColor32(0x40, 0x33, 0x66, 0xff)      // muted purple-blue for Planetary
                        : new RGBAColor32(0x55, 0x33, 0x88, 0xff);     // purple for plain Preview
                var pillLabel = inPolar ? "POLAR \u25BE" : inPlanetary ? "PLANETARY \u25BE" : "PREVIEW \u25BE";
                var pillX = rect.X + pad;
                var pillY = rect.Y + pad;
                // Click-to-open: anchor the dropdown directly under the pill. The pill is a single
                // draw==hit leaf -- background, label, and click region all bind to one node's rect
                // (rendered via the layout engine) so the hit target can never drift from the paint.
                var dropdown = state.ModeDropdown;
                var modeLeaf = Layout.Builder.Text(pillLabel, fontSize * 0.9f / dpiScale, AbortText, TextAlign.Center, TextAlign.Center)
                    .Stretch().Bg(modePillColor)
                    .Clickable(new HitResult.ButtonHit("ModePill"), _ =>
                    {
                        if (dropdown.IsOpen)
                        {
                            dropdown.Close();
                            return;
                        }
                        dropdown.Open(
                            pillX, pillY + pillH, pillW,
                            ImmutableArray.Create("Preview", "Polar Align", "Planetary"),
                            (idx, _) =>
                            {
                                var target = idx switch
                                {
                                    1 => LiveSessionMode.PolarAlign,
                                    2 => LiveSessionMode.Planetary,
                                    _ => LiveSessionMode.Preview,
                                };
                                if (target == state.Mode)
                                {
                                    return;
                                }

                                // Leaving an ACTIVE polar-align routine tears it down via the cancel
                                // signal, which flips Mode back to Preview itself -- so don't also set
                                // the target here (that would race the async handler); the user re-picks
                                // once it has stopped. An idle (setup-phase) polar flips directly below.
                                if (state.Mode == LiveSessionMode.PolarAlign
                                    && !(state.PolarAlignmentCts is null && state.PolarPhase == PolarAlignmentPhase.Idle))
                                {
                                    PostSignal(new CancelPolarAlignmentSignal());
                                    if (target != LiveSessionMode.Preview)
                                    {
                                        state.PolarStatusMessage = "Polar align stopped -- pick the mode again";
                                    }
                                    state.NeedsRedraw = true;
                                    return;
                                }

                                if (state.Mode == LiveSessionMode.PolarAlign)
                                {
                                    state.PolarStatusMessage = "";
                                }

                                switch (target)
                                {
                                    case LiveSessionMode.PolarAlign:
                                    {
                                        var (polarEnabled, polarReason) = EvaluatePolarPreconditions(state);
                                        if (polarEnabled)
                                        {
                                            // Switch into PolarAlign setup (PolarPhase.Idle); the panel's
                                            // Start button fires StartPolarAlignmentSignal after the user
                                            // reviews the config. Auto-enable the WCS grid so meridians
                                            // appear once the first probe frame solves.
                                            if (PreviewView is not null)
                                            {
                                                _previewState.ShowGrid = true;
                                            }
                                            state.Mode = LiveSessionMode.PolarAlign;
                                            state.PolarPhase = PolarAlignmentPhase.Idle;
                                            state.PolarStatusMessage = "Configure and click Start";
                                        }
                                        else
                                        {
                                            state.PolarStatusMessage = polarReason;
                                        }
                                        break;
                                    }
                                    case LiveSessionMode.Planetary:
                                        state.Mode = LiveSessionMode.Planetary;
                                        break;
                                    default:
                                        state.Mode = LiveSessionMode.Preview;
                                        break;
                                }
                                state.NeedsRedraw = true;
                            });
                        state.NeedsRedraw = true;
                    });
                RenderLayout(modeLeaf, new RectF32(pillX, pillY, pillW, pillH), fontPath, dpiScale);

                // Current time is shown by the global status-bar clock (top-right on every
                // tab) -- no separate in-content clock here, to avoid a duplicate display.
                return;
            }

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
            var obsDisplay = obsCount > 0 ? Math.Clamp(obsIdx + 1, 0, obsCount) : 0;
            var progressParts = $"Obs: {obsDisplay}/{obsCount}";
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

        private void RenderTimeline(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, TimelineBg);

            if (!state.IsRunning)
            {
                RenderPreviewTimeline(state, rect, fontPath, fontSize, dpiScale, timeProvider);
                return;
            }

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

        private void RenderBottomStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, float dpiScale, float pad, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);
            FillRect(rect.X, rect.Y, rect.Width, 1, SeparatorColor);

            var abortW = state.IsRunning ? 80f * dpiScale : 0;
            var rmsW = 220f * dpiScale;
            var guideW = rect.Width - rmsW - abortW - pad * (state.IsRunning ? 5 : 3);

            // Mini guide graph (left portion)
            if (guideW > 40)
            {
                var guideRect = new RectF32(rect.X + pad, rect.Y + 2, guideW, rect.Height - 4);
                RenderCompactGuideGraph(state, guideRect, dpiScale);
            }

            // RMS stats (between graph and abort)
            var rmsX = rect.X + guideW + pad * 2;
            var rmsText = LiveSessionActions.FormatGuideRms(state.LastGuideStats);
            DrawText(rmsText, fontPath,
                rmsX, rect.Y, rmsW, rect.Height,
                fontSize * 0.9f, BodyText, TextAlign.Near, TextAlign.Center);

            // ABORT button (right, after RMS)
            if (state.IsRunning)
            {
                var abortX = rmsX + rmsW + pad;
                RenderButton("ABORT", abortX, rect.Y + 4 * dpiScale, abortW, rect.Height - 8 * dpiScale,
                    fontPath, fontSize, AbortBg, AbortText, "AbortSession",
                    _ => { state.ShowAbortConfirm = true; state.NeedsRedraw = true; });
            }
        }

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

        // -----------------------------------------------------------------------
        // Polar alignment: precondition gating + side panel
        // -----------------------------------------------------------------------
    }
}
