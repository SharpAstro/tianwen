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
            var x0 = rect.X + pad;
            var w = rect.Width - pad * 2;

            // Header
            DrawText("Polar Alignment", fontPath,
                x0, rect.Y, w, rowH,
                fontSize, HeaderText, TextAlign.Near, TextAlign.Center);

            // Source toggle: [Main | Guider] -- moved here from the toolbar G button
            // so the choice lives next to its consumers (the polar side panel).
            // Switching mid-run would invalidate the Phase A v1 anchor frame, so the
            // buttons are inert (DimText) while polar is actually running; users can
            // still preview which source is active.
            var sourceY = rect.Y + rowH;
            var sourceLabelW = w * 0.30f;
            var srcBtnW = (w - sourceLabelW) / 2f;
            var canSwitchSource = state.PolarPhase == PolarAlignmentPhase.Idle
                || state.PolarPhase == PolarAlignmentPhase.Failed;
            DrawText("Source", fontPath,
                x0, sourceY, sourceLabelW, rowH,
                fontSize * 0.8f, DimText, TextAlign.Near, TextAlign.Center);
            var activeSrcBg = new RGBAColor32(0x44, 0x66, 0x99, 0xff);
            var inactiveSrcBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            var srcFg = canSwitchSource ? BodyText : DimText;

            RenderButton("Main",
                x0 + sourceLabelW, sourceY + 2, srcBtnW - 2, rowH - 4,
                fontPath, fontSize * 0.85f,
                state.PolarAlignUseGuider ? inactiveSrcBg : activeSrcBg,
                srcFg, "PolarSrcMain",
                _ =>
                {
                    if (canSwitchSource && state.PolarAlignUseGuider)
                    {
                        state.PolarAlignUseGuider = false;
                        state.NeedsRedraw = true;
                    }
                });
            RenderButton("Guider",
                x0 + sourceLabelW + srcBtnW, sourceY + 2, srcBtnW - 2, rowH - 4,
                fontPath, fontSize * 0.85f,
                state.PolarAlignUseGuider ? activeSrcBg : inactiveSrcBg,
                srcFg, "PolarSrcGuider",
                _ =>
                {
                    if (canSwitchSource && !state.PolarAlignUseGuider)
                    {
                        state.PolarAlignUseGuider = true;
                        state.NeedsRedraw = true;
                    }
                });

            // Setup phase: routine not yet started -> render the configuration
            // form + Start button instead of the running-phase status / gauges.
            // The Start button posts StartPolarAlignmentSignal with a snapshot
            // of state.PolarSetupConfig as the captured Configuration.
            if (state.PolarPhase == PolarAlignmentPhase.Idle && state.PolarAlignmentCts is null)
            {
                RenderPolarSetupRows(state, rect, x0, sourceY + rowH + pad, w, rowH, fontPath, fontSize, dpiScale, pad);
                return;
            }

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
            FillRect(x0, phaseY, w, rowH, phaseColor);
            DrawText(phaseLabel, fontPath,
                x0, phaseY, w, rowH,
                fontSize * 0.95f, BrightText, TextAlign.Center, TextAlign.Center);

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

            RenderButton(cancelLabel, x0, buttonY, halfW, rowH * 1.5f,
                fontPath, fontSize * 0.9f,
                cancelBg, cancelFg,
                "PolarCancel",
                _ => { if (canCancel) PostSignal(new CancelPolarAlignmentSignal()); });

            RenderButton("Done", x0 + halfW + pad, buttonY, halfW, rowH * 1.5f,
                fontPath, fontSize * 0.9f,
                canDone ? new RGBAColor32(0x44, 0xaa, 0x66, 0xff) : new RGBAColor32(0x33, 0x33, 0x3a, 0xff),
                canDone ? BrightText : DimText,
                "PolarDone",
                _ => { if (canDone) PostSignal(new DonePolarAlignmentSignal()); });
        }

        /// <summary>
        /// Setup-phase rendering: numeric +/- rows for the headline polar-align
        /// tunables, an "On done" cycle button, a "Save frames" toggle, and a
        /// prominent Start button. Posts <c>StartPolarAlignmentSignal</c> with
        /// the captured <c>state.PolarSetupConfig</c> when the user clicks Start.
        /// </summary>
        private void RenderPolarSetupRows(
            LiveSessionState state, RectF32 rect,
            float x0, float y, float w, float rowH, string fontPath, float fontSize, float dpiScale, float pad)
        {
            var labelW = w * 0.42f;
            var btnW = (w - labelW) / 4f;
            var valW = (w - labelW) / 2f;
            var smallFs = fontSize * 0.78f;

            // ---- Rotation (DeltaRaDeg) ---------------------------------------
            y = RenderConfigRow(
                "Rotation", $"{state.PolarSetupConfig.RotationDeg:F0}\u00B0",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, dpiScale, pad,
                "PolarSetupRotMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.RotationDeg - 15.0;
                    state.PolarSetupConfig = state.PolarSetupConfig with { RotationDeg = Math.Max(15.0, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupRotPlus",
                () =>
                {
                    var v = state.PolarSetupConfig.RotationDeg + 15.0;
                    state.PolarSetupConfig = state.PolarSetupConfig with { RotationDeg = Math.Min(180.0, v) };
                    state.NeedsRedraw = true;
                });

            // ---- Settle (SettleSeconds) --------------------------------------
            y = RenderConfigRow(
                "Settle", $"{state.PolarSetupConfig.SettleSeconds:F0}s",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, dpiScale, pad,
                "PolarSetupSettleMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.SettleSeconds - 1.0;
                    state.PolarSetupConfig = state.PolarSetupConfig with { SettleSeconds = Math.Max(0.0, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupSettlePlus",
                () =>
                {
                    var v = state.PolarSetupConfig.SettleSeconds + 1.0;
                    state.PolarSetupConfig = state.PolarSetupConfig with { SettleSeconds = Math.Min(30.0, v) };
                    state.NeedsRedraw = true;
                });

            // ---- Target accuracy (TargetAccuracyArcmin) ----------------------
            y = RenderConfigRow(
                "Target acc", $"{state.PolarSetupConfig.TargetAccuracyArcmin:F1}\u2032",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, dpiScale, pad,
                "PolarSetupAccMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.TargetAccuracyArcmin - 0.5;
                    state.PolarSetupConfig = state.PolarSetupConfig with { TargetAccuracyArcmin = Math.Max(0.5, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupAccPlus",
                () =>
                {
                    var v = state.PolarSetupConfig.TargetAccuracyArcmin + 0.5;
                    state.PolarSetupConfig = state.PolarSetupConfig with { TargetAccuracyArcmin = Math.Min(10.0, v) };
                    state.NeedsRedraw = true;
                });

            // ---- Min stars for solve -----------------------------------------
            y = RenderConfigRow(
                "Min stars", $"{state.PolarSetupConfig.MinStarsForSolve}",
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, dpiScale, pad,
                "PolarSetupMinStarsMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.MinStarsForSolve - 5;
                    state.PolarSetupConfig = state.PolarSetupConfig with { MinStarsForSolve = Math.Max(5, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupMinStarsPlus",
                () =>
                {
                    var v = state.PolarSetupConfig.MinStarsForSolve + 5;
                    state.PolarSetupConfig = state.PolarSetupConfig with { MinStarsForSolve = Math.Min(100, v) };
                    state.NeedsRedraw = true;
                });

            // ---- Re-seed interval (RefineFullSolveInterval) ------------------
            // 0 reads as "off" -- the orchestrator skips the periodic full-solve
            // re-seed and relies entirely on the residual-spike fallback.
            var reseedText = state.PolarSetupConfig.RefineFullSolveInterval <= 0
                ? "off"
                : $"{state.PolarSetupConfig.RefineFullSolveInterval}";
            y = RenderConfigRow(
                "Re-seed every", reseedText,
                x0, y, labelW, btnW, valW, rowH, fontPath, smallFs, dpiScale, pad,
                "PolarSetupReseedMinus",
                () =>
                {
                    var v = state.PolarSetupConfig.RefineFullSolveInterval - 10;
                    state.PolarSetupConfig = state.PolarSetupConfig with { RefineFullSolveInterval = Math.Max(0, v) };
                    state.NeedsRedraw = true;
                },
                "PolarSetupReseedPlus",
                () =>
                {
                    var v = state.PolarSetupConfig.RefineFullSolveInterval + 10;
                    state.PolarSetupConfig = state.PolarSetupConfig with { RefineFullSolveInterval = Math.Min(200, v) };
                    state.NeedsRedraw = true;
                });

            y += pad;

            // ---- On-done cycle (ReverseAxisBack / Park / LeaveInPlace) -------
            var onDoneLabel = state.PolarSetupConfig.OnDone switch
            {
                PolarAlignmentOnDone.ReverseAxisBack => "Reverse",
                PolarAlignmentOnDone.Park => "Park",
                PolarAlignmentOnDone.LeaveInPlace => "Leave",
                _ => "?"
            };
            DrawText("On done", fontPath, x0, y, labelW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            var onDoneBtn = Layout.Builder.Text(onDoneLabel, smallFs / dpiScale, BodyText, TextAlign.Center, TextAlign.Center)
                .Stretch().Bg(new RGBAColor32(0x44, 0x66, 0x99, 0xff))
                .Clickable(new HitResult.ButtonHit("PolarSetupOnDone"), _ =>
                {
                    var next = state.PolarSetupConfig.OnDone switch
                    {
                        PolarAlignmentOnDone.ReverseAxisBack => PolarAlignmentOnDone.Park,
                        PolarAlignmentOnDone.Park => PolarAlignmentOnDone.LeaveInPlace,
                        _ => PolarAlignmentOnDone.ReverseAxisBack,
                    };
                    state.PolarSetupConfig = state.PolarSetupConfig with { OnDone = next };
                    state.NeedsRedraw = true;
                });
            RenderLayout(onDoneBtn, new RectF32(x0 + labelW, y, w - labelW, rowH), fontPath, dpiScale);
            y += rowH;

            // ---- Save frames toggle ------------------------------------------
            var saveLabel = state.PolarSetupConfig.SaveFrames ? "On" : "Off";
            var saveBg = state.PolarSetupConfig.SaveFrames
                ? new RGBAColor32(0x44, 0x66, 0x99, 0xff)
                : new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            DrawText("Save frames", fontPath, x0, y, labelW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            var saveBtn = Layout.Builder.Text(saveLabel, smallFs / dpiScale, BodyText, TextAlign.Center, TextAlign.Center)
                .Stretch().Bg(saveBg)
                .Clickable(new HitResult.ButtonHit("PolarSetupSaveFrames"), _ =>
                {
                    state.PolarSetupConfig = state.PolarSetupConfig with { SaveFrames = !state.PolarSetupConfig.SaveFrames };
                    state.NeedsRedraw = true;
                });
            RenderLayout(saveBtn, new RectF32(x0 + labelW, y, w - labelW, rowH), fontPath, dpiScale);
            y += rowH;

            // ---- Incremental-solver toggle (diagnostic / safe fallback) ------
            var incLabel = state.PolarSetupConfig.UseIncrementalSolver ? "On" : "Off";
            var incBg = state.PolarSetupConfig.UseIncrementalSolver
                ? new RGBAColor32(0x44, 0x66, 0x99, 0xff)
                : new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            DrawText("Incremental", fontPath, x0, y, labelW, rowH, smallFs, DimText, TextAlign.Near, TextAlign.Center);
            var incBtn = Layout.Builder.Text(incLabel, smallFs / dpiScale, BodyText, TextAlign.Center, TextAlign.Center)
                .Stretch().Bg(incBg)
                .Clickable(new HitResult.ButtonHit("PolarSetupUseIncremental"), _ =>
                {
                    state.PolarSetupConfig = state.PolarSetupConfig with { UseIncrementalSolver = !state.PolarSetupConfig.UseIncrementalSolver };
                    state.NeedsRedraw = true;
                });
            RenderLayout(incBtn, new RectF32(x0 + labelW, y, w - labelW, rowH), fontPath, dpiScale);

            // ---- Start button (anchored at panel bottom, full width) ---------
            // Cancel-back-to-Preview lives above Start so the Start button sits
            // in the muscle-memory location for "primary action" (bottom).
            var buttonY = rect.Y + rect.Height - rowH * 3 - pad * 3;
            RenderButton("Cancel", x0, buttonY, w, rowH * 1.2f,
                fontPath, fontSize * 0.85f,
                new RGBAColor32(0x33, 0x33, 0x3a, 0xff), DimText,
                "PolarSetupBack",
                _ =>
                {
                    state.Mode = LiveSessionMode.Preview;
                    state.PolarStatusMessage = "";
                    state.NeedsRedraw = true;
                });
            buttonY += rowH * 1.2f + pad;

            // Authoritative pre-flight check mirrors EvaluatePolarPreconditions;
            // disabled-grey button gives the user a hint without burying them
            // in the side panel's status line.
            var (canStart, _) = EvaluatePolarPreconditions(state);
            var startBg = canStart ? new RGBAColor32(0x44, 0xaa, 0x66, 0xff) : new RGBAColor32(0x33, 0x33, 0x3a, 0xff);
            var startFg = canStart ? BrightText : DimText;
            RenderButton("Start", x0, buttonY, w, rowH * 1.6f,
                fontPath, fontSize,
                startBg, startFg,
                "PolarSetupStart",
                _ =>
                {
                    if (!canStart) return;
                    var miniIdx = PreviewView is not null ? _previewState.SelectedCameraIndex : -1;
                    var otaIdx = miniIdx >= 0 ? miniIdx : 0;
                    PostSignal(new StartPolarAlignmentSignal(
                        OtaIndex: otaIdx,
                        DeltaRaDeg: state.PolarSetupConfig.RotationDeg,
                        UseGuider: state.PolarAlignUseGuider,
                        Configuration: state.PolarSetupConfig));
                });
        }

        /// <summary>
        /// One row of the polar-align setup form: dim label + [-] + value display +
        /// [+]. Returns the y-cursor advanced past the row.
        /// </summary>
        private float RenderConfigRow(
            string label, string valueText,
            float x0, float y, float labelW, float btnW, float valW, float rowH,
            string fontPath, float fontSize, float dpiScale, float pad,
            string minusAction, Action onMinus,
            string plusAction, Action onPlus)
        {
            // Caller draws the dim label cell; the [-] value [+] control is one declarative stepper
            // node (each cell its own draw==hit leaf) instead of two RenderButton calls bracketing a
            // hand-positioned value DrawText. The bounds rect spans btnW+valW+btnW so the Star value
            // cell exactly fills the original value column; font / button width convert back to design
            // units (the engine re-applies dpiScale). pad is kept for signature stability (unused).
            DrawText(label, fontPath, x0, y, labelW, rowH, fontSize, DimText, TextAlign.Near, TextAlign.Center);
            var btnBg = new RGBAColor32(0x2a, 0x2a, 0x35, 0xff);
            var style = new FormRowLayout.StepperStyle(btnBg, BodyText, btnBg, DimText, fontSize / dpiScale, btnW / dpiScale);
            var ctrl = FormRowLayout.StepperControl(style,
                "-", minusAction, _ => onMinus(),
                "+", plusAction, _ => onPlus(),
                valueText, fontSize / dpiScale, BrightText, enabled: true);
            RenderLayout(ctrl, new RectF32(x0 + labelW, y, btnW + valW + btnW, rowH), fontPath, dpiScale);
            return y + rowH;
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
