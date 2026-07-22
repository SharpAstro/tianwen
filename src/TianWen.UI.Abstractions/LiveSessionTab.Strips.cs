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
    /// Session-mode chrome: top status strip, schedule timeline, bottom strip, abort-confirm overlay.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        private void RenderTopStrip(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, HeaderBg);

            var dpiScale = DpiScale;
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
                var inFlats = state.Mode == LiveSessionMode.Flats;
                var modePillColor = inPolar
                    ? StatusSolving                                    // cyan while PA running
                    : inPlanetary
                        ? new RGBAColor32(0x40, 0x33, 0x66, 0xff)      // muted purple-blue for Planetary
                        : inFlats
                            ? new RGBAColor32(0x88, 0x66, 0x22, 0xff)  // warm amber for Flats (light source)
                            : new RGBAColor32(0x55, 0x33, 0x88, 0xff); // purple for plain Preview
                var pillLabel = inPolar ? "POLAR \u25BE" : inPlanetary ? "PLANETARY \u25BE" : inFlats ? "FLATS \u25BE" : "PREVIEW \u25BE";
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
                            ImmutableArray.Create("Preview", "Polar Align", "Planetary", "Flats"),
                            (idx, _) =>
                            {
                                var target = idx switch
                                {
                                    1 => LiveSessionMode.PolarAlign,
                                    2 => LiveSessionMode.Planetary,
                                    3 => LiveSessionMode.Flats,
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

                                // Same guard for an in-flight flat run: cancel it and let the async
                                // handler flip Mode back; the user re-picks once it has stopped.
                                if (state.Mode == LiveSessionMode.Flats && state.FlatsCts is not null)
                                {
                                    PostSignal(new CancelFlatsSignal());
                                    if (target != LiveSessionMode.Preview)
                                    {
                                        state.FlatStatusMessage = "Flat run stopping -- pick the mode again";
                                    }
                                    state.NeedsRedraw = true;
                                    return;
                                }

                                if (state.Mode == LiveSessionMode.PolarAlign)
                                {
                                    state.PolarStatusMessage = "";
                                }
                                if (state.Mode == LiveSessionMode.Flats)
                                {
                                    state.FlatStatusMessage = "";
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
                                    case LiveSessionMode.Flats:
                                        // Enter Flats setup: the panel's Start button fires StartFlatsSignal
                                        // after the user picks a source + count. Seed the per-filter count
                                        // from the working copy (persisted across mode switches).
                                        state.Mode = LiveSessionMode.Flats;
                                        state.FlatStatusMessage = "Pick a source and click Start";
                                        break;
                                    default:
                                        state.Mode = LiveSessionMode.Preview;
                                        break;
                                }
                                state.NeedsRedraw = true;
                            });
                        state.NeedsRedraw = true;
                    });
                RenderLayout(modeLeaf, new RectF32(pillX, pillY, pillW, pillH), fontPath);

                // Current time is shown by the global status-bar clock (top-right on every
                // tab) -- no separate in-content clock here, to avoid a duplicate display.
                return;
            }

            // Running: phase pill + activity text + obs/frame/exposure progress, as ONE arranged row.
            // The strip background is already filled above; the pill's own colour is on its leaf. Was a
            // hand-placed FillRect pill + three absolutely-positioned DrawText columns.
            var pillColor = LiveSessionActions.PhaseColor(state.Phase);
            var label = LiveSessionActions.PhaseLabel(state.Phase);
            var activityText = LiveSessionActions.PhaseStatusText(state, timeProvider);

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

            var runTree = Layout.Builder.HStack(
                    Layout.Builder.Text(label, BaseFontSize * 0.9f, AbortText, TextAlign.Center, TextAlign.Center)
                        .WFixed(140f).HStar().Bg(pillColor),
                    Layout.Builder.Text(activityText, BaseFontSize, BodyText, TextAlign.Near, TextAlign.Center).WStar(),
                    Layout.Builder.Text(progressParts, BaseFontSize, DimText, TextAlign.Far, TextAlign.Center).WStar())
                .WithGap(BasePadding).Pad(BasePadding);
            RenderLayout(runTree, rect, fontPath);
        }

        // -----------------------------------------------------------------------
        // Timeline: phase bars + now needle + time axis
        // -----------------------------------------------------------------------

        private void RenderTimeline(LiveSessionState state, RectF32 rect, string fontPath, float fontSize, ITimeProvider timeProvider)
        {
            FillRect(rect.X, rect.Y, rect.Width, rect.Height, TimelineBg);

            if (!state.IsRunning)
            {
                RenderPreviewTimeline(state, rect, fontPath, fontSize, timeProvider);
                return;
            }

            var dpiScale = DpiScale;

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

        private void RenderBottomStrip(LiveSessionState state, RectF32 rect, string fontPath)
        {
            // One arranged row over the header background with a 1px top hairline:
            // [mini guide graph (raster Fill) * | RMS stats | ABORT]. Was a hand-computed
            // guideW/rmsX/abortX split with a RenderButton.
            var rmsText = LiveSessionActions.FormatGuideRms(state.LastGuideStats);

            var children = new List<Layout.Node>
            {
                Layout.Builder.Fill(key: "bottomGuide").Stretch(),
                Layout.Builder.Text(rmsText, BaseFontSize * 0.9f, BodyText, TextAlign.Near, TextAlign.Center).WFixed(220f).HStar(),
            };
            if (state.IsRunning)
            {
                children.Add(Layout.Builder.Text("ABORT", BaseFontSize, AbortText, TextAlign.Center, TextAlign.Center)
                    .WFixed(80f).HStar().Bg(AbortBg)
                    .Clickable(new HitResult.ButtonHit("AbortSession"),
                        _ => { state.ShowAbortConfirm = true; state.NeedsRedraw = true; }));
            }

            var root = Layout.Builder.VStack(
                    Layout.Builder.Spacer().RowH(1f).Bg(SeparatorColor),
                    Layout.Builder.HStack([.. children]).WithGap(BasePadding).Stretch().Pad(2f))
                .Bg(HeaderBg);
            RenderLayout(root, rect, fontPath,
                drawFill: (fill, r) => { if (fill.Key == "bottomGuide") RenderCompactGuideGraph(state, r); });
        }

        private void RenderAbortConfirm(RectF32 contentRect, string fontPath, float fontSize)
        {
            var dpiScale = DpiScale;
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

        /// <summary>
        /// Session-driven user prompt overlay ("switch on the panel, then Continue"). A centred card with
        /// the title, message, and clickable [Continue] / [Cancel] buttons; Enter = Continue, Escape =
        /// Cancel (handled in <c>.Input</c>). Rendered whenever <see cref="LiveSessionState.PendingPrompt"/>
        /// is non-null, in any mode, so a future dark-frame cover-close prompt reuses it unchanged.
        /// </summary>
        private void RenderSessionPrompt(RectF32 contentRect, SessionPromptEventArgs prompt,
            string fontPath, float fontSize)
        {
            var dpiScale = DpiScale;
            var pad = BasePadding * dpiScale;
            var rowH = BaseRowHeight * dpiScale;
            var cardW = MathF.Min(contentRect.Width * 0.7f, 520f * dpiScale);
            var cardH = rowH * 5f + pad * 2f;
            var cardX = contentRect.X + (contentRect.Width - cardW) / 2f;
            var cardY = contentRect.Y + (contentRect.Height - cardH) / 2f;

            // Dim backdrop + card.
            FillRect(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height,
                new RGBAColor32(0x00, 0x00, 0x00, 0xaa));
            FillRect(cardX, cardY, cardW, cardH, PanelBg);
            FillRect(cardX, cardY, cardW, 2f, StatusSlewing); // accent bar

            var innerX = cardX + pad;
            var innerW = cardW - pad * 2f;

            DrawText(prompt.Title, fontPath,
                innerX, cardY + pad, innerW, rowH,
                fontSize * 1.1f, BrightText, TextAlign.Near, TextAlign.Center);

            DrawText(prompt.Message, fontPath,
                innerX, cardY + pad + rowH, innerW, rowH * 2.4f,
                fontSize, BodyText, TextAlign.Near, TextAlign.Near);

            // Buttons: [Cancel] left, [Continue] right (primary in the muscle-memory spot). One HStack of
            // two equal Star cells (was two RenderButton calls placed by hand); device-px -> dpiScale:1f.
            var btnH = rowH * 1.3f;
            var btnY = cardY + cardH - btnH - pad;
            var btnRow = Layout.Builder.HStack(
                    Layout.Builder.Text(prompt.CancelLabel, fontSize, BodyText, TextAlign.Center, TextAlign.Center)
                        .WStar().HStar().Bg(new RGBAColor32(0x33, 0x33, 0x3a, 0xff))
                        .Clickable(new HitResult.ButtonHit("SessionPromptCancel"), _ => PostSignal(new RespondSessionPromptSignal(false))),
                    Layout.Builder.Text(prompt.ContinueLabel, fontSize, BrightText, TextAlign.Center, TextAlign.Center)
                        .WStar().HStar().Bg(new RGBAColor32(0x44, 0xaa, 0x66, 0xff))
                        .Clickable(new HitResult.ButtonHit("SessionPromptContinue"), _ => PostSignal(new RespondSessionPromptSignal(true))))
                .WithGap(pad);
            RenderLayout(btnRow, new RectF32(innerX, btnY, innerW, btnH), fontPath, dpiScale: 1f);
        }

        // -----------------------------------------------------------------------
        // Polar alignment: precondition gating + side panel
        // -----------------------------------------------------------------------
    }
}
