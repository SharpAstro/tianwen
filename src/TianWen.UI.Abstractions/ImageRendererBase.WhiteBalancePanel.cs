using System;
using DIR.Lib;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// The white-balance popover: the R / G / B sliders, Auto and Reset, under the toolbar's
    /// <see cref="ToolbarAction.WhiteBalance"/> button.
    /// </summary>
    /// <remarks>
    /// <para><b>A section of the docked info strip until 2026-09-11</b>, moved at the user's request:
    /// three sliders and two buttons that most frames never touch were standing open under thirteen
    /// rows of statistics. As a popover they are one click away, the strip keeps to what it reports,
    /// and the button does the one thing the section never could: say from across the bar, by its
    /// highlight, that a white balance is in force.</para>
    /// <para><b>A menu in everything but its contents.</b> Painted with the dropdowns so its regions
    /// win z-order; a full-window backdrop registered under it closes it on a press anywhere else and
    /// consumes that press, exactly as <c>RenderDropdownMenu</c> does; it claims the keyboard as it
    /// paints so Escape closes it through the one claimant check in the key handler rather than a
    /// second Escape branch; and while it is open it owns the pointer
    /// (<see cref="ViewerState.OverlayOwnsPointer"/>). Closed, it leaves no slider band behind.</para>
    /// </remarks>
    partial class ImageRendererBase<TSurface>
    {
        // Manual white-balance slider track rects (R, G, B), captured in RenderInfoPanel each frame; map a
        // cursor-X <-> WB multiplier in BeginWhiteBalanceDragAt / UpdateWhiteBalanceDrag. Default/empty when
        // the source is monochrome (no WB sliders drawn).
        private readonly RectF32[] _wbTrackRects = new RectF32[3];

        // White-balance slider range (canonical values live on GrayWorldWhiteBalance so the slider extent and
        // the auto-WB clamp stay in lock-step). Log-mapped so neutral (1.0) sits at the track midpoint and an
        // equal gain/cut is symmetric (0.5x left edge <-> 2.0x right edge).
        // Slider TRAVEL, and deliberately NOT GrayWorldWhiteBalance's clamp, which is what these two
        // used to be. They are different concerns that happened to share a value: [0.5, 2] bounds
        // what a gray-world ESTIMATE is allowed to return, whereas this is the range the sliders can
        // DISPLAY -- and a photometric calibration routinely lands outside it (an LPS-filtered
        // IMX492 SMC master fits R = 0.463). Borrowing the estimator's clamp silently rounded a real
        // measurement up to 0.50 on the way to the handle, so the slider disagreed with the render it
        // exists to report.
        //
        // 0.25 and 4 keep neutral 1.0 exactly at the track midpoint under the log mapping
        // (sqrt(0.25 * 4) = 1), which is the property the handle position depends on.
        private const float WbMin = 0.25f;
        private const float WbMax = 4.0f;
        // -----------------------------------------------------------------------
        // Manual white-balance sliders (the popover; shared across FITS / TIFF / SER)
        //
        // Three log-mapped sliders (R/G/B) over [WbMin, WbMax] with neutral 1.0 at the track midpoint,
        // plus a Reset. Drag is press + move + release (mirrors the transport scrub): a press begins a
        // drag on the hit channel, mouse-move maps cursor-X -> multiplier, release ends it. A WB change
        // only re-derives the stretch uniforms from cached stats (no pixel pass), so it sets NeedsRedraw,
        // never NeedsTextureUpdate.
        // -----------------------------------------------------------------------

        /// <summary>Every label the Reset button can carry, so its width can be reserved.</summary>
        private static readonly string[] ResetLabels = ["Reset WB", "Reset to calibrated"];

        /// <summary>Every label the calibration button can carry. "Calibrate" is the widest.</summary>
        private static readonly string[] SpccLabels = ["Calibrate", "SPCC on", "SPCC off"];

        /// <summary>
        /// A button's width held at its WIDEST label, so toggling state cannot move the buttons beside
        /// it. The viewer's toolbar makes the same reservation for Zoom and Enhance.
        /// </summary>
        private float ReservedButtonWidth(string[] labels, float gap)
        {
            var widest = 0f;
            foreach (var label in labels)
            {
                var w = MeasureText(label, FontSize);
                if (w > widest) { widest = w; }
            }

            return widest + gap * 2f;
        }

        /// <summary>
        /// What the Auto / Reset / calibration row needs, in its widest state. The ONE definition of
        /// it: the panel sizes itself from this and the body lays the row out from the same
        /// reservations, so the box can no longer be narrower than what it draws.
        /// </summary>
        private float WhiteBalanceButtonRowWidth(float gap)
            => MeasureText("Auto", FontSize) + gap * 2f
                + gap + ReservedButtonWidth(ResetLabels, gap)
                + gap + ReservedButtonWidth(SpccLabels, gap);

        /// <summary>
        /// The popover's body: the provenance line, the three sliders, Auto and Reset. Advances
        /// <paramref name="y"/> past what it drew, and captures the per-channel track rects the drag
        /// maps against.
        /// </summary>
        private void RenderWhiteBalanceBody(ViewerState state, ref float y, float x, float panelWidth)
        {
            // PROVENANCE, not the numbers: the numbers are on the sliders now. Method, survivor
            // count and white reference are the part a triple cannot carry, and the part that says
            // whether to trust it -- a 104-star photometric fit and a grey-world guess can both
            // read "R = 0.46".
            if (state.ColorCalibrationEnabled && _document?.ColorCalibrationSummary is { } summary)
            {
                DrawTextLine(ref y, x, Ellipsize(summary.Describe(), panelWidth, FontSize),
                    ViewerTheme.Palette.DimText);
            }

            // The sliders show the EFFECTIVE multiplier -- the calibration composed with the manual
            // fine-tune, which is exactly what the shader receives. They used to show the manual
            // triple alone, so a calibrated image sat at 1.00/1.00/1.00 on a panel whose whole job is
            // to report the white balance: a control reading neutral over an image that visibly is
            // not. Composed through the pipeline's own ComposeWhiteBalance so the panel cannot drift
            // from the render.
            var wb = EffectiveWhiteBalance(state);
            ReadOnlySpan<(string Label, float Value, RGBAColor32 Fill)> rows =
            [
                ("R", wb.R, RGBAColor32.FromFloat(0.85f, 0.32f, 0.32f, 1f)),
                ("G", wb.G, RGBAColor32.FromFloat(0.34f, 0.74f, 0.38f, 1f)),
                ("B", wb.B, RGBAColor32.FromFloat(0.38f, 0.56f, 0.92f, 1f)),
            ];

            var gap = 6f * DpiScale;
            var rowH = FontSize + gap;
            var labelW = MeasureText("R", FontSize) + gap;
            var valueW = MeasureText("0.00", FontSize) + gap;

            for (var ch = 0; ch < 3; ch++)
            {
                var (label, value, fill) = rows[ch];
                var rowY = y;
                DrawText(label, x, rowY, FontSize, ViewerTheme.Palette.BodyText);

                var trackX = x + labelW;
                var trackRight = x + panelWidth - valueW;
                var trackW = MathF.Max(0f, trackRight - trackX);
                if (trackW > 0f)
                {
                    var frac = WbValueToFrac(value);
                    // Generous full-row hit band; its X/Width drive the cursor-X -> multiplier mapping. The
                    // bar centres on the row; the handle is one font-line tall at the row top.
                    var hitBand = new RectF32(trackX, rowY - gap / 2f, trackW, FontSize + gap);
                    _wbTrackRects[ch] = hitBand;
                    DrawTrackSlider(trackX, trackW, rowY, FontSize, frac,
                        fill, hitBand, new WhiteBalanceSliderHit(ch), TrackChrome, DpiScale);
                }
                else
                {
                    _wbTrackRects[ch] = default;
                }

                DrawText(value.ToString("0.00"), trackRight, rowY, FontSize, ViewerTheme.Palette.DimText);
                y = rowY + rowH;
            }

            // Auto + Reset buttons row: both self-contained via OnClick (both mouse-down paths run
            // HitTestAndDispatch, and neither label is a ToolbarAction so each falls through to the
            // OnClick-already-ran path). Auto runs gray-world over the current frame and drops the result
            // into the sliders -- which then act as the fine-tune.
            var btnH = FontSize + gap;

            const string autoLabel = "Auto";
            var autoW = MeasureText(autoLabel, FontSize) + gap * 2f;
            FillRect(x, y, autoW, btnH, ToolbarButtonBg);
            DrawText(autoLabel, x + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(x, y, autoW, btnH, new HitResult.ButtonHit("AutoWhiteBalance"),
                _ =>
                {
                    if (_source is { } src && AutoWhiteBalance.GrayWorld(src) is { } grayWorld)
                    {
                        // Gray-world returns an ABSOLUTE answer, so it belongs on the effective
                        // value. Writing the manual slot directly would compose it on top of an
                        // active photometric calibration -- two absolute corrections multiplied,
                        // which is the same double-correction the SPCC path documents at length.
                        SetEffectiveWhiteBalance(state, grayWorld);
                        state.NeedsRedraw = true;
                    }
                });

            // "Reset" and not "Reset WB": with a calibration active this returns to the CALIBRATED
            // triple (manual identity), not to no-white-balance-at-all, and the sliders visibly jump
            // back to it. Switching the calibration off is the button beside this one.
            var resetLabel = state.ColorCalibrationEnabled && _document?.ColorCalibration is not null
                ? "Reset to calibrated"
                : "Reset WB";
            // Reserved against the widest state, like the SPCC button below: measuring the CURRENT
            // label let "Reset to calibrated" push the row past the panel's right edge the moment a
            // calibration landed, which is what it looked like -- a button hanging out of its box.
            var resetW = ReservedButtonWidth(ResetLabels, gap);
            var resetX = x + autoW + gap;
            FillRect(resetX, y, resetW, btnH, ToolbarButtonBg);
            DrawText(resetLabel, resetX + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(resetX, y, resetW, btnH, new HitResult.ButtonHit("ResetWhiteBalance"),
                _ =>
                {
                    state.ManualWhiteBalance = (1f, 1f, 1f);
                    // Drop the parked triple too: the user has just said explicitly that identity is
                    // what they want, so resurrecting a pre-calibration value later would override a
                    // more recent instruction with an older one.
                    state.ManualWhiteBalanceBeforeCalibration = null;
                    state.NeedsRedraw = true;
                });

            // The photometric calibration itself, moved off the toolbar and in here beside the sliders
            // it populates. It is one flag: "Calibrate" and "SPCC" were two buttons on the strip both
            // writing ColorCalibrationEnabled, so pressing either lit the other. Here it can also say
            // WHICH state it is in, where the strip only had room for a lit rectangle.
            //
            // Dim rather than absent when the frame has too few stars to fit against (the same >= 5
            // predicate the toolbar button used): a control that vanishes reads as a bug, one that is
            // dim reads as a precondition.
            var canCalibrate = _document?.Stars is { Count: >= 5 };
            var calibrated = _document?.ColorCalibration is not null;
            var spccLabel = !calibrated
                ? "Calibrate"
                : state.ColorCalibrationEnabled ? "SPCC on" : "SPCC off";
            // Measured against the widest state, not the current one, so toggling it cannot shuffle the
            // row sideways -- the same reservation the toolbar makes for Zoom and Enhance. It used to
            // name "SPCC off" alone, which is not the widest: "Calibrate" is longer in a proportional
            // face, so the reservation was a shade short in the very state a fresh document opens in.
            var spccW = ReservedButtonWidth(SpccLabels, gap);
            var spccX = resetX + resetW + gap;
            FillRect(spccX, y, spccW, btnH,
                calibrated && state.ColorCalibrationEnabled ? ToolbarButtonActiveBg : ToolbarButtonBg);
            DrawText(spccLabel, spccX + gap, y + gap / 2f, FontSize,
                canCalibrate ? ViewerTheme.Palette.BodyText : ViewerTheme.Palette.DimText);
            if (canCalibrate)
            {
                // NOT "SpccCalibrate" or any other ToolbarAction name: a ButtonHit whose label parses as
                // one is ALSO run by the toolbar action handler, so the toggle would fire twice and
                // cancel itself. The two buttons above avoid it by accident; this one says so.
                RegisterClickable(spccX, y, spccW, btnH, new HitResult.ButtonHit("ToggleColorCalibration"),
                    _ =>
                    {
                        // Toggle AND start, the same pair W has always run: on a document with no
                        // calibration yet a bare toggle is a no-op, and on one that has a solution a
                        // bare start is.
                        ViewerActions.SetColorCalibrationEnabled(state, !state.ColorCalibrationEnabled);
                        TryStartColorCalibration(state);
                        state.NeedsRedraw = true;
                    });
            }

            y += btnH + FontSize;
        }

        /// <summary>
        /// The auto calibration currently in force, or neutral. Gated on
        /// <see cref="ViewerState.ColorCalibrationEnabled"/> and not merely on the triple existing,
        /// so a switched-off calibration reports neutral -- which is what the render is doing.
        /// </summary>
        private (float R, float G, float B) ActiveAutoWhiteBalance(ViewerState state)
            => state.ColorCalibrationEnabled && _document?.ColorCalibration is { } auto
                ? auto
                : (1f, 1f, 1f);

        /// <summary>
        /// What the shader actually multiplies by: the auto calibration composed with the manual
        /// fine-tune. Routed through <see cref="StretchSolver.ComposeWhiteBalance"/> rather than
        /// multiplying here, so the panel and the pipeline cannot disagree about composition order
        /// or about what a neutral triple means.
        /// </summary>
        private (float R, float G, float B) EffectiveWhiteBalance(ViewerState state)
            => StretchSolver.ComposeWhiteBalance(ActiveAutoWhiteBalance(state), state.ManualWhiteBalance)
               ?? (1f, 1f, 1f);

        /// <summary>
        /// Writes <paramref name="target"/> as the EFFECTIVE white balance, by solving for the manual
        /// factor that lands there once composed over the active calibration.
        /// <para>
        /// This is what makes the sliders directly editable while the auto/manual split stays intact
        /// underneath -- and the split has to stay, because only the AUTO half scales the stretch
        /// stats (see StretchSolver): collapsing the two into one number would change what an
        /// unlinked stretch does with the calibration. The arithmetic itself is
        /// <see cref="StretchSolver.DecomposeWhiteBalance"/>, beside its forward counterpart, so the
        /// panel and the pipeline cannot disagree about composition order.
        /// </para>
        /// </summary>
        private void SetEffectiveWhiteBalance(ViewerState state, (float R, float G, float B) target)
            => state.ManualWhiteBalance =
                StretchSolver.DecomposeWhiteBalance(ActiveAutoWhiteBalance(state), target);

        private static float WbValueToFrac(float value)
        {
            var clamped = Math.Clamp(value, WbMin, WbMax);
            return MathF.Log(clamped / WbMin) / MathF.Log(WbMax / WbMin);
        }

        private static float WbFracToValue(float frac)
        {
            var f = Math.Clamp(frac, 0f, 1f);
            return WbMin * MathF.Exp(f * MathF.Log(WbMax / WbMin));
        }

        /// <summary>
        /// Begins a manual white-balance drag (press on a WB slider track). Public so both mouse-down paths
        /// (FitsViewer Program + GUI viewer tab) dispatch identically, mirroring <see cref="BeginScrubAt"/>.
        /// </summary>
        public void BeginWhiteBalanceDragAt(int channel, float px)
        {
            if (_state is not { } || (uint)channel >= 3u)
            {
                return;
            }

            _state.WhiteBalanceDragChannel = channel;
            UpdateWhiteBalanceDrag(px);
        }

        // Maps a cursor X onto a WB multiplier for the active drag channel against its captured track rect.
        private void UpdateWhiteBalanceDrag(float px)
        {
            if (_state is not { } state)
            {
                return;
            }
            var ch = state.WhiteBalanceDragChannel;
            if ((uint)ch >= 3u || _wbTrackRects[ch].Width <= 0f)
            {
                return;
            }

            var frac = TrackFrac(_wbTrackRects[ch], px);
            var value = WbFracToValue(frac);

            // The handle was dragged to an EFFECTIVE multiplier, because that is what the track
            // displays; the manual factor needed to land there is solved for. Setting the manual slot
            // to the dropped value instead would move the handle somewhere else entirely whenever a
            // calibration is active -- drop red on 0.60 over a 0.463 calibration and it would render
            // 0.28 and snap to that, so the slider would run away from the pointer.
            var wb = EffectiveWhiteBalance(state);
            SetEffectiveWhiteBalance(state, ch switch
            {
                0 => (value, wb.G, wb.B),
                1 => (wb.R, value, wb.B),
                _ => (wb.R, wb.G, value),
            });
            state.NeedsRedraw = true;
        }
        // -----------------------------------------------------------------------
        // The popover itself
        // -----------------------------------------------------------------------

        /// <summary>
        /// Escape closes the popover; every other key passes through, and so does Escape once the
        /// panel is no longer open. A claimant is asked whether it still applies, not told.
        /// </summary>
        private sealed class WhiteBalancePanelClaimant(Func<ViewerState?> state) : IKeyboardClaimant
        {
            public bool HandleKeyDown(InputKey key)
            {
                if (state() is not { WhiteBalancePanelOpen: true } open || key is not InputKey.Escape)
                {
                    return false;
                }

                open.WhiteBalancePanelOpen = false;
                open.NeedsRedraw = true;
                return true;
            }
        }

        private WhiteBalancePanelClaimant? _whiteBalanceClaimant;

        /// <summary>Neutral to a thousandth on every channel: the state in which the button is unlit.</summary>
        private static bool IsNeutralWhiteBalance((float R, float G, float B) wb)
            => MathF.Abs(wb.R - 1f) < 1e-3f && MathF.Abs(wb.G - 1f) < 1e-3f && MathF.Abs(wb.B - 1f) < 1e-3f;

        private void RenderWhiteBalancePanel(ViewerState state)
        {
            if (!state.WhiteBalancePanelOpen)
            {
                _wbTrackRects[0] = _wbTrackRects[1] = _wbTrackRects[2] = default;
                return;
            }

            // Nothing to hang it from: the button is not on this bar (a host with a narrower set) or
            // was not enabled this frame (a mono source). It closes rather than floating at a guess.
            if (!_toolbarButtonBounds.TryGetValue(ToolbarAction.WhiteBalance, out var anchor))
            {
                state.WhiteBalancePanelOpen = false;
                _wbTrackRects[0] = _wbTrackRects[1] = _wbTrackRects[2] = default;
                return;
            }

            // Claimed as it paints, like a dropdown, so the key handler's one claimant check routes
            // Escape here without a branch of its own.
            Ui.KeyboardClaimant = _whiteBalanceClaimant ??= new WhiteBalancePanelClaimant(() => _state);

            // The backdrop goes down FIRST so the panel's own regions, registered after it, win: a
            // press anywhere else closes the panel and is consumed, the button included, which is
            // what makes a second press on the button close what the first opened.
            RegisterClickable(0f, 0f, Width, Height, new HitResult.ButtonHit("WhiteBalanceBackdrop"),
                _ =>
                {
                    state.WhiteBalancePanelOpen = false;
                    state.NeedsRedraw = true;
                });

            var dpiScale = DpiScale;
            var pad = PanelPadding;
            var gap = 6f * dpiScale;

            // WIDE ENOUGH FOR THE BUTTON ROW, not merely the info panel's width. This was
            // BaseInfoPanelWidth alone, and the row is measured text: once a calibration landed and
            // Reset became "Reset to calibrated", the row was wider than the box and the last button
            // hung out over the image. The row reserves its widest labels (see the body), so this is a
            // constant per DPI and the panel does not resize as the buttons are toggled.
            var w = MathF.Max(BaseInfoPanelWidth * dpiScale, WhiteBalanceButtonRowWidth(gap) + pad * 2f);
            var x = OverlayPlacement.ClampX(anchor.X, w, Width);
            var y = anchor.Y + anchor.Height;

            // Sized from the same measures the body draws with, so the box and its contents cannot
            // disagree: the provenance line when there is one, three slider rows, the button row.
            var rowH = FontSize + gap;
            var btnH = FontSize + gap;
            var provenanceH = state.ColorCalibrationEnabled && _document?.ColorCalibrationSummary is not null
                ? TextLineAdvance
                : 0f;
            var h = pad + provenanceH + (3f * rowH) + btnH + pad;

            FillRect(x - 1f, y - 1f, w + 2f, h + 2f, ViewerTheme.Palette.SeparatorStrong);
            FillRect(x, y, w, h, ViewerTheme.InfoPanelBg);
            // The panel's blank area swallows a press rather than letting it reach the backdrop.
            RegisterClickable(x, y, w, h, new HitResult.ButtonHit("WhiteBalancePanelBackground"), _ => { });

            var bodyY = y + pad;
            RenderWhiteBalanceBody(state, ref bodyY, x + pad, w - (pad * 2f));
        }
    }
}
