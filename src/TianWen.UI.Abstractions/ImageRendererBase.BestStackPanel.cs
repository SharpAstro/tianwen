using System;
using System.Globalization;
using System.Linq;
using DIR.Lib;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Devices;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // -----------------------------------------------------------------------
        // Best stack (info panel; a SER only)
        //
        // The whole capture stacked as `tianwen planetary-stack` stacks it (PlanetaryBestStack, #1159): the measured best, slow, run in
        // the background with its progress on the button, both masters written beside the capture and the sharpened one opened. The
        // planet and telescope section above it (shared with the live view's Derive) gives the derived sharpening its planet and its
        // diffraction; without them it is the preset's.
        // -----------------------------------------------------------------------

        /// <summary>The designs the panel offers, each with the central obstruction the sharpening assumes for it.</summary>
        private static readonly (OpticalDesign Design, string Label)[] BestStackDesigns =
        [
            (OpticalDesign.Newtonian, "Newton"),
            (OpticalDesign.SCT, "SCT / Mak"),
            (OpticalDesign.Refractor, "Refractor"),
        ];

        /// <summary>
        /// The strength stops the panel offers (<see cref="PlanetarySharpening.StrengthStops"/>), the truth first: one Derive fits them all
        /// (#1314). A stop's hit is its value without the point, <c>Strength25</c> for 2.5.
        /// </summary>
        private static readonly Layout.ButtonGroupOption<double>[] StrengthOptions =
        [
            .. PlanetarySharpening.StrengthStops.Select(stop => new Layout.ButtonGroupOption<double>(stop,
                stop == 1 ? "Truth" : stop.ToString("0.#", CultureInfo.InvariantCulture))
            {
                Hit = new HitResult.ButtonHit(stop == 1 ? "StrengthTruth" : "Strength" + stop.ToString("0.#", CultureInfo.InvariantCulture).Replace(".", "")),
            }),
        ];

        /// <summary>The Best stack block as one tree: the action alone, its planet and telescope in the section above it.</summary>
        private Layout.Node BuildBestStackTree(ViewerState state)
        {
            var inv = CultureInfo.InvariantCulture;
            var running = state.BestStackProgress is not null;
            var label = state.BestStackProgress is { } fraction
                ? string.Create(inv, $"Cancel ({fraction * 100:0}%)")
                : "Best stack";
            return Layout.Builder.HStack(
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
        }

        /// <summary>
        /// The planet and telescope section as one tree, which the Best stack and the live view's Derive both sharpen for: the planet, the
        /// filter (a mono source's only), the aperture stepper, the design. The planet and the filter default to what the capture's name
        /// gives, and a caption says what that is, so a name that gives no planet (which leaves the sharpening the preset's) is seen before
        /// a run, not after it. A live capture has no name, so there it is the panel's choice alone.
        /// </summary>
        private Layout.Node BuildTelescopeTree(ViewerState state, bool mono)
        {
            var inv = CultureInfo.InvariantCulture;
            var style = new Layout.ButtonGroupStyle(TransportTrackFill, ToolbarButtonBg, ViewerTheme.Palette.BodyText, ViewerTheme.Palette.BodyText,
                GuiTheme.Hover(ToolbarButtonBg));

            // A capture's name, or a planetary master's file's (#1314), whose OBJECT names the planet first.
            var capture = state.SequencePath ?? state.MasterPath;
            ReadOnlySpan<Layout.ButtonGroupOption<CatalogIndex?>> planets =
            [
                new(null, "Auto") { Hit = new HitResult.ButtonHit("PlanetAuto") },
                new(CatalogIndex.Jupiter, "Jupiter") { Hit = new HitResult.ButtonHit("PlanetJupiter") },
                new(CatalogIndex.Saturn, "Saturn") { Hit = new HitResult.ButtonHit("PlanetSaturn") },
            ];
            var rows = new System.Collections.Generic.List<Layout.Node>
            {
                Caption(PlanetCaption()),
                Layout.Builder.ButtonGroup(planets, state.PlanetaryBody,
                        chosenPlanet =>
                        {
                            state.PlanetaryBody = chosenPlanet;
                            state.NeedsRedraw = true;
                        },
                        style, BaseFontSize)
                    .RowH(BaseFontSize + WaveletGap),
            };

            if (mono)
            {
                ReadOnlySpan<Layout.ButtonGroupOption<double?>> filters =
                [
                    new(null, "Auto") { Hit = new HitResult.ButtonHit("FilterAuto") },
                    new(550d, "L") { Hit = new HitResult.ButtonHit("FilterL") },
                    new(650d, "R") { Hit = new HitResult.ButtonHit("FilterR") },
                    new(530d, "G") { Hit = new HitResult.ButtonHit("FilterG") },
                    new(460d, "B") { Hit = new HitResult.ButtonHit("FilterB") },
                    new(750d, "IR") { Hit = new HitResult.ButtonHit("FilterIr") },
                ];
                rows.Add(Caption(FilterCaption()));
                rows.Add(Layout.Builder.ButtonGroup(filters, state.PlanetaryFilterNm,
                        chosenFilter =>
                        {
                            state.PlanetaryFilterNm = chosenFilter;
                            state.NeedsRedraw = true;
                        },
                        style, BaseFontSize)
                    .RowH(BaseFontSize + WaveletGap));
            }

            var aperture = state.PlanetaryApertureMm is { } mm ? string.Create(inv, $"{mm} mm") : "no telescope";
            rows.Add(Layout.Builder.HStack(
                    Layout.Builder.Text("Aperture", BaseFontSize, ViewerTheme.Palette.DimText),
                    StepButton("◀", "ApertureDown", () => state.PlanetaryApertureMm = state.SteppedAperture(up: false)),
                    Layout.Builder.Text(aperture, BaseFontSize, ViewerTheme.Palette.BodyText, hAlign: TextAlign.Center, widthSample: "no telescope"),
                    StepButton("▶", "ApertureUp", () => state.PlanetaryApertureMm = state.SteppedAperture(up: true)),
                    Layout.Builder.Spacer().HStar())
                .WithGap(WaveletGap)
                .CrossCenter()
                .RowH(BaseFontSize + WaveletGap));

            ReadOnlySpan<Layout.ButtonGroupOption<OpticalDesign>> designs =
            [
                new(BestStackDesigns[0].Design, BestStackDesigns[0].Label) { Hit = new HitResult.ButtonHit("DesignNewtonian") },
                new(BestStackDesigns[1].Design, BestStackDesigns[1].Label) { Hit = new HitResult.ButtonHit("DesignSct") },
                new(BestStackDesigns[2].Design, BestStackDesigns[2].Label) { Hit = new HitResult.ButtonHit("DesignRefractor") },
            ];
            rows.Add(Layout.Builder.ButtonGroup(designs, state.PlanetaryDesign,
                    chosen =>
                    {
                        state.PlanetaryDesign = chosen;
                        state.PlanetaryTelescopeChanged = true;
                        state.NeedsRedraw = true;
                    },
                    style, BaseFontSize)
                .RowH(BaseFontSize + WaveletGap));

            // How far past the truth the sharpening goes (#1251): the truth by default, a post's look as an option. Once derived, a stop
            // switches the dials to its own gains at once (#1314); before, it is what the next Derive and Best stack take.
            rows.Add(Caption(state.PlanetaryStrength == 1
                ? "Sharpening: to the truth"
                : string.Create(inv, $"Sharpening: {state.PlanetaryStrength:0.#} times the truth in the mid scales")));
            rows.Add(Layout.Builder.ButtonGroup(StrengthOptions, state.PlanetaryStrength, state.ChooseStrength,
                    style, BaseFontSize)
                .RowH(BaseFontSize + WaveletGap));

            return Layout.Builder.VStack([.. rows]).WithGap(WaveletGap);

            Layout.Node Caption(string text)
                => Layout.Builder.Text(text, BaseFontSize, ViewerTheme.Palette.DimText).RowH(BaseFontSize + WaveletGap);

            // What the planet is, and where it comes from: the panel, the capture's name, or neither.
            string PlanetCaption()
            {
                if (state.PlanetaryBody is { } chosenBody)
                {
                    return $"Planet: {chosenBody}";
                }
                if (state.MasterPlanet is { } fromHeader)
                {
                    return $"Planet: {fromHeader}, from the header";
                }
                if (capture is null)
                {
                    return "Planet: choose one for the derived sharpening";
                }
                if (PlanetaryCaptureName.Planet(capture) is { } fromName)
                {
                    return $"Planet: {fromName}, from the name";
                }
                return "Planet: none in the name, so the preset's sharpening";
            }

            // The filter likewise, broadband where nothing names one.
            string FilterCaption()
            {
                if (state.PlanetaryFilterNm is { } chosenNm)
                {
                    return string.Create(inv, $"Filter: {chosenNm:0} nm");
                }
                if (capture is not null && PlanetaryCaptureName.WavelengthNm(capture) is { } fromNameNm)
                {
                    return string.Create(inv, $"Filter: {fromNameNm:0} nm, from the name");
                }
                return "Filter: none named, so broadband (550 nm)";
            }

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

        /// <summary>The planet and telescope section, measured and painted as the wavelet block is.</summary>
        private void RenderTelescopeControls(ViewerState state, bool mono, ref float y, float x, float panelWidth)
        {
            DrawSectionHeading(ref y, x, "Planet and telescope", panelWidth);
            PaintTree(BuildTelescopeTree(state, mono), ref y, x, panelWidth);
        }

        /// <summary>The Best stack block, measured and painted through one context as the wavelet block is.</summary>
        private void RenderBestStackControls(ViewerState state, ref float y, float x, float panelWidth)
        {
            DrawSectionHeading(ref y, x, "Best stack", panelWidth);
            PaintTree(BuildBestStackTree(state), ref y, x, panelWidth);
        }

        // A panel block's tree measured and painted through one context, below y, which moves past it.
        private void PaintTree(Layout.Node tree, ref float y, float x, float panelWidth)
        {
            var ctx = MeasureContext();
            var measured = MeasureLayout(tree, new Layout.Size<float>(panelWidth, float.MaxValue));
            var rect = new RectF32(x, y, panelWidth, measured.Height);
            PaintLayout(ArrangeLayout(tree, rect, ctx), ctx);
            y = rect.Bottom + (WaveletGap * DpiScale);
        }
    }
}
