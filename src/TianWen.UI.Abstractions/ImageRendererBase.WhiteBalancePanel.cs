using System;
using System.Collections.Immutable;
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
    /// <para><b>A menu in everything but its contents, and the engine owns every part of that.</b>
    /// <see cref="Layout.Builder.Popover"/> is the whole declaration: the backdrop that dismisses and
    /// consumes the press (the button included, which is what makes a second press close what the
    /// first opened), the placement under the button with its on-screen clamp, the Escape claim, and
    /// the pointer ownership. Those were five obligations written out here, and forgetting one of
    /// them was silent.</para>
    /// <para><b>It is ONE arranged tree, and the box is the engine's measurement of it.</b> This
    /// panel is what the tone popover's own doc comment used to hold up as the counter-example: it
    /// advanced a <c>y</c> by hand, summed its own box height from the constants its body drew with,
    /// and took its width from a union of every label a button can carry -- three arithmetic chains
    /// that agreed only while someone kept them agreeing. The reservations are still here and are
    /// <see cref="Layout.Content.Text.WidthSample"/>s now, which is the engine's own way of saying
    /// "hold this at its widest label": stated on the node that draws it, so there is nothing to keep
    /// in step.</para>
    /// </remarks>
    partial class ImageRendererBase<TSurface>
    {
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

        /// <summary>The widest label the Reset button carries, held as the node's own width sample so
        /// the row cannot shuffle sideways when the label changes. It used to be a union measured over
        /// an array beside the paint, and then turned into a number.</summary>
        private const string ResetWidthSample = "Reset to calibrated";

        /// <summary>
        /// Likewise for the calibration button, whose in-flight label is the widest of its four.
        /// Leaving it out would put the button back to changing width under the user -- while a fit is
        /// running, which is the worst moment for it.
        /// </summary>
        private const string SpccWidthSample = "Calibrating...";

        /// <summary>The label column, as wide as its widest letter, and the value column at its widest
        /// reading. Stated as samples for the same reason the buttons are.</summary>
        /// <remarks>G, a round capital, is the widest of the three in the faces this ships with. The
        /// column was sized to R, so the G alone did not fit and was drawn as a lone ellipsis
        /// (2026-09-18); the engine no longer cuts a single character at all (DIR.Lib's
        /// <c>TextFit.IsSingleGrapheme</c>), but a column narrower than its label would still overhang.</remarks>
        private const string WbLabelWidthSample = "G";
        private const string WbValueWidthSample = "0.00";

        /// <summary>Inter-row and inter-column gap, design units. The row height is a font line plus
        /// one of these, which is what the hand-laid-out version spelled out per row.</summary>
        private const float WbGap = 6f;

        // The three channel dials, one state each, living across frames because the tree does not: a
        // Content.Slider leaf carries a REFERENCE to caller-owned state, and the drag the engine arms
        // on a press holds that same reference until the release. Seeded from ViewerState at the top
        // of every build.
        //
        // What each carries is the track FRACTION, not the multiplier: the mapping is logarithmic
        // (neutral at the midpoint) while a SliderState's range is linear, so the log lives in the two
        // conversions either side of it, exactly where it lived when a drag mapped a cursor X by hand.
        private readonly SliderState[] _wbSliders = [new SliderState(), new SliderState(), new SliderState()];

        /// <summary>Test seam: one channel's dial, so a test can find its arranged leaf by identity.</summary>
        internal SliderState WhiteBalanceSliderState(int channel) => _wbSliders[channel];

        private static readonly RGBAColor32[] WbChannelFill =
        [
            RGBAColor32.FromFloat(0.85f, 0.32f, 0.32f, 1f),
            RGBAColor32.FromFloat(0.34f, 0.74f, 0.38f, 1f),
            RGBAColor32.FromFloat(0.38f, 0.56f, 0.92f, 1f),
        ];

        private static readonly string[] WbChannelLabels = ["R", "G", "B"];

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

        /// <summary>Neutral to a thousandth on every channel: the state in which the button is unlit.</summary>
        private static bool IsNeutralWhiteBalance((float R, float G, float B) wb)
            => MathF.Abs(wb.R - 1f) < 1e-3f && MathF.Abs(wb.G - 1f) < 1e-3f && MathF.Abs(wb.B - 1f) < 1e-3f;

        /// <summary>
        /// One channel's row: letter, track, value. The track is a <see cref="Layout.Content.Slider"/>
        /// leaf, so the engine draws it, registers it and arms its drag from the rect it painted --
        /// draw == drag by construction, where this used to read its band back out of the region list.
        /// </summary>
        private Layout.Node WhiteBalanceRow(int channel, float value)
            => Layout.Builder.HStack(
                    Layout.Builder.Text(WbChannelLabels[channel], BaseFontSize, ViewerTheme.Palette.BodyText,
                        widthSample: WbLabelWidthSample),
                    Layout.Builder.Slider(_wbSliders[channel], WbChannelFill[channel], TrackChrome).HStar(),
                    Layout.Builder.Text(value.ToString("0.00"), BaseFontSize, ViewerTheme.Palette.DimText,
                        hAlign: TextAlign.Far, widthSample: WbValueWidthSample))
                .WithGap(WbGap)
                .CrossCenter()
                .RowH(BaseFontSize + WbGap);

        /// <summary>
        /// One button in a panel's action row, held at <paramref name="widthSample"/> so toggling its
        /// label cannot move its neighbours.
        /// </summary>
        /// <remarks>
        /// Shared with the wavelet block rather than copied into it: the rule below -- register even
        /// when the button cannot act -- is the kind this codebase has had to walk back from two
        /// independent copies of more than once.
        /// </remarks>
        /// <remarks>
        /// Registered even when it cannot act, so a press lands on the button and stops there. Reset
        /// and the calibration button both used to register nothing while dim, which let the press
        /// reach the backdrop behind them and CLOSE the panel -- so pressing a busy or unavailable
        /// control made the whole popover vanish, which reads as a crash rather than as a refusal.
        /// </remarks>
        private Layout.Node PanelButton(string label, string hit, bool enabled,
            Action onPress, string? widthSample = null, RGBAColor32? background = null)
        {
            var fill = background ?? ToolbarButtonBg;
            var button = Layout.Builder.Text(label, BaseFontSize,
                    enabled ? ViewerTheme.Palette.BodyText : ViewerTheme.Palette.DimText,
                    hAlign: TextAlign.Center, widthSample: widthSample)
                .PadX(WbGap)
                // The row's height, not the label's: a text node measures its own glyphs, so "Calibrate"
                // (ascenders) stood taller than "Auto" beside it and the centred row read as three sizes.
                .HStar()
                .Bg(fill)
                .Clickable(new HitResult.ButtonHit(hit),
                    _ =>
                    {
                        if (enabled)
                        {
                            onPress();
                        }
                    },
                    enabled ? CursorKind.Pointer : null);

            // Lit under the pointer only when a press would do something: a dim button that lit up would
            // promise the action its dimness is refusing.
            return enabled ? button.BgHover(GuiTheme.Hover(fill)) : button;
        }

        /// <summary>
        /// The whole popover as one tree. Nothing here is positioned and nothing is measured; the
        /// engine is told what the content is and does both.
        /// </summary>
        private Layout.Node BuildWhiteBalanceTree(ViewerState state)
        {
            var rows = ImmutableArray.CreateBuilder<Layout.Node>();

            // PROVENANCE, not the numbers: the numbers are on the sliders. Method, survivor count and
            // white reference are the part a triple cannot carry, and the part that says whether to trust
            // it -- a 104-star photometric fit and a grey-world guess can both read "R = 0.46".
            //
            // Short lines at their own width, never trimmed. This used to be Describe(), which repeats
            // the triple, on ONE line held to no width at all so the painter cut it to the box: "SPCC
            // R=0.671 G=1.000 B=1.605 -- 22 ..." dropped the star count's unit and the whole white
            // reference, which is the half of the line the sliders cannot say (2026-09-18). Each line
            // now reports its width like any other row, so the box is at least as wide as the widest;
            // the button row, held at its widest labels, is wider than any of them today, so the box
            // does not move when a calibration lands.
            if (state.ColorCalibrationEnabled && _document?.ColorCalibrationSummary is { } summary)
            {
                foreach (var line in summary.ProvenanceLines())
                {
                    rows.Add(Layout.Builder.Text(line, BaseFontSize, ViewerTheme.Palette.DimText)
                        .RowH(TextLineAdvance));
                }
            }

            // The sliders show the EFFECTIVE multiplier -- the calibration composed with the manual
            // fine-tune, which is exactly what the shader receives. They used to show the manual
            // triple alone, so a calibrated image sat at 1.00/1.00/1.00 on a panel whose whole job is
            // to report the white balance: a control reading neutral over an image that visibly is
            // not. Composed through the pipeline's own ComposeWhiteBalance so the panel cannot drift
            // from the render.
            var wb = EffectiveWhiteBalance(state);
            ReadOnlySpan<float> values = [wb.R, wb.G, wb.B];

            for (var ch = 0; ch < 3; ch++)
            {
                var channel = ch;
                _wbSliders[ch].Value = WbValueToFrac(values[ch]);
                _wbSliders[ch].OnChanged = frac =>
                {
                    // The handle was dragged to an EFFECTIVE multiplier, because that is what the
                    // track displays; the manual factor needed to land there is solved for. Setting
                    // the manual slot to the dropped value instead would move the handle somewhere
                    // else entirely whenever a calibration is active -- drop red on 0.60 over a 0.463
                    // calibration and it would render 0.28 and snap to that, so the slider would run
                    // away from the pointer.
                    var current = EffectiveWhiteBalance(state);
                    var value = WbFracToValue(frac);
                    SetEffectiveWhiteBalance(state, channel switch
                    {
                        0 => (value, current.G, current.B),
                        1 => (current.R, value, current.B),
                        _ => (current.R, current.G, value),
                    });
                    state.NeedsRedraw = true;
                };

                rows.Add(WhiteBalanceRow(ch, values[ch]));
            }

            // Auto runs gray-world over the current frame and drops the result into the sliders --
            // which then act as the fine-tune.
            var auto = PanelButton("Auto", "AutoWhiteBalance", enabled: true, () =>
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
            //
            // ACTIVE ONLY WHEN THERE IS SOMETHING TO RESET, which is the MANUAL triple being off
            // identity -- not the sliders being off 1.00. The two differ exactly when a calibration is
            // active: the sliders then read the EFFECTIVE value (1.44/1.00/1.23 on the Sag Triplet)
            // while the manual layer this button clears is identity, because
            // SetColorCalibrationEnabled sets it to identity when it switches the calibration on. So
            // straight after calibrating, this button did nothing at all while looking like it would
            // undo what you were looking at. Dim rather than absent, the same reading the calibration
            // button beside it takes: a control that vanishes reads as a bug, one that is dim reads as
            // a precondition.
            var resetLabel = state.ColorCalibrationEnabled && _document?.ColorCalibration is not null
                ? "Reset to calibrated"
                : "Reset WB";
            var canReset = state.ManualWhiteBalance != (1f, 1f, 1f);
            var reset = PanelButton(resetLabel, "ResetWhiteBalance", canReset, () =>
            {
                state.ManualWhiteBalance = (1f, 1f, 1f);
                // Drop the parked triple too: the user has just said explicitly that identity is
                // what they want, so resurrecting a pre-calibration value later would override a
                // more recent instruction with an older one.
                state.ManualWhiteBalanceBeforeCalibration = null;
                state.NeedsRedraw = true;
            }, ResetWidthSample);

            // The photometric calibration itself, moved off the toolbar and in here beside the sliders
            // it populates. It is one flag: "Calibrate" and "SPCC" were two buttons on the strip both
            // writing ColorCalibrationEnabled, so pressing either lit the other. Here it can also say
            // WHICH state it is in, where the strip only had room for a lit rectangle.
            //
            // Dim rather than absent when the frame has too few stars to fit against (the same >= 5
            // predicate the toolbar button used). And it SAYS SO WHILE IT IS RUNNING: a photometric
            // fit is a catalogue init plus a match against a few thousand stars -- seconds, not a
            // frame -- and the button used to go on reading "Calibrate" throughout, so the one control
            // the user had just pressed looked like it had ignored them. The status bar said
            // "Calibrating color..." all along; the button, which is where they were looking, did not.
            var inFlight = _document?.ColorCalibrationInFlight ?? false;
            var canCalibrate = !inFlight && _document?.Stars is { Count: >= 5 };
            var calibrated = _document?.ColorCalibration is not null;
            var spccLabel = inFlight
                ? "Calibrating..."
                : !calibrated
                    ? "Calibrate"
                    : state.ColorCalibrationEnabled ? "SPCC on" : "SPCC off";

            // NOT "SpccCalibrate" or any other ToolbarAction name: a ButtonHit whose label parses as
            // one is ALSO run by the toolbar action handler, so the toggle would fire twice and
            // cancel itself. The two buttons above avoid it by accident; this one says so.
            var spcc = PanelButton(spccLabel, "ToggleColorCalibration", canCalibrate, () =>
            {
                // The action follows the LABEL. "Calibrate" fits; "SPCC on"/"SPCC off" toggles the
                // fit this frame already has. It used to do both unconditionally, which was
                // harmless while every frame was auto-fitted on arrival and is not now: on a
                // freshly opened target the flag is still down from the previous set, so a press
                // on a button reading "Calibrate" toggled the flag OFF and relied on the fit
                // landing to turn it back on -- leaving it off whenever the fit declined.
                if (calibrated)
                {
                    ViewerActions.SetColorCalibrationEnabled(state, !state.ColorCalibrationEnabled);
                }
                else
                {
                    // Enabled FIRST, so the render that follows shows the fit the moment it lands
                    // rather than waiting for a second press.
                    ViewerActions.SetColorCalibrationEnabled(state, true);
                    TryStartColorCalibration(state);
                }

                state.NeedsRedraw = true;
            }, SpccWidthSample,
                calibrated && state.ColorCalibrationEnabled ? ToolbarButtonActiveBg : ToolbarButtonBg);

            // The trailing spacer is what keeps the three buttons at their reserved widths instead of
            // sharing the row: it takes the slack a wider box leaves over.
            rows.Add(Layout.Builder.HStack(auto, reset, spcc, Layout.Builder.Spacer().WStar())
                .WithGap(WbGap)
                .CrossCenter()
                .RowH(BaseFontSize + WbGap));

            // Outer: a one-unit border ring around the padded body, so the frame is part of the tree
            // rather than two rectangles drawn before it. The body swallows a press anywhere on it.
            return Layout.Builder.VStack(
                    Layout.Builder.VStack(rows.ToImmutable().AsSpan())
                        .Pad(BasePanelPadding)
                        .Bg(ViewerTheme.InfoPanelBg)
                        .Clickable(new HitResult.ButtonHit("WhiteBalancePanelBackground"), _ => { })
                        .WStar())
                .Pad(1f)
                .Bg(ViewerTheme.Palette.SeparatorStrong)
                .WStar();
        }

        private void RenderWhiteBalancePanel(ViewerState state)
        {
            // The engine paints nothing for a closed popover, so this is not the open check -- it is
            // the measure this frame does not have to pay for. A closed popover is still measured and
            // arranged, and measuring a panel nobody can see is the non-drawing work a gated render is
            // supposed to skip.
            if (!state.WhiteBalancePopover.IsOpen)
            {
                return;
            }

            // Nothing to hang it from: the button is not on this bar (a host with a narrower set) or
            // was not enabled this frame (a mono source). It closes rather than floating at a guess.
            if (!TryGetPaintedToolbarRect(ToolbarAction.WhiteBalance, out var anchor))
            {
                state.WhiteBalancePopover.Close();
                return;
            }

            var ctx = MeasureContext();

            // WIDE ENOUGH FOR THE BUTTON ROW, not merely the info panel's width. That was
            // BaseInfoPanelWidth alone while the button row is measured text, so once a calibration
            // landed and Reset became "Reset to calibrated" the row was wider than the box and the
            // last button hung out over the image. The engine measures what is actually in the tree,
            // so the info panel's width is only a FLOOR -- stated as the Star minimum the engine
            // already honours, rather than as a max() over a separately summed row width.
            var content = BuildWhiteBalanceTree(state).WStar(1f, BaseInfoPanelWidth);

            // Under the button and on screen, both the engine's: with an anchor, a side means just
            // OUTSIDE that edge of the ANCHOR while the clamp still targets the rect the popover
            // floats in, which is the whole window here.
            var tree = Layout.Builder.Popover(anchor, content, state.WhiteBalancePopover);
            PaintLayout(ArrangeLayout(tree, new RectF32(0f, 0f, Width, Height), ctx), ctx);
        }
    }
}
