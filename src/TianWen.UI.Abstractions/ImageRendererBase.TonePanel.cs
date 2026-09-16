using System;
using System.Collections.Immutable;
using DIR.Lib;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The tone popover: the curves boost, the highlight soft clip, and the display HDR that is
/// neither of them, under the toolbar's <see cref="ToolbarAction.Tone"/> button.
/// </summary>
/// <remarks>
/// <para><b>Two toolbar buttons until 8.1, one of them misnamed.</b> "Boost" and "HDR" sat side by
/// side doing related things to the same pixels, and the second was the one label in the bar that
/// promised something the viewer does not do: it is a soft knee applied after the MTF and inside
/// [0, 1], so it never asks the panel for a nit above SDR white. Folded together here for the
/// reason Calibrate and SPCC folded into the white-balance popover -- one control, and room to say
/// how its parts relate, which a row of buttons has nowhere to put.</para>
/// <para><b>The last block is the point of the fold.</b> Showing light ABOVE SDR white is a
/// different thing from compressing highlights into it, and someone who comes looking for HDR
/// arrives at exactly this control. So the panel names display HDR, greys it, and says why --
/// drawn rather than hidden, because a menu whose entries come and go teaches nothing about how to
/// make them available. See docs/plans/hdr-display.md.</para>
/// <para><b>A menu in everything but its contents, and the engine owns every part of that now.</b>
/// <see cref="Layout.Builder.Popover"/> is the whole declaration: the backdrop that closes it on a
/// press anywhere else and consumes that press (the button included, which is what makes a second
/// press close what the first opened), the placement under the button, the Escape claim, and the
/// pointer ownership that stops chrome underneath lighting up. Those were five separate obligations
/// written out here, and forgetting one was silent.</para>
/// <para><b>It is ONE arranged tree, and the box is the engine's measurement of it.</b> The
/// white-balance popover beside it predates that rule: it advances a <c>y</c> by hand, sums its own
/// box height from the same constants its body draws with, and takes its width from a union of
/// every label a button can carry -- three arithmetic chains that agree only while someone keeps
/// them agreeing, and a test of it has to redo the arithmetic a fourth time to say where anything
/// landed. The first draft of THIS panel did the same and was already wrong: the soft-clip heading
/// ran past the right edge of its own box, because the width union listed the strings someone
/// remembered. Here the engine measures, the box is that measurement, every region binds to its
/// arranged rect, and a test reads arranged nodes. The rest of the viewer chrome is on the same
/// road: <c>docs/plans/viewer-layout-engine.md</c>.</para>
/// </remarks>
partial class ImageRendererBase<TSurface>
{
    /// <summary>The label of the block that names the display HDR this panel does not do.</summary>
    private const string DisplayHdrLabel = "HDR display (scRGB)";

    /// <summary>
    /// Why that block is dim. A CONSTANT, and deliberately about the VIEWER rather than about the
    /// machine: nothing here asks the GPU what colour spaces its surface offers, so a claim about
    /// what this GPU can do would be the same kind of guess the old "HDR" label was. It becomes the
    /// renderer's own answer when surface-capability detection lands (hdr-display.md, P2).
    /// </summary>
    private const string DisplayHdrReason = "not yet; the viewer always presents SDR";

    private const string SoftClipHeading = "Soft clip: highlights bend instead of clipping";
    private const string BoostNeedsStarsReason = "needs detected stars";
    private const string CurveModeNeedsBoostReason = "no effect at zero boost";
    private const string KneeNeedsAmountReason = "no effect at zero amount";

    /// <summary>The widest label the curve-mode button carries, so it cannot resize under the
    /// pointer. Stated as a <c>widthSample</c> ON the node, which is the engine's own way of saying
    /// it -- not a union measured here and turned into a number.</summary>
    private const string CurveModeWidthSample = "Curve: spline";

    /// <summary>Likewise for the dial columns: the widest label and the widest value.</summary>
    private const string ToneLabelWidthSample = "Amount";
    private const string ToneValueWidthSample = "100%";

    // Slider travel. The boost and the soft-clip amount run from zero to the top of their own
    // preset ladders, so the keyboard ladders (B and H) land ON the track rather than past its end.
    // The knee's travel is wider than its ladder uses on purpose: the presets walk it down from
    // 0.85 to 0.70 as the amount rises, and a track stopping at 0.70 would put the hardest preset
    // against the end stop with nowhere to go.
    private const float BoostSliderMax = 1.5f;
    private const float SoftClipAmountMax = 2.0f;
    private const float SoftClipKneeMin = 0.50f;
    private const float SoftClipKneeMax = 0.95f;

    /// <summary>
    /// The least track worth dragging, in design units, as a Star MINIMUM rather than a fixed
    /// width: it takes part in the measure that decides the panel's width and then gets out of the
    /// way, so inside a wider box the track takes the slack and the labels either side stay put.
    /// </summary>
    private const float ToneTrackMinWidth = 120f;

    /// <summary>Inter-row and inter-column gap, design units.</summary>
    private const float ToneGap = 6f;

    /// <summary>The boost dial's accent.</summary>
    private static readonly RGBAColor32 ToneBoostFill = RGBAColor32.FromFloat(0.62f, 0.55f, 0.85f, 1f);

    /// <summary>The soft clip's two dials share one accent: they are one control in two parts.</summary>
    private static readonly RGBAColor32 ToneSoftClipFill = RGBAColor32.FromFloat(0.85f, 0.66f, 0.35f, 1f);

    // The three dials, one state each, living across frames because the tree does not: a Content.Slider
    // leaf carries a REFERENCE to caller-owned state (the precedent a text field sets), and the drag the
    // engine arms on a press holds that same reference until the release. Re-seeded from ViewerState at
    // the top of every build, so the handle reports what the render is doing and the ladders (B, H) move
    // it without going through here.
    private readonly SliderState _toneBoostSlider = new() { Min = 0f, Max = BoostSliderMax };
    private readonly SliderState _toneAmountSlider = new() { Min = 0f, Max = SoftClipAmountMax };
    private readonly SliderState _toneKneeSlider = new() { Min = SoftClipKneeMin, Max = SoftClipKneeMax };

    /// <summary>Test seam: the boost dial, so a test can find its arranged leaf by identity.</summary>
    internal SliderState ToneBoostSliderState => _toneBoostSlider;

    /// <summary>Test seam: the soft-clip amount dial.</summary>
    internal SliderState ToneAmountSliderState => _toneAmountSlider;

    /// <summary>Test seam: the soft-clip knee dial.</summary>
    internal SliderState ToneKneeSliderState => _toneKneeSlider;

    // EMPTY, never default: a default ImmutableArray throws on every read, so a closed panel would
    // hand a caller a different KIND of empty from an open one with nothing in it.
    private ImmutableArray<Layout.ArrangedNode<float>> _toneArranged = [];

    /// <summary>
    /// Test seam: the panel as the engine arranged it. A test asks this where it would otherwise
    /// scan the window for regions and redo the panel's own arithmetic to interpret what it found.
    /// </summary>
    internal ImmutableArray<Layout.ArrangedNode<float>> ToneLayoutForTest => _toneArranged;

    private void ClearToneState() => _toneArranged = [];

    /// <summary>
    /// One dial's row: label, track, value. The track is a <see cref="Layout.Content.Slider"/> leaf,
    /// so the engine draws it, registers it and arms its drag; nothing here maps a cursor X.
    /// </summary>
    /// <remarks>
    /// A dim dial still registers, with no press bound, so it swallows the press rather than letting
    /// it reach the backdrop and close the panel being read. That is the same answer the old
    /// unregistered band got by accident (the panel body swallowed it) and it is now stated.
    /// </remarks>
    private Layout.Node ToneDialRow(string label, SliderState dial, string valueText, RGBAColor32 accent)
        => Layout.Builder.HStack(
                Layout.Builder.Text(label, BaseFontSize,
                    dial.Enabled ? ViewerTheme.Palette.BodyText : ViewerTheme.Palette.DimText,
                    widthSample: ToneLabelWidthSample),
                Layout.Builder.Slider(dial, accent, TrackChrome).WStar(1f, ToneTrackMinWidth).HStar(),
                Layout.Builder.Text(valueText, BaseFontSize, ViewerTheme.Palette.DimText,
                    hAlign: TextAlign.Far, widthSample: ToneValueWidthSample))
            .WithGap(ToneGap)
            .CrossCenter()
            .RowH(BaseFontSize + ToneGap);

    /// <summary>A reason under a dim control, indented past the label column to the control it
    /// explains. The indent is a spacer sized by the same sample the label column uses, so the two
    /// cannot drift.</summary>
    private static Layout.Node ToneReasonRow(string reason, float fontSize)
        => Layout.Builder.HStack(
                Layout.Builder.Text(string.Empty, fontSize, widthSample: ToneLabelWidthSample),
                Layout.Builder.Text(reason, fontSize, ViewerTheme.Palette.DimText))
            .WithGap(ToneGap)
            .RowH(fontSize + (ToneGap * 0.5f));

    private static Layout.Node ToneSeparator()
        => Layout.Builder.Box(0f, 1f, ViewerTheme.Palette.SeparatorStrong).RowH(1f);

    /// <summary>
    /// The whole popover as one tree. Nothing here is positioned and nothing is measured; the
    /// engine is told what the content is and does both.
    /// </summary>
    private Layout.Node BuildToneTree(ViewerState state)
    {
        var small = BaseFontSize * 0.85f;
        var rowH = BaseFontSize + ToneGap;

        // The boost keeps the precondition its own toolbar button carried. Stated, rather than left
        // as a control that quietly does nothing.
        var boostEnabled = _document?.Stars is { Count: > 0 };

        // The curve mode reaches the pixels ONLY through the boost, so at zero boost it names a
        // difference the picture cannot show.
        var curveModeLive = boostEnabled && state.CurvesBoost > 0f;
        var curveLabel = state.CurvesMode == 1 ? "Curve: spline" : "Curve: boost";

        // Seeded, not rebuilt: the three states outlive the tree, so what is written here is the
        // value the handle draws at and the callback a drag will reach. Each writes the SAME field
        // its keyboard ladder does, so a dial dragged off a rung simply is not on one.
        _toneBoostSlider.Value = state.CurvesBoost;
        _toneBoostSlider.Enabled = boostEnabled;
        _toneBoostSlider.OnChanged = v =>
        {
            state.CurvesBoost = v;
            state.NeedsRedraw = true;
        };

        _toneAmountSlider.Value = state.HdrAmount;
        _toneAmountSlider.OnChanged = v =>
        {
            state.HdrAmount = v;
            state.NeedsRedraw = true;
        };

        _toneKneeSlider.Value = state.HdrKnee;
        _toneKneeSlider.Enabled = state.HdrAmount > 0f;
        _toneKneeSlider.OnChanged = v =>
        {
            state.HdrKnee = v;
            state.NeedsRedraw = true;
        };

        var rows = ImmutableArray.CreateBuilder<Layout.Node>();

        rows.Add(ToneDialRow("Boost", _toneBoostSlider, UiFormat.Percent0(state.CurvesBoost), ToneBoostFill));
        if (!boostEnabled)
        {
            rows.Add(ToneReasonRow(BoostNeedsStarsReason, small));
        }

        rows.Add(Layout.Builder.HStack(
                Layout.Builder.Text(string.Empty, BaseFontSize, widthSample: ToneLabelWidthSample),
                Layout.Builder.Text(curveLabel, BaseFontSize,
                        curveModeLive ? ViewerTheme.Palette.BodyText : ViewerTheme.Palette.DimText,
                        hAlign: TextAlign.Center, widthSample: CurveModeWidthSample)
                    .PadX(ToneGap)
                    .Bg(ToolbarButtonBg)
                    .Clickable(new HitResult.ButtonHit("ToneCurveMode"),
                        _ =>
                        {
                            if (curveModeLive)
                            {
                                ViewerActions.CycleCurvesMode(state);
                            }
                        },
                        curveModeLive ? CursorKind.Pointer : null),
                curveModeLive
                    ? Layout.Builder.Spacer().WStar()
                    : Layout.Builder.Text(CurveModeNeedsBoostReason, small, ViewerTheme.Palette.DimText))
            .WithGap(ToneGap)
            .CrossCenter()
            .RowH(rowH));

        rows.Add(ToneSeparator());

        // The heading carries what the control DOES, because the two dials below it are the part
        // that used to read as "1.0 / 0.80" with the meaning nowhere on screen.
        rows.Add(Layout.Builder.Text(SoftClipHeading, small, ViewerTheme.Palette.DimText).RowH(rowH));
        rows.Add(ToneDialRow("Amount", _toneAmountSlider, state.HdrAmount.ToString("0.00"), ToneSoftClipFill));
        rows.Add(ToneDialRow("Knee", _toneKneeSlider, state.HdrKnee.ToString("0.00"), ToneSoftClipFill));
        if (state.HdrAmount <= 0f)
        {
            rows.Add(ToneReasonRow(KneeNeedsAmountReason, small));
        }

        rows.Add(ToneSeparator());

        // The thing this control is NOT, in the one place a user looking for HDR will arrive. The
        // wrapper is clickable and inert: it swallows a press that would otherwise reach the
        // backdrop and close the panel being read.
        rows.Add(Layout.Builder.VStack(
                Layout.Builder.Text(DisplayHdrLabel, BaseFontSize, ViewerTheme.Palette.DimText).RowH(rowH),
                Layout.Builder.Text(DisplayHdrReason, small, ViewerTheme.Palette.DimText)
                    .RowH(small + ToneGap))
            .Clickable(new HitResult.ButtonHit("ToneDisplayHdr"), _ => { })
            .WStar());

        // Outer: a one-unit border ring around the padded body, so the frame is part of the tree
        // rather than two rectangles drawn before it. The body swallows a press anywhere on it.
        return Layout.Builder.VStack(
                Layout.Builder.VStack(rows.ToImmutable().AsSpan())
                    .WithGap(ToneGap * 0.5f)
                    .Pad(BasePanelPadding)
                    .Bg(ViewerTheme.InfoPanelBg)
                    .Clickable(new HitResult.ButtonHit("TonePanelBackground"), _ => { })
                    .WStar())
            .Pad(1f)
            .Bg(ViewerTheme.Palette.SeparatorStrong)
            .WStar();
    }

    private void RenderTonePanel(ViewerState state)
    {
        // The engine paints nothing for a closed popover, so this is not the open check -- it is the
        // measure this frame does not have to pay for. A closed popover is still MEASURED and ARRANGED
        // (the flag is inert everywhere but the paint), and measuring a dozen rows of text on the
        // render thread for a panel nobody can see is exactly the non-drawing work a gated render is
        // supposed to skip.
        if (!state.TonePopover.IsOpen)
        {
            ClearToneState();
            return;
        }

        // Nothing to hang it from: the button is not on this bar (a host with a narrower set) or was
        // not enabled this frame. It closes rather than floating at a guess.
        //
        // Read back from the regions the paint REGISTERED rather than from a rect cache kept beside it.
        // The cache this used to ask is removed by this same branch, which converts the toolbar rect
        // caches to read-backs; the tone panel arrived on a different branch and added a caller of it, so
        // the two merge textually clean and do not compile. Nothing flags that but a build.
        if (!TryGetPaintedToolbarRect(ToolbarAction.Tone, out var anchor))
        {
            state.TonePopover.Close();
            ClearToneState();
            return;
        }

        var ctx = MeasureContext();

        // THE BOX IS THE MEASUREMENT, and that is the whole point of the tree. The first draft of
        // this panel summed its own height from the constants its body drew with and took its width
        // from a hand-kept union of every string it might show -- and the width was already wrong,
        // because the union listed the strings someone remembered rather than the ones the tree
        // contains. The engine measures what is actually in it, prose and track minimum alike; the
        // info panel's width is only a FLOOR, and it is stated as the Star minimum the engine already
        // honours rather than as a max() over a separate measure pass.
        var content = BuildToneTree(state).WStar(1f, BaseInfoPanelWidth);

        // Under the button and on screen, both the engine's: a side means just OUTSIDE that edge of
        // the ANCHOR while the clamp still targets the rect the popover floats in, which is the whole
        // window here. That pairing is what OverlayPlacement.ClampX was a private half of.
        var tree = Layout.Builder.Popover(anchor, content, state.TonePopover);

        _toneArranged = ArrangeLayout(tree, new RectF32(0f, 0f, Width, Height), ctx);
        PaintLayout(_toneArranged, ctx);
    }
}
