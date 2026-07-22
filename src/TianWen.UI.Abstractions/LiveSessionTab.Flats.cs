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
        private void RenderFlatsSidePanel(LiveSessionState state, RectF32 rect,
            float fontSize, float pad, float rowH)
        {
            var fontPath = FontPath;
            // A run is in flight iff a CTS is live (mirrors PolarAlignmentCts). Running -> phase/status/Cancel.
            if (state.FlatsCts is not null)
            {
                DrawText("Flat Capture", fontPath,
                    rect.X + pad, rect.Y, rect.Width - pad * 2, rowH, fontSize, HeaderText, TextAlign.Near, TextAlign.Center);
                RenderFlatsRunningRows(state, rect, rect.X + pad, rect.Y + rowH + pad, rect.Width - pad * 2, rowH, fontSize, pad);
                return;
            }

            // SETUP: the WHOLE panel is ONE tree rooted at the host-provided rect -- there is no internal
            // x0/y cursor and no per-row `new RectF32`. Header + source + count + hint stack from the top
            // (the Dock fill); Cancel/Start pin to the bottom (Dock.Bottom). Every sub-rect the panel draws
            // comes from the arrangement, not hand-computed pixels; the only rect passed is the panel's own.
            var hint = state.FlatSetupSource switch
            {
                FlatIlluminationChoice.Calibrator => "Uses the OTA's cover/calibrator (flip-flat, panel, or manual light panel). Converge once, then shoot.",
                FlatIlluminationChoice.SkyDusk => "Slews near the anti-solar zenith (tracking off) and re-meters each frame as the sky darkens.",
                FlatIlluminationChoice.SkyDawn => "Slews near the anti-solar zenith (tracking off) and re-meters each frame as the sky brightens.",
                _ => ""
            };
            var (canStart, reason) = EvaluateFlatsPreconditions(state);

            Layout.Node LabeledRow(string label, Layout.Node control) =>
                Layout.Builder.HStack(
                        Layout.Builder.Text(label, BaseFontSize * 0.78f, DimText).WStar(0.42f).HStar(),
                        control.WStar(0.58f).HStar())
                    .RowH(BaseRowHeight);

            Layout.Node CountStep(string glyph, string action, Action onClick) =>
                Layout.Builder.Text(glyph, BaseFontSize * 0.78f, BodyText, TextAlign.Center, TextAlign.Center)
                    .WFixed(24f).HStar().Bg(new RGBAColor32(0x2a, 0x2a, 0x35, 0xff))
                    .Clickable(new HitResult.ButtonHit(action), _ => { onClick(); });

            Layout.Node Button(string label, string action, float rowScale, RGBAColor32 bg, RGBAColor32 fg, Action<InputModifier> onClick) =>
                Layout.Builder.Text(label, BaseFontSize * (rowScale > 1.4f ? 1f : 0.85f), fg, TextAlign.Center, TextAlign.Center)
                    .RowH(BaseRowHeight * rowScale).Bg(bg)
                    .Clickable(new HitResult.ButtonHit(action), onClick);

            var content = Layout.Builder.VStack(
                Layout.Builder.Text("Flat Capture", BaseFontSize, HeaderText).RowH(BaseRowHeight),
                Layout.Builder.Spacer().RowH(BasePadding),
                LabeledRow("Source",
                    Layout.Builder.Text(FlatSourceLabel(state.FlatSetupSource), BaseFontSize * 0.78f, BodyText, TextAlign.Center, TextAlign.Center)
                        .Bg(new RGBAColor32(0x44, 0x66, 0x99, 0xff))
                        .Clickable(new HitResult.ButtonHit("FlatsSetupSource"), _ =>
                        {
                            state.FlatSetupSource = state.FlatSetupSource switch
                            {
                                FlatIlluminationChoice.Calibrator => FlatIlluminationChoice.SkyDusk,
                                FlatIlluminationChoice.SkyDusk => FlatIlluminationChoice.SkyDawn,
                                _ => FlatIlluminationChoice.Calibrator,
                            };
                            state.NeedsRedraw = true;
                        })),
                LabeledRow("Per filter",
                    Layout.Builder.HStack(
                        CountStep("-", "FlatsSetupCountMinus", () => { state.FlatSetupPerFilter = Math.Max(1, state.FlatSetupPerFilter - 1); state.NeedsRedraw = true; }),
                        Layout.Builder.Text($"{state.FlatSetupPerFilter}", BaseFontSize * 0.78f, BrightText, TextAlign.Center, TextAlign.Center).Stretch(),
                        CountStep("+", "FlatsSetupCountPlus", () => { state.FlatSetupPerFilter = Math.Min(100, state.FlatSetupPerFilter + 1); state.NeedsRedraw = true; }))),
                Layout.Builder.Spacer().RowH(BasePadding),
                Layout.Builder.Text(hint, BaseFontSize * 0.78f, DimText, TextAlign.Near, TextAlign.Near).RowH(BaseRowHeight * 3f));

            var buttons = Layout.Builder.VStack(
                Button("Cancel", "FlatsSetupBack", 1.2f, new RGBAColor32(0x33, 0x33, 0x3a, 0xff), DimText,
                    _ => { state.Mode = LiveSessionMode.Preview; state.FlatStatusMessage = ""; state.NeedsRedraw = true; }),
                Layout.Builder.Spacer().RowH(BasePadding),
                Button("Start", "FlatsSetupStart", 1.6f,
                    canStart ? new RGBAColor32(0x44, 0xaa, 0x66, 0xff) : new RGBAColor32(0x33, 0x33, 0x3a, 0xff),
                    canStart ? BrightText : DimText,
                    _ =>
                    {
                        if (!canStart) { state.FlatStatusMessage = reason; state.NeedsRedraw = true; return; }
                        PostSignal(new StartFlatsSignal(state.FlatSetupSource, state.FlatSetupPerFilter));
                    }));

            var bottomH = BaseRowHeight * 1.2f + BasePadding + BaseRowHeight * 1.6f;
            var tree = Layout.Builder.Dock(content, Layout.Builder.Bottom(buttons, bottomH)).Pad(BasePadding);
            RenderLayout(tree, rect);
        }

        /// <summary>
        /// Running-phase rendering: a phase pill driven by the session phase (Cooling / Flats /
        /// Finalising), the current-activity status line, and a Cancel button. The kept + metering
        /// frames appear in the centre preview viewer.
        /// </summary>
        private void RenderFlatsRunningRows(
            LiveSessionState state, RectF32 rect,
            float x0, float y, float w, float rowH, float fontSize, float pad)
        {
            var fontPath = FontPath;
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
                new RectF32(x0, y, w, rowH));
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
            var cancelNode = Layout.Builder.Text(cancelLabel, fontSize * 0.9f, cancelFg, TextAlign.Center, TextAlign.Center)
                .Stretch().Bg(cancelBg)
                .Clickable(new HitResult.ButtonHit("FlatsCancel"), _ => { if (canCancel) PostSignal(new CancelFlatsSignal()); });
            RenderLayout(cancelNode, new RectF32(x0, buttonY, w, rowH * 1.6f), dpiScale: 1f);
        }
    }
}
