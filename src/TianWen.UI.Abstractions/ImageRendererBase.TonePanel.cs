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
/// <para><b>A menu in everything but its contents</b>, like the white-balance popover: painted with
/// the dropdowns so its regions win z-order; a full-window backdrop under it closes it on a press
/// anywhere else and consumes that press, the button included, which is what makes a second press
/// close what the first opened; it claims the keyboard as it paints so Escape routes through the
/// one claimant check rather than a second branch; and while it is open it owns the pointer
/// (<see cref="ViewerState.OverlayOwnsPointer"/>). Closed, it leaves no slider band behind.</para>
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

    private const string ToneBoostFillKey = "toneBoost";
    private const string ToneAmountFillKey = "toneAmount";
    private const string ToneKneeFillKey = "toneKnee";

    // Track rects captured as the panel paints, so a drag maps a cursor X against the same rect the
    // engine arranged. Default/empty when the panel is closed or a dial drew dim, which is what
    // stops a drag being answered by a control that is not on screen.
    private readonly RectF32[] _toneTrackRects = new RectF32[3];

    /// <summary>
    /// Escape closes the popover; every other key passes through, and so does Escape once the panel
    /// is no longer open. A claimant is asked whether it still applies, not told.
    /// </summary>
    private sealed class TonePanelClaimant(Func<ViewerState?> state) : IKeyboardClaimant
    {
        public bool HandleKeyDown(InputKey key)
        {
            if (state() is not { TonePanelOpen: true } open || key is not InputKey.Escape)
            {
                return false;
            }

            open.TonePanelOpen = false;
            open.NeedsRedraw = true;
            return true;
        }
    }

    private TonePanelClaimant? _toneClaimant;

    // EMPTY, never default: a default ImmutableArray throws on every read, so a closed panel would
    // hand a caller a different KIND of empty from an open one with nothing in it.
    private ImmutableArray<Layout.ArrangedNode<float>> _toneArranged = [];

    /// <summary>
    /// Test seam: the panel as the engine arranged it. A test asks this where it would otherwise
    /// scan the window for regions and redo the panel's own arithmetic to interpret what it found.
    /// </summary>
    internal ImmutableArray<Layout.ArrangedNode<float>> ToneLayoutForTest => _toneArranged;

    private void ClearToneState()
    {
        _toneTrackRects[0] = _toneTrackRects[1] = _toneTrackRects[2] = default;
        _toneArranged = [];
    }

    /// <summary>
    /// One dial's row: label, track, value. The track is a keyed <see cref="Layout.Content.Fill"/>,
    /// the escape hatch the DSL provides for a control the engine has no opinion about -- it places
    /// the rect and <see cref="DrawToneFill"/> draws the slider into it.
    /// </summary>
    /// <remarks>
    /// A dim row's fill registers no band: the press falls through to the panel background and is
    /// swallowed there. The alternative -- a live band over a control that cannot move -- is a
    /// slider that follows the pointer and changes nothing, which reads as a broken control rather
    /// than as an unmet precondition.
    /// </remarks>
    private Layout.Node ToneDialRow(string label, string fillKey, string valueText, bool enabled)
        => Layout.Builder.HStack(
                Layout.Builder.Text(label, FontSize,
                    enabled ? ViewerTheme.Palette.BodyText : ViewerTheme.Palette.DimText,
                    widthSample: ToneLabelWidthSample),
                Layout.Builder.Fill(key: fillKey).WStar(1f, ToneTrackMinWidth).HStar(),
                Layout.Builder.Text(valueText, FontSize, ViewerTheme.Palette.DimText,
                    hAlign: TextAlign.Far, widthSample: ToneValueWidthSample))
            .WithGap(ToneGap)
            .CrossCenter()
            .RowH(FontSize + ToneGap);

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
        var small = FontSize * 0.85f;
        var rowH = FontSize + ToneGap;

        // The boost keeps the precondition its own toolbar button carried. Stated, rather than left
        // as a control that quietly does nothing.
        var boostEnabled = _document?.Stars is { Count: > 0 };

        // The curve mode reaches the pixels ONLY through the boost, so at zero boost it names a
        // difference the picture cannot show.
        var curveModeLive = boostEnabled && state.CurvesBoost > 0f;
        var curveLabel = state.CurvesMode == 1 ? "Curve: spline" : "Curve: boost";

        var rows = ImmutableArray.CreateBuilder<Layout.Node>();

        rows.Add(ToneDialRow("Boost", ToneBoostFillKey, UiFormat.Percent0(state.CurvesBoost), boostEnabled));
        if (!boostEnabled)
        {
            rows.Add(ToneReasonRow(BoostNeedsStarsReason, small));
        }

        rows.Add(Layout.Builder.HStack(
                Layout.Builder.Text(string.Empty, FontSize, widthSample: ToneLabelWidthSample),
                Layout.Builder.Text(curveLabel, FontSize,
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
        rows.Add(ToneDialRow("Amount", ToneAmountFillKey, state.HdrAmount.ToString("0.00"), enabled: true));
        rows.Add(ToneDialRow("Knee", ToneKneeFillKey, state.HdrKnee.ToString("0.00"),
            enabled: state.HdrAmount > 0f));
        if (state.HdrAmount <= 0f)
        {
            rows.Add(ToneReasonRow(KneeNeedsAmountReason, small));
        }

        rows.Add(ToneSeparator());

        // The thing this control is NOT, in the one place a user looking for HDR will arrive. The
        // wrapper is clickable and inert: it swallows a press that would otherwise reach the
        // backdrop and close the panel being read.
        rows.Add(Layout.Builder.VStack(
                Layout.Builder.Text(DisplayHdrLabel, FontSize, ViewerTheme.Palette.DimText).RowH(rowH),
                Layout.Builder.Text(DisplayHdrReason, small, ViewerTheme.Palette.DimText)
                    .RowH(small + ToneGap))
            .Clickable(new HitResult.ButtonHit("ToneDisplayHdr"), _ => { })
            .WStar());

        // Outer: a one-unit border ring around the padded body, so the frame is part of the tree
        // rather than two rectangles drawn before it. The body swallows a press anywhere on it.
        return Layout.Builder.VStack(
                Layout.Builder.VStack(rows.ToImmutable().AsSpan())
                    .WithGap(ToneGap * 0.5f)
                    .Pad(PanelPadding / Scale.X)
                    .Bg(ViewerTheme.InfoPanelBg)
                    .Clickable(new HitResult.ButtonHit("TonePanelBackground"), _ => { })
                    .WStar())
            .Pad(1f)
            .Bg(ViewerTheme.Palette.SeparatorStrong)
            .WStar();
    }

    /// <summary>Draws one dial into the rect the engine placed for it, and captures that rect.</summary>
    private void DrawToneFill(ViewerState state, Layout.Content.Fill fill, RectF32 rect)
    {
        var (slider, frac, enabled, accent) = fill.Key switch
        {
            ToneBoostFillKey => (ToneSlider.Boost,
                Math.Clamp(state.CurvesBoost / BoostSliderMax, 0f, 1f),
                _document?.Stars is { Count: > 0 },
                RGBAColor32.FromFloat(0.62f, 0.55f, 0.85f, 1f)),
            ToneAmountFillKey => (ToneSlider.SoftClipAmount,
                Math.Clamp(state.HdrAmount / SoftClipAmountMax, 0f, 1f),
                true,
                RGBAColor32.FromFloat(0.85f, 0.66f, 0.35f, 1f)),
            _ => (ToneSlider.SoftClipKnee,
                Math.Clamp((state.HdrKnee - SoftClipKneeMin) / (SoftClipKneeMax - SoftClipKneeMin), 0f, 1f),
                state.HdrAmount > 0f,
                RGBAColor32.FromFloat(0.85f, 0.66f, 0.35f, 1f)),
        };

        if (!enabled || rect.Width <= 0f)
        {
            _toneTrackRects[(int)slider] = default;
            return;
        }

        _toneTrackRects[(int)slider] = rect;
        DrawTrackSlider(rect.X, rect.Width, rect.Y, rect.Height, frac,
            accent, rect, new ToneSliderHit(slider), TrackChrome, Scale);
    }

    /// <summary>
    /// Maps a cursor X onto the dragged dial's value against the rect the engine arranged for it.
    /// Nothing here writes the PRESET index: the ladders (B, H) keep their place, and a dial dragged
    /// off a rung simply is not on one, exactly as the white-balance sliders sit off the calibrated
    /// triple.
    /// </summary>
    private void UpdateToneDrag(float px)
    {
        if (_state is not { ToneDragSlider: { } slider } state)
        {
            return;
        }

        var track = _toneTrackRects[(int)slider];
        if (track.Width <= 0f)
        {
            return;
        }

        var frac = TrackFrac(track, px);
        switch (slider)
        {
            case ToneSlider.Boost:
                state.CurvesBoost = frac * BoostSliderMax;
                break;
            case ToneSlider.SoftClipAmount:
                state.HdrAmount = frac * SoftClipAmountMax;
                break;
            default:
                state.HdrKnee = SoftClipKneeMin + (frac * (SoftClipKneeMax - SoftClipKneeMin));
                break;
        }

        state.NeedsRedraw = true;
    }

    /// <summary>
    /// Begins a tone-dial drag (press on one of the tracks). Public so both mouse-down paths
    /// (FitsViewer Program + GUI viewer tab) dispatch identically, mirroring
    /// <see cref="BeginWhiteBalanceDragAt"/>.
    /// </summary>
    public void BeginToneDragAt(ToneSlider slider, float px)
    {
        if (_state is not { } state)
        {
            return;
        }

        state.ToneDragSlider = slider;
        UpdateToneDrag(px);
    }

    private void RenderTonePanel(ViewerState state)
    {
        if (!state.TonePanelOpen)
        {
            ClearToneState();
            return;
        }

        // Nothing to hang it from: the button is not on this bar (a host with a narrower set) or was
        // not enabled this frame. It closes rather than floating at a guess.
        //
        // Read back from the regions the paint REGISTERED rather than from a rect cache kept beside it:
        // the cache this used to ask was removed by the adoption sweep, which is a semantic conflict git
        // merged without a word, since one branch added this caller while the other deleted the field.
        if (!TryGetPaintedToolbarRect(ToolbarAction.Tone, out var anchor))
        {
            state.TonePanelOpen = false;
            ClearToneState();
            return;
        }

        // Claimed as it paints, like a dropdown, so the key handler's one claimant check routes
        // Escape here without a branch of its own.
        Ui.KeyboardClaimant = _toneClaimant ??= new TonePanelClaimant(() => _state);

        // The backdrop goes down FIRST so the panel's own regions, registered after it, win: a press
        // anywhere else closes the panel and is consumed, the button included.
        RegisterClickable(0f, 0f, Width, Height, new HitResult.ButtonHit("ToneBackdrop"),
            _ =>
            {
                state.TonePanelOpen = false;
                state.NeedsRedraw = true;
            });

        var tree = BuildToneTree(state);
        var ctx = new PixelMeasureContext<TSurface>(Renderer, FontPath, Scale.X, Scale.Y)
        {
            Fallback = FontFallback,
            EmojiFontPath = EmojiFontPath,
        };

        // THE BOX IS THE MEASUREMENT, and that is the whole point of the tree. The first draft of
        // this panel summed its own height from the constants its body drew with and took its width
        // from a hand-kept union of every string it might show -- and the width was already wrong,
        // because the union listed the strings someone remembered rather than the ones the tree
        // contains. The engine measures what is actually in it, prose and track minimum alike.
        var desired = Layout.Engine.Measure(tree, new Layout.Size<float>(Width, Height), ctx);
        var w = MathF.Max(BaseInfoPanelWidth * DpiScale, desired.Width);
        var x = OverlayPlacement.ClampX(anchor.X, w, Width);
        var y = anchor.Y + anchor.Height;

        _toneArranged = ArrangeLayout(tree, new RectF32(x, y, w, desired.Height), ctx);
        PaintLayout(_toneArranged, ctx, drawFill: (fill, rect) => DrawToneFill(state, fill, rect));
    }
}
