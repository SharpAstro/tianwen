using System;
using System.Collections.Immutable;
using System.Globalization;
using DIR.Lib;
using TianWen.Lib.Imaging;

namespace TianWen.UI.Abstractions;

/// <summary>
/// The Background popover under the toolbar's <see cref="ToolbarAction.BackgroundNeutralize"/> button: the sky's
/// neutralisation (method and strength, what it did to the sky in 0..255 levels) and the background reduce
/// (<see cref="StretchUniforms.BackgroundReduce"/>, on by default), the two things that decide what the sky looks like.
/// </summary>
/// <remarks>
/// <para><b>It replaced a flat dropdown of presets</b> (Off, Mean, Mean 50 %, Green pivot, Min pivot), which had no room for
/// a strength between, for the reduce, or for saying what the neutralisation did: its status line printed the gains, which
/// on a denoised frame are about 1e-5 from unity and read 1.0000 / 1.0000 / 1.0000 over a sky they visibly levelled.</para>
/// <para>One arranged tree, the box the engine's measurement of it, exactly as the Tone popover is (see its remarks).</para>
/// </remarks>
partial class ImageRendererBase<TSurface>
{
    private const string NeutraliseHeading = "Neutralise: the sky's colour levelled before the stretch";
    private const string ReduceHeading = "Reduce: the sky curved down, bright structure kept";
    private const string NeutraliseOffReason = "off: each channel's sky stays where the stretch puts it";
    private const string ReduceNeedsStretchReason = "needs a stretch";
    private const string ReducePlanetaryReason = "off under a planet's stretch, whose sky is already black";
    private const string BackgroundStrengthLabel = "Strength";
    private const string ReduceLevelLabel = "Sky to";

    // The reduce's level: the share of itself the sky is curved down to. Below 0.1 the sky goes black and takes the faint
    // structure with it; at 1 the curve is the identity, which is the checkbox's job.
    private const float ReduceLevelMin = 0.1f;
    private const float ReduceLevelMax = 0.95f;

    /// <summary>The neutralisation's dial accent.</summary>
    private static readonly RGBAColor32 BackgroundNeutraliseFill = RGBAColor32.FromFloat(0.55f, 0.75f, 0.55f, 1f);

    /// <summary>The reduce's dial accent.</summary>
    private static readonly RGBAColor32 BackgroundReduceFill = RGBAColor32.FromFloat(0.70f, 0.62f, 0.85f, 1f);

    private readonly SliderState _backgroundStrengthSlider = new() { Min = 0f, Max = 1f };
    private readonly SliderState _backgroundReduceSlider = new() { Min = ReduceLevelMin, Max = ReduceLevelMax };

    /// <summary>Test seam: the neutralisation's strength dial.</summary>
    internal SliderState BackgroundStrengthSliderState => _backgroundStrengthSlider;

    /// <summary>Test seam: the reduce's level dial.</summary>
    internal SliderState BackgroundReduceSliderState => _backgroundReduceSlider;

    private ImmutableArray<Layout.ArrangedNode<float>> _backgroundArranged = [];

    /// <summary>Test seam: the popover as the engine arranged it.</summary>
    internal ImmutableArray<Layout.ArrangedNode<float>> BackgroundLayoutForTest => _backgroundArranged;

    private void ClearBackgroundState() => _backgroundArranged = [];

    /// <summary>
    /// Sets the neutralisation, pinning the method's gain onto the document as the dropdown did: an explicit choice shows on
    /// the toolbar even where this frame's gain lands near identity. Null is Off.
    /// </summary>
    private void ChooseNeutralisation(ViewerState state, BackgroundNeutralizationMethod? method)
    {
        if (method is { } m)
        {
            state.BackgroundNeutralizationMethod = m;
            _document?.ComputeBackgroundNeutralization(m, state.ColorCalibrationEnabled);
            state.BackgroundNeutralizationEnabled = true;
        }
        else
        {
            if (_document is not null)
            {
                _document.BackgroundNeutralization = null;
            }
            state.BackgroundNeutralizationEnabled = false;
        }
        state.NeedsRedraw = true;
    }

    /// <summary>The whole popover as one tree.</summary>
    private Layout.Node BuildBackgroundTree(ViewerState state)
    {
        var small = BaseFontSize * 0.85f;
        var inv = CultureInfo.InvariantCulture;
        var stretch = PreparedStretch;
        var stretched = stretch.Mode is not StretchMode.None;
        var neutraliseOn = state.BackgroundNeutralizationEnabled;

        _backgroundStrengthSlider.Value = state.BackgroundNeutralizationStrength;
        _backgroundStrengthSlider.Enabled = neutraliseOn;
        _backgroundStrengthSlider.OnChanged = v =>
        {
            state.BackgroundNeutralizationStrength = v;
            state.NeedsRedraw = true;
        };

        _backgroundReduceSlider.Value = state.BackgroundReduceLevel;
        var reducible = stretched && !state.BackgroundReduceStandsDown;
        _backgroundReduceSlider.Enabled = state.BackgroundReduceEnabled && reducible;
        _backgroundReduceSlider.OnChanged = v =>
        {
            state.BackgroundReduceLevel = v;
            state.NeedsRedraw = true;
        };

        var rows = ImmutableArray.CreateBuilder<Layout.Node>();

        // Neutralisation: which method, how strongly, and what it did to the sky. A colour frame's alone: one channel has no
        // colour to level.
        rows.Add(Layout.Builder.Text(NeutraliseHeading, small, ViewerTheme.Palette.DimText).WStar());
        if (_document is not { } doc || !IsColour(doc))
        {
            rows.Add(ToneReasonRow(SaturationNeedsColourReason, small));
        }
        else
        {
            AddNeutralisation();
        }

        rows.Add(ToneSeparator());

        // The reduce: on or off, and to what share of itself the sky goes.
        rows.Add(Layout.Builder.Text(ReduceHeading, small, ViewerTheme.Palette.DimText).WStar());
        var checkStyle = new Layout.CheckboxStyle(ToolbarButtonBg, ViewerTheme.Palette.BodyText,
            ViewerTheme.Palette.BodyText, GuiTheme.Hover(ToolbarButtonBg));
        rows.Add(Layout.Builder.Checkbox("Reduce background", state.BackgroundReduceEnabled,
                on =>
                {
                    state.BackgroundReduceEnabled = on;
                    state.NeedsRedraw = true;
                },
                checkStyle, BaseFontSize, new HitResult.ButtonHit("BackgroundReduce"),
                disabledReason: !stretched ? ReduceNeedsStretchReason : state.BackgroundReduceStandsDown ? ReducePlanetaryReason : null)
            .WStar());
        rows.Add(ToneDialRow(ReduceLevelLabel, _backgroundReduceSlider, UiFormat.Percent0(state.BackgroundReduceLevel),
            BackgroundReduceFill));
        if (ReducedSkyInWords(stretch, state, inv) is { } reduced)
        {
            rows.Add(ToneReasonRow(reduced, small));
        }

        void AddNeutralisation()
        {
            ReadOnlySpan<Layout.ButtonGroupOption<BackgroundNeutralizationMethod?>> methods =
            [
                new(null, "Off") { Hit = new HitResult.ButtonHit("NeutraliseOff") },
                new(BackgroundNeutralizationMethod.Mean, "Mean") { Hit = new HitResult.ButtonHit("NeutraliseMean") },
                new(BackgroundNeutralizationMethod.GreenPivot, "Green pivot") { Hit = new HitResult.ButtonHit("NeutraliseGreen") },
                new(BackgroundNeutralizationMethod.MinPivot, "Min pivot") { Hit = new HitResult.ButtonHit("NeutraliseMin") },
            ];
            rows.Add(Layout.Builder.ButtonGroup(methods,
                    neutraliseOn ? state.BackgroundNeutralizationMethod : null,
                    chosen => ChooseNeutralisation(state, chosen),
                    PlanetaryChoiceStyle, BaseFontSize)
                .WStar());
            rows.Add(ToneDialRow(BackgroundStrengthLabel, _backgroundStrengthSlider,
                UiFormat.Percent0(state.BackgroundNeutralizationStrength), BackgroundNeutraliseFill));
            if ((neutraliseOn ? NeutralisedSkyInWords(stretch, inv) : NeutraliseOffReason) is { } said)
            {
                rows.Add(ToneReasonRow(said, small));
            }
        }

        // The Tone popover's frame: a one-unit ring round the padded body, which swallows a press anywhere on it.
        return Layout.Builder.VStack(
                Layout.Builder.VStack(rows.ToImmutable().AsSpan())
                    .WithGap(ToneGap * 0.5f)
                    .Pad(BasePanelPadding)
                    .Bg(ViewerTheme.InfoPanelBg)
                    .Clickable(new HitResult.ButtonHit("BackgroundPanelBackground"), _ => { })
                    .WStar())
            .Pad(1f)
            .Bg(ViewerTheme.Palette.SeparatorStrong)
            .WStar();
    }

    /// <summary>
    /// What the neutralisation did to the sky, in the 0..255 levels a picture is read in: each channel's stretched sky without
    /// it and with it. Null where the per-channel sky is not what the picture shows (Luma, which stretches the luminance, and
    /// no stretch) or there is no background measured.
    /// </summary>
    private string? NeutralisedSkyInWords(in StretchUniforms stretch, CultureInfo inv)
    {
        if (_document?.PerChannelBackground is not { Length: >= 3 } sky || stretch.Mode is StretchMode.Luma or StretchMode.None)
        {
            return null;
        }
        var without = stretch with { BackgroundNeutralization = (1f, 1f, 1f) };
        return string.Create(inv,
            $"sky RGB {Level(without, 0)}, {Level(without, 1)}, {Level(without, 2)} to {Level(stretch, 0)}, {Level(stretch, 1)}, {Level(stretch, 2)}");

        int Level(in StretchUniforms u, int c) => (int)MathF.Round(Math.Clamp(Image.StretchNormalisedCpu(sky[c], c, u), 0f, 1f) * 255f);
    }

    /// <summary>Where the reduce puts the sky, in 0..255 levels of the stretched luminance; null where it does not run.</summary>
    private string? ReducedSkyInWords(in StretchUniforms stretch, ViewerState state, CultureInfo inv)
    {
        if (state.BackgroundReduce >= 1f || stretch.Mode is StretchMode.None || _document is not { } document)
        {
            return null;
        }
        var sky = stretch.ComputePostStretchBackground(document.PerChannelBackground, document.LumaBackground);
        var reduced = (stretch with { BackgroundReduce = state.BackgroundReduce }).ReducedBackground(sky);
        return string.Create(inv, $"sky {MathF.Round(sky * 255f):0} to {MathF.Round(reduced * 255f):0} of 255");
    }

    private void RenderBackgroundPanel(ViewerState state)
    {
        // As the Tone popover: a closed popover is not measured, and one with no button to hang from closes.
        if (!state.BackgroundPopover.IsOpen)
        {
            ClearBackgroundState();
            return;
        }
        if (!TryGetPaintedToolbarRect(ToolbarAction.BackgroundNeutralize, out var anchor))
        {
            state.BackgroundPopover.Close();
            ClearBackgroundState();
            return;
        }

        var ctx = MeasureContext();
        var content = BuildBackgroundTree(state).WStar(1f, BaseInfoPanelWidth);
        var tree = Layout.Builder.Popover(anchor, content, state.BackgroundPopover);
        _backgroundArranged = ArrangeLayout(tree, new RectF32(0f, 0f, Width, Height), ctx);
        PaintLayout(_backgroundArranged, ctx);
    }
}
