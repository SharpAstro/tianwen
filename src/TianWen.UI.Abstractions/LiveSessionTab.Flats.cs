using System;
using DIR.Lib;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Flats mode (<see cref="LiveSessionMode.Flats"/>): the side panel for on-demand flat capture.
    /// Setup phase renders an illumination-source selector + flats-per-filter stepper + Start; while a
    /// run is in flight (<see cref="LiveSessionState.FlatsCts"/> non-null) it renders the flat phase +
    /// status + Cancel. The centre preview viewer (shared with Preview / PolarAlign) shows the metering
    /// and kept frames the session publishes. Mirrors <see cref="LiveSessionTab{TSurface}"/>'s
    /// <c>.Polar</c> partial.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        /// <summary>
        /// Surface-level precondition hint for the Flats Start button. The authoritative check lives
        /// in <c>AppSignalHandler.StartFlatsSignal</c> (device hub, cover/calibrator presence, mount for
        /// sky) and returns its own notifications; this is only a UX hint about whether the click will
        /// take.
        /// </summary>
        private static (bool Enabled, string Reason) EvaluateFlatsPreconditions(LiveSessionState state)
        {
            if (state.OtaCount == 0)
            {
                return (false, "Flats: no OTA configured");
            }
            if (state.FlatSetupSource is FlatIlluminationChoice.SkyDusk or FlatIlluminationChoice.SkyDawn
                && string.IsNullOrEmpty(state.MountDisplayName))
            {
                return (false, "Sky-flats: connect a mount first");
            }
            return (true, "Start flat capture");
        }

        /// <summary>Human label for the illumination-source selector.</summary>
        private static string FlatSourceLabel(FlatIlluminationChoice choice) => choice switch
        {
            FlatIlluminationChoice.Calibrator => "Calibrator / panel",
            FlatIlluminationChoice.SkyDusk => "Sky flats (dusk)",
            FlatIlluminationChoice.SkyDawn => "Sky flats (dawn)",
            _ => "?"
        };

        /// <summary>
        /// Side panel for Flats mode, replacing the exposure-log panel while
        /// <see cref="LiveSessionMode.Flats"/> is active. Setup form when idle, phase + status + Cancel
        /// while a run is in flight.
        /// </summary>
        private void RenderFlatsSidePanel(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float dpiScale, float pad, float rowH)
        {
            var x0 = rect.X + pad;
            var w = rect.Width - pad * 2;

            DrawText("Flat Capture", fontPath,
                x0, rect.Y, w, rowH,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            // A run is in flight iff a CTS is live (mirrors PolarAlignmentCts). No run -> setup form.
            if (state.FlatsCts is null)
            {
                RenderFlatsSetupRows(state, rect, x0, rect.Y + rowH + pad, w, rowH, fontPath, fontSize, dpiScale, pad);
                return;
            }

            RenderFlatsRunningRows(state, rect, x0, rect.Y + rowH + pad, w, rowH, fontPath, fontSize, dpiScale, pad);
        }

        /// <summary>
        /// Setup-phase rendering: source selector (3-way cycle), flats-per-filter stepper, a prominent
        /// Start button, and a Cancel-back-to-Preview. Posts <c>StartFlatsSignal</c> with the working-copy
        /// source + count when the user clicks Start.
        /// </summary>
        private void RenderFlatsSetupRows(
            LiveSessionState state, RectF32 rect,
            float x0, float y, float w, float rowH, string fontPath, float fontSize, float dpiScale, float pad)
        {
            var labelW = w * 0.42f;
            var btnW = (w - labelW) / 4f;
            var valW = (w - labelW) / 2f;
            var smallFs = fontSize * 0.78f;

            // ---- Illumination source (3-way cycle) ---------------------------
            DrawText("Source", fontPath, x0, y, labelW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            var srcBtn = Layout.Builder.Text(FlatSourceLabel(state.FlatSetupSource), smallFs / dpiScale, BodyText, TextAlign.Center, TextAlign.Center)
                .Stretch().Bg(new RGBAColor32(0x44, 0x66, 0x99, 0xff))
                .Clickable(new HitResult.ButtonHit("FlatsSetupSource"), _ =>
                {
                    state.FlatSetupSource = state.FlatSetupSource switch
                    {
                        FlatIlluminationChoice.Calibrator => FlatIlluminationChoice.SkyDusk,
                        FlatIlluminationChoice.SkyDusk => FlatIlluminationChoice.SkyDawn,
                        _ => FlatIlluminationChoice.Calibrator,
                    };
                    state.NeedsRedraw = true;
                });
            RenderLayout(srcBtn, new RectF32(x0 + labelW, y, w - labelW, rowH), fontPath, dpiScale);
            y += rowH;

            // ---- Flats per filter --------------------------------------------
            y = RenderConfigRow(
                "Per filter", $"{state.FlatSetupPerFilter}",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, dpiScale, pad,
                "FlatsSetupCountMinus",
                () =>
                {
                    state.FlatSetupPerFilter = Math.Max(1, state.FlatSetupPerFilter - 1);
                    state.NeedsRedraw = true;
                },
                "FlatsSetupCountPlus",
                () =>
                {
                    state.FlatSetupPerFilter = Math.Min(100, state.FlatSetupPerFilter + 1);
                    state.NeedsRedraw = true;
                });

            y += pad;

            // ---- Source hint -------------------------------------------------
            var hint = state.FlatSetupSource switch
            {
                FlatIlluminationChoice.Calibrator => "Uses the OTA's cover/calibrator (flip-flat, panel, or manual light panel). Converge once, then shoot.",
                FlatIlluminationChoice.SkyDusk => "Slews near the anti-solar zenith (tracking off) and re-meters each frame as the sky darkens.",
                FlatIlluminationChoice.SkyDawn => "Slews near the anti-solar zenith (tracking off) and re-meters each frame as the sky brightens.",
                _ => ""
            };
            DrawText(hint, fontPath, x0, y, w, rowH * 3, smallFs, DimText, TextAlign.Near, TextAlign.Near);

            // ---- Start / Cancel (anchored at panel bottom) -------------------
            var buttonY = rect.Y + rect.Height - rowH * 3 - pad * 3;
            RenderButton("Cancel", x0, buttonY, w, rowH * 1.2f,
                fontPath, fontSize * 0.85f,
                new RGBAColor32(0x33, 0x33, 0x3a, 0xff), DimText,
                "FlatsSetupBack",
                _ =>
                {
                    state.Mode = LiveSessionMode.Preview;
                    state.FlatStatusMessage = "";
                    state.NeedsRedraw = true;
                });
            buttonY += rowH * 1.2f + pad;

            var (canStart, reason) = EvaluateFlatsPreconditions(state);
            var startBg = canStart ? new RGBAColor32(0x44, 0xaa, 0x66, 0xff) : new RGBAColor32(0x33, 0x33, 0x3a, 0xff);
            var startFg = canStart ? BrightText : DimText;
            RenderButton("Start", x0, buttonY, w, rowH * 1.6f,
                fontPath, fontSize,
                startBg, startFg,
                "FlatsSetupStart",
                _ =>
                {
                    if (!canStart)
                    {
                        state.FlatStatusMessage = reason;
                        state.NeedsRedraw = true;
                        return;
                    }
                    PostSignal(new StartFlatsSignal(state.FlatSetupSource, state.FlatSetupPerFilter));
                });
        }

        /// <summary>
        /// Running-phase rendering: a phase pill driven by the session phase (Cooling / Flats /
        /// Finalising), the current-activity status line, and a Cancel button. The kept + metering
        /// frames appear in the centre preview viewer.
        /// </summary>
        private void RenderFlatsRunningRows(
            LiveSessionState state, RectF32 rect,
            float x0, float y, float w, float rowH, string fontPath, float fontSize, float dpiScale, float pad)
        {
            var (phaseLabel, phaseColor) = state.Phase switch
            {
                SessionPhase.Initialising => ("CONNECTING", StatusSlewing),
                SessionPhase.Cooling => ("COOLING", StatusSolving),
                SessionPhase.Flats => ("CAPTURING", StatusTracking),
                SessionPhase.Finalising => ("FINALISING", StatusSlewing),
                SessionPhase.Complete => ("COMPLETE", new RGBAColor32(0x44, 0xff, 0x44, 0xff)),
                SessionPhase.Aborted => ("CANCELLED", AbortBg),
                SessionPhase.Failed => ("FAILED", AbortBg),
                _ => (state.Phase.ToString().ToUpperInvariant(), DimText)
            };
            RenderLayout(
                Layout.Builder.Text(phaseLabel, BaseFontSize * 0.95f, BrightText, TextAlign.Center, TextAlign.Center).Bg(phaseColor),
                new RectF32(x0, y, w, rowH), fontPath, dpiScale);
            y += rowH + pad;

            // Source line
            DrawText($"Source: {FlatSourceLabel(state.FlatSetupSource)}", fontPath,
                x0, y, w, rowH, fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
            y += rowH;

            // Status line: the session's per-filter current-activity (set during the flat capture loop)
            // is the live progress signal -- flat frames don't flow through TotalFramesWritten, so there
            // is no session-wide count to show. Fall back to the panel's own FlatStatusMessage between
            // phases. Both are mirrored from the session by PollSession.
            var status = !string.IsNullOrEmpty(state.CurrentActivity)
                ? state.CurrentActivity
                : state.FlatStatusMessage;
            if (!string.IsNullOrEmpty(status))
            {
                DrawText(status, fontPath, x0, y, w, rowH * 3, fontSize * 0.85f, BodyText, TextAlign.Near, TextAlign.Near);
            }

            // Cancel (bottom). A terminal phase disables it (nothing left to cancel).
            var buttonY = rect.Y + rect.Height - rowH * 1.6f - pad * 2;
            var terminal = state.Phase is SessionPhase.Complete or SessionPhase.Aborted or SessionPhase.Failed;
            var cancelInFlight = state.FlatsCts is { IsCancellationRequested: true };
            var canCancel = !terminal && !cancelInFlight;
            var cancellingBg = new RGBAColor32(0xc4, 0x8a, 0x2c, 0xff);
            var (cancelLabel, cancelBg, cancelFg) = cancelInFlight
                ? ("Cancelling…", cancellingBg, BrightText)
                : canCancel
                    ? ("Cancel", AbortBg, AbortText)
                    : ("Cancel", new RGBAColor32(0x33, 0x33, 0x3a, 0xff), DimText);
            RenderButton(cancelLabel, x0, buttonY, w, rowH * 1.6f,
                fontPath, fontSize * 0.9f,
                cancelBg, cancelFg,
                "FlatsCancel",
                _ => { if (canCancel) PostSignal(new CancelFlatsSignal()); });
        }
    }
}
