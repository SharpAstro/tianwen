using System;
using System.Globalization;
using DIR.Lib;
using TianWen.Lib.Devices;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // -----------------------------------------------------------------------
        // Best stack (info panel; a SER only)
        //
        // The whole capture stacked as `tianwen planetary-stack` stacks it (PlanetaryBestStack, #1159): the measured best, slow, run in
        // the background with its progress on the button, both masters written beside the capture and the sharpened one opened. The
        // telescope's aperture and design give the derived sharpening its diffraction; without an aperture it is the preset's.
        // -----------------------------------------------------------------------

        /// <summary>The designs the panel offers, each with the central obstruction the sharpening assumes for it.</summary>
        private static readonly (OpticalDesign Design, string Label)[] BestStackDesigns =
        [
            (OpticalDesign.Newtonian, "Newtonian"),
            (OpticalDesign.SCT, "SCT / Mak"),
            (OpticalDesign.Refractor, "Refractor"),
        ];

        /// <summary>The Best stack block as one tree: the action, the aperture stepper, the design.</summary>
        private Layout.Node BuildBestStackTree(ViewerState state)
        {
            var inv = CultureInfo.InvariantCulture;
            var running = state.BestStackProgress is not null;
            var label = state.BestStackProgress is { } fraction
                ? string.Create(inv, $"Cancel ({fraction * 100:0}%)")
                : "Best stack";
            var action = Layout.Builder.HStack(
                    PanelButton(label, "BestStack", enabled: true,
                        onPress: () =>
                        {
                            state.BestStackRequested = true;
                            state.NeedsRedraw = true;
                        },
                        widthSample: "Cancel (100%)",
                        background: running ? TransportTrackFill : ToolbarButtonBg),
                    Layout.Builder.Spacer().HStar())
                .WithGap(WaveletGap)
                .CrossCenter()
                .RowH(BaseFontSize + WaveletGap);

            var aperture = state.PlanetaryApertureMm is { } mm ? string.Create(inv, $"{mm} mm") : "no telescope";
            var stepper = Layout.Builder.HStack(
                    Layout.Builder.Text("Aperture", BaseFontSize, ViewerTheme.Palette.DimText),
                    StepButton("◀", "ApertureDown", () => state.PlanetaryApertureMm = state.SteppedAperture(up: false)),
                    Layout.Builder.Text(aperture, BaseFontSize, ViewerTheme.Palette.BodyText, hAlign: TextAlign.Center, widthSample: "no telescope"),
                    StepButton("▶", "ApertureUp", () => state.PlanetaryApertureMm = state.SteppedAperture(up: true)),
                    Layout.Builder.Spacer().HStar())
                .WithGap(WaveletGap)
                .CrossCenter()
                .RowH(BaseFontSize + WaveletGap);

            ReadOnlySpan<Layout.ButtonGroupOption<OpticalDesign>> options =
            [
                new(BestStackDesigns[0].Design, BestStackDesigns[0].Label) { Hit = new HitResult.ButtonHit("DesignNewtonian") },
                new(BestStackDesigns[1].Design, BestStackDesigns[1].Label) { Hit = new HitResult.ButtonHit("DesignSct") },
                new(BestStackDesigns[2].Design, BestStackDesigns[2].Label) { Hit = new HitResult.ButtonHit("DesignRefractor") },
            ];
            var style = new Layout.ButtonGroupStyle(TransportTrackFill, ToolbarButtonBg, ViewerTheme.Palette.BodyText, ViewerTheme.Palette.BodyText,
                GuiTheme.Hover(ToolbarButtonBg));
            var design = Layout.Builder.ButtonGroup(options, state.PlanetaryDesign,
                    chosen =>
                    {
                        state.PlanetaryDesign = chosen;
                        state.PlanetaryTelescopeChanged = true;
                        state.NeedsRedraw = true;
                    },
                    style, BaseFontSize)
                .RowH(BaseFontSize + WaveletGap);

            return Layout.Builder.VStack(action, stepper, design).WithGap(WaveletGap);

            Layout.Node StepButton(string glyph, string hit, Action step)
                => FormRowLayout.StepMark(glyph, BaseFontSize, ViewerTheme.Palette.BodyText)
                    .WFixed(BaseFontSize + WaveletGap)
                    .HStar()
                    .Bg(ToolbarButtonBg)
                    .BgHover(GuiTheme.Hover(ToolbarButtonBg))
                    .Clickable(new HitResult.ButtonHit(hit), _ =>
                    {
                        step();
                        state.PlanetaryTelescopeChanged = true;
                        state.NeedsRedraw = true;
                    });
        }

        /// <summary>The Best stack block, measured and painted through one context as the wavelet block is.</summary>
        private void RenderBestStackControls(ViewerState state, ref float y, float x, float panelWidth)
        {
            DrawSectionHeading(ref y, x, "Best stack", panelWidth);
            var tree = BuildBestStackTree(state);
            var ctx = MeasureContext();
            var measured = MeasureLayout(tree, new Layout.Size<float>(panelWidth, float.MaxValue));
            var rect = new RectF32(x, y, panelWidth, measured.Height);
            PaintLayout(ArrangeLayout(tree, rect, ctx), ctx);
            y = rect.Bottom + (WaveletGap * DpiScale);
        }
    }
}
