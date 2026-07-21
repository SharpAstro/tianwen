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
    /// Polar-alignment mode: preconditions, side panel, setup rows, config rows, error gauges.
    /// </summary>
    public partial class LiveSessionTab<TSurface>
    {
        /// <summary>
        /// Surface-level precondition check for the toolbar PA button. Mirrors the
        /// authoritative check in <c>AppSignalHandler.StartPolarAlignmentSignal</c> —
        /// the handler re-validates and returns its own notifications so this is
        /// purely a UX hint about whether the click will succeed.
        /// </summary>
        private static (bool Enabled, string Reason) EvaluatePolarPreconditions(LiveSessionState state)
        {
            // Routine already running -> the PA button becomes a Cancel; the caller
            // handles that via the "active" branch. Treat as enabled here.
            if (state.Mode == LiveSessionMode.PolarAlign)
            {
                return (true, "Cancel polar alignment");
            }
            if (state.OtaCount == 0)
            {
                return (false, "Polar align: no OTA configured");
            }
            if (string.IsNullOrEmpty(state.MountDisplayName))
            {
                return (false, "Polar align: connect a mount first");
            }
            return (true, "Start polar alignment");
        }

        /// <summary>
        /// Side panel for polar-align mode: status pill, error needles, exposure
        /// indicator, IsSettled / IsAligned LEDs, direction-hint badges, Cancel /
        /// Done buttons. Replaces the right-hand exposure-log panel while
        /// <see cref="LiveSessionMode.PolarAlign"/> is active.
        /// </summary>
        private void RenderPolarSidePanel(LiveSessionState state, RectF32 rect, string fontPath,
            float fontSize, float dpiScale, float pad, float rowH)
        {
            // Setup phase: routine not yet started -> the whole panel is ONE arranged tree (header +
            // source + config + bottom-pinned Cancel/Start). The running phase keeps its status/gauge
            // flow (raster gauges) below.
            if (state.PolarPhase == PolarAlignmentPhase.Idle && state.PolarAlignmentCts is null)
            {
                RenderPolarSetupPanel(state, rect, fontPath, dpiScale);
                return;
            }

            var x0 = rect.X + pad;
            var w = rect.Width - pad * 2;

            // Header
            DrawText("Polar Alignment", fontPath,
                x0, rect.Y, w, rowH,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            // Source toggle (greyed while running -- switching mid-run would invalidate the anchor frame).
            var sourceY = rect.Y + rowH;
            RenderLayout(PolarSourceRow(state), new RectF32(x0, sourceY, w, rowH), fontPath, dpiScale);

            // Phase pill
            var phaseY = sourceY + rowH + pad;
            var (phaseLabel, phaseColor) = state.PolarPhase switch
            {
                PolarAlignmentPhase.Idle => ("IDLE", DimText),
                PolarAlignmentPhase.ProbingExposure => ("PROBING", StatusSolving),
                PolarAlignmentPhase.Rotating => ("ROTATING", StatusSlewing),
                PolarAlignmentPhase.Frame2 => ("FRAME 2", StatusSolving),
                PolarAlignmentPhase.Refining => ("REFINING", StatusTracking),
                PolarAlignmentPhase.Aligned => ("ALIGNED", new RGBAColor32(0x44, 0xff, 0x44, 0xff)),
                PolarAlignmentPhase.RestoringMount => ("RESTORING", StatusSlewing),
                PolarAlignmentPhase.Failed => ("FAILED", AbortBg),
                _ => ("?", DimText)
            };
            RenderLayout(
                Layout.Builder.Text(phaseLabel, BaseFontSize * 0.95f, BrightText, TextAlign.Center, TextAlign.Center).Bg(phaseColor),
                new RectF32(x0, phaseY, w, rowH), fontPath, dpiScale);

            var y = phaseY + rowH + pad;

            // Status message
            if (state.PolarStatusMessage is { Length: > 0 } status)
            {
                var statusH = rowH * 2;
                DrawText(status, fontPath,
                    x0, y, w, statusH,
                    fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Near);
                y += statusH + pad;
            }

            // Phase A info: locked exposure + chord-angle sanity readout
            if (state.PolarPhaseAResult is { Success: true } phaseA)
            {
                var lockedMs = phaseA.LockedExposure.TotalMilliseconds;
                DrawText(
                    $"Locked: {lockedMs:F0}ms  ({phaseA.StarsMatchedFrame1}/{phaseA.StarsMatchedFrame2} stars)",
                    fontPath,
                    x0, y, w, rowH,
                    fontSize * 0.78f, DimText, TextAlign.Near, TextAlign.Center);
                y += rowH;

                var chordObsArcsec = phaseA.ChordAngleObservedRad * 180.0 / Math.PI * 3600.0;
                var chordPredArcsec = phaseA.ChordAnglePredictedRad * 180.0 / Math.PI * 3600.0;
                var chordDiff = Math.Abs(chordObsArcsec - chordPredArcsec);
                var chordColor = chordDiff < 5 ? StatusTracking : chordDiff < 30 ? StatusSlewing : AbortBg;
                DrawText($"Chord \u0394: {chordDiff:F1}\u2033", fontPath,
                    x0, y, w, rowH,
                    fontSize * 0.78f, chordColor, TextAlign.Near, TextAlign.Center);
                y += rowH + pad;
            }

            // Live solve gauges (Az / Alt error needles)
            if (state.LastPolarSolve is { } solve)
            {
                y = RenderPolarErrorGauges(state, solve, x0, y, w, rowH, fontPath, fontSize, pad);
            }

            // Cancel / Done buttons (bottom of panel). The Cancel button gates on
            // three states: idle (greyed), active (red, clickable), and
            // cancellation-in-flight (amber, disabled, "Cancelling..." label).
            // The intermediate state covers the gap between the user's click and
            // the session's RestoringMount cleanup completing -- otherwise a
            // mash of repeated clicks could fire multiple Cancel signals or the
            // user wouldn't know the request was actually picked up.
            var buttonY = rect.Y + rect.Height - rowH * 2 - pad * 2;
            var halfW = (w - pad) / 2;
            var cancelInFlight = state.PolarAlignmentCts is { IsCancellationRequested: true };
            var canCancel = state.Mode == LiveSessionMode.PolarAlign
                && state.PolarPhase != PolarAlignmentPhase.Idle
                && !cancelInFlight;
            var canDone = state.PolarPhase == PolarAlignmentPhase.Aligned
                || (state.PolarPhase == PolarAlignmentPhase.Refining && state.LastPolarSolve is { IsSettled: true, IsAligned: true });

            // Amber for "in progress" -- distinct from the active red and the
            // disabled grey so the state is unambiguous at a glance.
            var cancellingBg = new RGBAColor32(0xc4, 0x8a, 0x2c, 0xff);
            var (cancelLabel, cancelBg, cancelFg) = cancelInFlight
                ? ("Cancelling\u2026", cancellingBg, BrightText)
                : canCancel
                    ? ("Cancel", AbortBg, AbortText)
                    : ("Cancel", new RGBAColor32(0x33, 0x33, 0x3a, 0xff), DimText);

            // [Cancel | Done] as one HStack (was two RenderButtons bracketing a pad gap).
            var buttonRow = Layout.Builder.HStack(
                    Layout.Builder.Text(cancelLabel, BaseFontSize * 0.9f, cancelFg, TextAlign.Center, TextAlign.Center)
                        .WStar().HStar().Bg(cancelBg)
                        .Clickable(new HitResult.ButtonHit("PolarCancel"), _ => { if (canCancel) PostSignal(new CancelPolarAlignmentSignal()); }),
                    Layout.Builder.Text("Done", BaseFontSize * 0.9f, canDone ? BrightText : DimText, TextAlign.Center, TextAlign.Center)
                        .WStar().HStar().Bg(canDone ? new RGBAColor32(0x44, 0xaa, 0x66, 0xff) : new RGBAColor32(0x33, 0x33, 0x3a, 0xff))
                        .Clickable(new HitResult.ButtonHit("PolarDone"), _ => { if (canDone) PostSignal(new DonePolarAlignmentSignal()); }))
                .WithGap(BasePadding);
            RenderLayout(buttonRow, new RectF32(x0, buttonY, w, rowH * 1.5f), fontPath, dpiScale);
        }

        /// <summary>
        /// Setup-phase panel as ONE arranged tree: header + source toggle + five numeric config rows +
        /// an On-done cycle + Save-frames / Incremental toggles stack from the top (the Dock fill), and
        /// Cancel / Start pin to the bottom (Dock.Bottom). No internal cursor -- the only constructed rect
        /// is the panel rect. Start posts StartPolarAlignmentSignal with a snapshot of PolarSetupConfig.
        /// </summary>
        private void RenderPolarSetupPanel(LiveSessionState state, RectF32 rect, string fontPath, float dpiScale)
        {
            var (canStart, _) = EvaluatePolarPreconditions(state);
            var cfg = state.PolarSetupConfig;
            var toggleActiveBg = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
            var toggleInactiveBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);

            Layout.Node ConfigRow(string label, string valueText, string minusAction, Action onMinus, string plusAction, Action onPlus)
            {
                var btnBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
                var style = new FormRowLayout.StepperStyle(btnBg, BodyText, btnBg, DimText, BaseFontSize * 0.78f, 34f);
                var stepper = FormRowLayout.StepperControl(style,
                    "-", minusAction, _ => onMinus(),
                    "+", plusAction, _ => onPlus(),
                    valueText, BaseFontSize * 0.78f, BrightText, enabled: true);
                return Layout.Builder.HStack(
                        Layout.Builder.Text(label, BaseFontSize * 0.78f, DimText).WStar(0.42f).HStar(),
                        stepper.WStar(0.58f).HStar())
                    .RowH(BaseRowHeight);
            }

            Layout.Node ChoiceRow(string label, string valueText, RGBAColor32 bg, string action, Action onClick) =>
                Layout.Builder.HStack(
                        Layout.Builder.Text(label, BaseFontSize * 0.78f, DimText).WStar(0.42f).HStar(),
                        Layout.Builder.Text(valueText, BaseFontSize * 0.78f, BodyText, TextAlign.Center, TextAlign.Center)
                            .WStar(0.58f).HStar().Bg(bg)
                            .Clickable(new HitResult.ButtonHit(action), _ => { onClick(); }))
                    .RowH(BaseRowHeight);

            var onDoneLabel = cfg.OnDone switch
            {
                PolarAlignmentOnDone.ReverseAxisBack => "Reverse",
                PolarAlignmentOnDone.Park => "Park",
                PolarAlignmentOnDone.LeaveInPlace => "Leave",
                _ => "?"
            };
            var reseedText = cfg.RefineFullSolveInterval <= 0 ? "off" : $"{cfg.RefineFullSolveInterval}";

            var content = Layout.Builder.VStack(
                Layout.Builder.Text("Polar Alignment", BaseFontSize, HeaderText).RowH(BaseRowHeight),
                PolarSourceRow(state),
                ConfigRow("Rotation", $"{cfg.RotationDeg:F0}°",
                    "PolarSetupRotMinus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { RotationDeg = Math.Max(15.0, state.PolarSetupConfig.RotationDeg - 15.0) }; state.NeedsRedraw = true; },
                    "PolarSetupRotPlus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { RotationDeg = Math.Min(180.0, state.PolarSetupConfig.RotationDeg + 15.0) }; state.NeedsRedraw = true; }),
                ConfigRow("Settle", $"{cfg.SettleSeconds:F0}s",
                    "PolarSetupSettleMinus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { SettleSeconds = Math.Max(0.0, state.PolarSetupConfig.SettleSeconds - 1.0) }; state.NeedsRedraw = true; },
                    "PolarSetupSettlePlus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { SettleSeconds = Math.Min(30.0, state.PolarSetupConfig.SettleSeconds + 1.0) }; state.NeedsRedraw = true; }),
                ConfigRow("Target acc", $"{cfg.TargetAccuracyArcmin:F1}′",
                    "PolarSetupAccMinus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { TargetAccuracyArcmin = Math.Max(0.5, state.PolarSetupConfig.TargetAccuracyArcmin - 0.5) }; state.NeedsRedraw = true; },
                    "PolarSetupAccPlus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { TargetAccuracyArcmin = Math.Min(10.0, state.PolarSetupConfig.TargetAccuracyArcmin + 0.5) }; state.NeedsRedraw = true; }),
                ConfigRow("Min stars", $"{cfg.MinStarsForSolve}",
                    "PolarSetupMinStarsMinus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { MinStarsForSolve = Math.Max(5, state.PolarSetupConfig.MinStarsForSolve - 5) }; state.NeedsRedraw = true; },
                    "PolarSetupMinStarsPlus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { MinStarsForSolve = Math.Min(100, state.PolarSetupConfig.MinStarsForSolve + 5) }; state.NeedsRedraw = true; }),
                ConfigRow("Re-seed every", reseedText,
                    "PolarSetupReseedMinus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { RefineFullSolveInterval = Math.Max(0, state.PolarSetupConfig.RefineFullSolveInterval - 10) }; state.NeedsRedraw = true; },
                    "PolarSetupReseedPlus", () => { state.PolarSetupConfig = state.PolarSetupConfig with { RefineFullSolveInterval = Math.Min(200, state.PolarSetupConfig.RefineFullSolveInterval + 10) }; state.NeedsRedraw = true; }),
                Layout.Builder.Spacer().RowH(BasePadding),
                ChoiceRow("On done", onDoneLabel, toggleActiveBg, "PolarSetupOnDone", () =>
                {
                    var next = state.PolarSetupConfig.OnDone switch
                    {
                        PolarAlignmentOnDone.ReverseAxisBack => PolarAlignmentOnDone.Park,
                        PolarAlignmentOnDone.Park => PolarAlignmentOnDone.LeaveInPlace,
                        _ => PolarAlignmentOnDone.ReverseAxisBack,
                    };
                    state.PolarSetupConfig = state.PolarSetupConfig with { OnDone = next };
                    state.NeedsRedraw = true;
                }),
                ChoiceRow("Save frames", cfg.SaveFrames ? "On" : "Off", cfg.SaveFrames ? toggleActiveBg : toggleInactiveBg, "PolarSetupSaveFrames", () =>
                {
                    state.PolarSetupConfig = state.PolarSetupConfig with { SaveFrames = !state.PolarSetupConfig.SaveFrames };
                    state.NeedsRedraw = true;
                }),
                ChoiceRow("Incremental", cfg.UseIncrementalSolver ? "On" : "Off", cfg.UseIncrementalSolver ? toggleActiveBg : toggleInactiveBg, "PolarSetupUseIncremental", () =>
                {
                    state.PolarSetupConfig = state.PolarSetupConfig with { UseIncrementalSolver = !state.PolarSetupConfig.UseIncrementalSolver };
                    state.NeedsRedraw = true;
                }));

            var buttons = Layout.Builder.VStack(
                Layout.Builder.Text("Cancel", BaseFontSize * 0.85f, DimText, TextAlign.Center, TextAlign.Center)
                    .RowH(BaseRowHeight * 1.2f).Bg(new RGBAColor32(0x33, 0x33, 0x3a, 0xff))
                    .Clickable(new HitResult.ButtonHit("PolarSetupBack"), _ =>
                    {
                        state.Mode = LiveSessionMode.Preview;
                        state.PolarStatusMessage = "";
                        state.NeedsRedraw = true;
                    }),
                Layout.Builder.Spacer().RowH(BasePadding),
                Layout.Builder.Text("Start", BaseFontSize, canStart ? BrightText : DimText, TextAlign.Center, TextAlign.Center)
                    .RowH(BaseRowHeight * 1.6f).Bg(canStart ? new RGBAColor32(0x44, 0xaa, 0x66, 0xff) : new RGBAColor32(0x33, 0x33, 0x3a, 0xff))
                    .Clickable(new HitResult.ButtonHit("PolarSetupStart"), _ =>
                    {
                        if (!canStart) return;
                        var miniIdx = PreviewView is not null ? _previewState.SelectedCameraIndex : -1;
                        var otaIdx = miniIdx >= 0 ? miniIdx : 0;
                        PostSignal(new StartPolarAlignmentSignal(
                            OtaIndex: otaIdx,
                            DeltaRaDeg: state.PolarSetupConfig.RotationDeg,
                            UseGuider: state.PolarAlignUseGuider,
                            Configuration: state.PolarSetupConfig));
                    }));

            var bottomH = BaseRowHeight * 1.2f + BasePadding + BaseRowHeight * 1.6f;
            var tree = Layout.Builder.Dock(content, Layout.Builder.Bottom(buttons, bottomH)).Pad(BasePadding);
            RenderLayout(tree, rect, fontPath, dpiScale);
        }

        /// <summary>
        /// The [Source | Main | Guider] toggle as a Layout.Node, shared by the setup tree and the running
        /// panel. Switching is inert while a run is in flight (only Idle / Failed can switch source).
        /// </summary>
        private Layout.Node PolarSourceRow(LiveSessionState state)
        {
            var canSwitchSource = state.PolarPhase == PolarAlignmentPhase.Idle
                || state.PolarPhase == PolarAlignmentPhase.Failed;
            var activeSrcBg = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
            var inactiveSrcBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            var srcFg = canSwitchSource ? BodyText : DimText;
            return Layout.Builder.HStack(
                    Layout.Builder.Text("Source", BaseFontSize * 0.8f, DimText).WStar(0.30f).HStar(),
                    Layout.Builder.Text("Main", BaseFontSize * 0.85f, srcFg, TextAlign.Center, TextAlign.Center)
                        .WStar(0.35f).HStar().Bg(state.PolarAlignUseGuider ? inactiveSrcBg : activeSrcBg)
                        .Clickable(new HitResult.ButtonHit("PolarSrcMain"), _ =>
                        {
                            if (canSwitchSource && state.PolarAlignUseGuider) { state.PolarAlignUseGuider = false; state.NeedsRedraw = true; }
                        }),
                    Layout.Builder.Text("Guider", BaseFontSize * 0.85f, srcFg, TextAlign.Center, TextAlign.Center)
                        .WStar(0.35f).HStar().Bg(state.PolarAlignUseGuider ? activeSrcBg : inactiveSrcBg)
                        .Clickable(new HitResult.ButtonHit("PolarSrcGuider"), _ =>
                        {
                            if (canSwitchSource && !state.PolarAlignUseGuider) { state.PolarAlignUseGuider = true; state.NeedsRedraw = true; }
                        }))
                .WithGap(2f).RowH(BaseRowHeight);
        }

        private float RenderPolarErrorGauges(
            LiveSessionState state,
            LiveSolveResult solve,
            float x0, float y, float w, float rowH, string fontPath, float fontSize, float pad)
        {
            const double radToArcmin = 60.0 * 180.0 / Math.PI;
            // Use raw (per-frame) errors on the gauge so the user sees their
            // knob nudges land within one solve cycle (~250ms). The smoothed
            // values are still consumed by the IsSettled / IsAligned latches
            // -- the gauge needs responsiveness, the latches need stability.
            // Stale solve (lost lock near pole, FOV slewed away from catalog
            // window): NaN errors / null WCS. Surface a "Solve lost" badge
            // and skip the gauge so the user knows the readout isn't tracking
            // their knob nudges anymore.
            if (solve.Wcs is null || double.IsNaN(solve.AzErrorRad))
            {
                DrawText($"Solve lost ({solve.ConsecutiveFailedSolves} fail)",
                    fontPath, x0, y, w, rowH,
                    fontSize * 0.85f, AbortBg, TextAlign.Near, TextAlign.Center);
                y += rowH + pad;
                DrawText("Move mount slightly off pole and re-solve",
                    fontPath, x0, y, w, rowH,
                    fontSize * 0.78f, DimText, TextAlign.Near, TextAlign.Center);
                y += rowH + pad;
                return y;
            }
            var azArcmin = solve.AzErrorRad * radToArcmin;
            var altArcmin = solve.AltErrorRad * radToArcmin;

            DrawText("Az error", fontPath,
                x0, y, w, rowH,
                fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
            y = RenderErrorBar(azArcmin, x0, y + rowH, w, rowH * 0.6f, fontPath, fontSize);
            y += pad;

            DrawText("Alt error", fontPath,
                x0, y, w, rowH,
                fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
            y = RenderErrorBar(altArcmin, x0, y + rowH, w, rowH * 0.6f, fontPath, fontSize);
            y += pad;

            // Direction hint badges (where to push the knobs).
            var azHint = azArcmin >= 0 ? "\u2192 East" : "\u2190 West";
            var altHint = altArcmin >= 0 ? "\u2191 Up" : "\u2193 Down";
            DrawText($"Az: {azHint} {Math.Abs(azArcmin):F1}\u2032", fontPath,
                x0, y, w, rowH,
                fontSize * 0.85f, BodyText, TextAlign.Near, TextAlign.Center);
            y += rowH;
            DrawText($"Alt: {altHint} {Math.Abs(altArcmin):F1}\u2032", fontPath,
                x0, y, w, rowH,
                fontSize * 0.85f, BodyText, TextAlign.Near, TextAlign.Center);
            y += rowH + pad;

            // Exposure / star indicator
            DrawText(
                $"{solve.ExposureUsed.TotalMilliseconds:F0}ms  {solve.StarsMatched} stars"
                + (solve.ConsecutiveFailedSolves > 0 ? $"  ({solve.ConsecutiveFailedSolves} fail)" : ""),
                fontPath,
                x0, y, w, rowH,
                fontSize * 0.78f, DimText, TextAlign.Near, TextAlign.Center);
            y += rowH;

            // IsSettled / IsAligned LEDs
            var ledY = y;
            var ledSize = rowH * 0.6f;
            var settledColor = solve.IsSettled ? StatusTracking : DimText;
            var alignedColor = solve.IsAligned ? new RGBAColor32(0x44, 0xff, 0x44, 0xff) : DimText;
            FillRect(x0, ledY + (rowH - ledSize) / 2, ledSize, ledSize, settledColor);
            DrawText("Settled", fontPath,
                x0 + ledSize + pad, ledY, w - ledSize - pad, rowH,
                fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
            ledY += rowH;
            FillRect(x0, ledY + (rowH - ledSize) / 2, ledSize, ledSize, alignedColor);
            DrawText("Aligned", fontPath,
                x0 + ledSize + pad, ledY, w - ledSize - pad, rowH,
                fontSize * 0.8f, BodyText, TextAlign.Near, TextAlign.Center);
            return ledY + rowH;
        }

        private float RenderErrorBar(double arcmin, float x, float y, float w, float h, string fontPath, float fontSize)
        {
            // Centred zero-line bar; needle position scaled to a +/- 30' span (clamped).
            FillRect(x, y, w, h, GraphBg);
            var midX = x + w / 2;
            FillRect(midX, y, 1, h, SeparatorColor);

            const double FullScaleArcmin = 30.0;
            var clamped = Math.Clamp(arcmin, -FullScaleArcmin, FullScaleArcmin);
            var fraction = (float)(clamped / FullScaleArcmin);

            var absArcmin = Math.Abs(arcmin);
            var color = absArcmin < 1.0 ? StatusTracking
                : absArcmin < 5.0 ? StatusSlewing
                : AbortBg;
            var needleX = midX + fraction * (w / 2);
            var needleW = Math.Max(2f, h * 0.25f);
            FillRect(needleX - needleW / 2, y, needleW, h, color);

            DrawText($"{arcmin:+0.0;-0.0}\u2032", fontPath,
                x, y, w, h,
                fontSize * 0.8f, BrightText, TextAlign.Center, TextAlign.Center);
            return y + h;
        }
    }
}
