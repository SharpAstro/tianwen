using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using DIR.Lib;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Imaging;
using TianWen.UI.Abstractions.Overlays;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        // 6 a-trous detail scales, finest first. Linear gain in [0, WaveletGainMax]; neutral 1.0. Only
        // drawn for the live stacked view. The range holds what a derivation seeds: 9.7 on the finest band of the warped
        // twin (docs/plans/planetary-restoration.md, "The live view's derived sharpening"); a gain past either end is kept as
        // derived and only its dial rests at the end.
        private const int WaveletBandCount = 6;
        private const float WaveletGainMax = 10f;

        // -----------------------------------------------------------------------
        // Info panel
        // -----------------------------------------------------------------------

        private void RenderInfoPanel(IPreviewSource source, ViewerState state)
        {
            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            // The metadata and the statistics read the SOURCE (InfoPanelData), so any source could show
            // them; which do is still decided here, a document's only: a SER and the planetary stack show
            // only what applies to a sequence (the wavelet controls of the stacked view) -- filling the
            // strip the layout reserves regardless. A SER's statistics are its first frame's, which a scrub
            // would show under every frame, so widening this waits for P3 (where the preview's readouts
            // go, docs/plans/live-session-preview.md). The white balance is a toolbar popover now, shared
            // by both.
            var document = source as AstroImageDocument;

            // Info-panel rect from the single layout pass (docked right by the Split's content Dock).
            var panel = _layout.InfoPanel;
            FillRect(panel.X, panel.Y, panel.Width, panel.Height, ViewerTheme.InfoPanelBg);

            var y = panel.Y + PanelPadding;
            var x = panel.X + PanelPadding;

            var maxTextWidth = panel.Width - PanelPadding * 2;

            if (document is not null)
            {
                DrawSectionHeading(ref y, x, "Metadata", maxTextWidth);
                foreach (var line in InfoPanelData.GetMetadataLines(source))
                {
                    DrawWrappedTextLine(ref y, x, line, maxTextWidth, ViewerTheme.Palette.BodyText);
                }

                y += FontSize;

                // Rolled up by default: thirteen rows for a colour frame, the tallest block in the
                // strip, and the one most frames never need. The heading stays, so the section is
                // one click away rather than gone.
                DrawCollapsibleHeading(ref y, x, "Statistics", maxTextWidth,
                    state.InfoPanelStatisticsCollapsed, "ToggleStatistics", () =>
                    {
                        state.InfoPanelStatisticsCollapsed = !state.InfoPanelStatisticsCollapsed;
                        state.NeedsRedraw = true;
                    });
                if (!state.InfoPanelStatisticsCollapsed)
                {
                    // A table, not lines: the numbers line up on their decimal points in the strip's
                    // proportional face, and a colour frame is five rows where it was thirteen.
                    var (header, rows) = InfoPanelData.GetStatisticsTable(source);
                    DrawTable(ref y, x, maxTextWidth, header, rows);
                }
            }

            // The white-balance sliders used to be a section here. They are a popover under the
            // toolbar's white-balance button now (ImageRendererBase.WhiteBalancePanel.cs): three
            // sliders and two buttons most frames never touch were standing open under the
            // statistics, and the button can say from across the bar what the section could not,
            // that a white balance is in force.

            // The planet and telescope both the Best stack and the live view's Derive sharpen for (#1159), once, above both; a planetary
            // master's Derive too (#1314).
            if (state.ShowStacked || state.SequencePath is not null || state.IsPlanetaryMaster)
            {
                y += FontSize;
                RenderTelescopeControls(state, source.SensorType is SensorType.Monochrome, ref y, x, maxTextWidth);
            }

            // Wavelet-sharpen layer sliders: the live stacked view's, and a planetary master's (#1314). They re-sharpen a master; they have
            // no effect on a raw frame.
            if (state.ShowStacked || state.IsPlanetaryMaster)
            {
                y += FontSize;
                RenderWaveletControls(state, ref y, x, maxTextWidth);
            }

            // The whole capture stacked as planetary-stack stacks it, for a SER (#1159).
            if (state.SequencePath is not null)
            {
                y += FontSize;
                RenderBestStackControls(state, ref y, x, maxTextWidth);
            }

            // The SELECTION used to have a section here. It now floats over the picture instead
            // (ImageRendererBase.SelectionPanel.cs), with a dedicated info panel shared with the sky
            // atlas -- Alt/Az and rise/transit/set included, which this strip never had room for.

            // Cursor readout goes LAST, and that placement is the point: it only exists while the
            // pointer is over the image, so anywhere above the sliders it shoves them up and down as
            // the pointer enters and leaves -- a control that moves under the hand about to grab it.
            // A section whose presence varies belongs after every section whose does not.
            if (state.CursorPixelInfo is not null)
            {
                y += FontSize;
                DrawSectionHeading(ref y, x, "Cursor", maxTextWidth);
                foreach (var line in InfoPanelData.GetCursorLines(state))
                {
                    DrawTextLine(ref y, x, line, ViewerTheme.Palette.BodyText);
                }
            }

            // The keyboard-shortcut list used to sit here: nineteen static rows, the tallest block in the
            // info panel. It is gone. Every shortcut that has a button now rides that button's hover
            // tooltip, and the ones that do not (zoom ratios, playback, the panel toggles) live behind the
            // toolbar's "?" list. A tooltip puts the key where the user's pointer already is, which a
            // block pinned to the bottom of a side panel cannot.
        }

        // -----------------------------------------------------------------------
        // Wavelet-sharpen layer sliders (info panel; live stacked view only)
        //
        // The Registax / AstroSurface 6-layer convention: one slider per a-trous detail scale, finest first.
        // Linear gain in [0, WaveletGainMax], neutral 1.0. Dragging a layer turns sharpening on and re-pushes
        // the params; the controller re-sharpens the cached stacked master off-thread (no re-stack), so the
        // image follows within a frame or two. Same press + drag + release model as the WB sliders.
        // -----------------------------------------------------------------------

        /// <summary>Inter-row and inter-column gap for the wavelet block, design units.</summary>
        private const float WaveletGap = 6f;

        /// <summary>The band-number column and the value column, at their widest readings.</summary>
        private const string WaveletBandWidthSample = "6";
        private const string WaveletValueWidthSample = "0.0";

        // One dial per band, living across frames because the tree does not: a Content.Slider leaf
        // carries a REFERENCE to caller-owned state, and the drag the engine arms on a press holds that
        // same reference until the release. Seeded from ViewerState at the top of every build, exactly
        // as the white-balance dials one section up are.
        //
        // What each carries is the track FRACTION, not the gain: a SliderState's range is 0..1 and the
        // gain runs to WaveletGainMax, so the scale lives in the two conversions either side -- which is
        // where it lived when a drag mapped a cursor X onto a gain by hand.
        private readonly SliderState[] _waveletSliders =
            [new SliderState(), new SliderState(), new SliderState(),
             new SliderState(), new SliderState(), new SliderState()];

        /// <summary>Test seam: one band's dial, so a test can find its arranged leaf by identity.</summary>
        internal SliderState WaveletSliderState(int band) => _waveletSliders[band];

        /// <summary>
        /// One band's row: number, track, gain. The track is a <see cref="Layout.Content.Slider"/> leaf,
        /// so the engine draws it, registers it and arms its drag from the rect it painted -- draw ==
        /// drag by construction, where this used to read the band's track back out of the region list
        /// on every pointer move.
        /// </summary>
        private Layout.Node WaveletRow(int band, float gain, RGBAColor32 fill)
            => Layout.Builder.HStack(
                    Layout.Builder.Text((band + 1).ToString(), BaseFontSize, ViewerTheme.Palette.BodyText,
                        widthSample: WaveletBandWidthSample),
                    Layout.Builder.Slider(_waveletSliders[band], fill, TrackChrome).HStar(),
                    Layout.Builder.Text(gain.ToString("0.0"), BaseFontSize, ViewerTheme.Palette.DimText,
                        hAlign: TextAlign.Far, widthSample: WaveletValueWidthSample))
                .WithGap(WaveletGap)
                .CrossCenter()
                .RowH(BaseFontSize + WaveletGap);

        /// <summary>
        /// The wavelet block as one tree: an action row over one row per band. Nothing here is
        /// positioned and nothing is measured by hand; the engine is told what the content is and does
        /// both.
        /// </summary>
        private Layout.Node BuildWaveletTree(ViewerState state)
        {
            var gains = state.WaveletGains;

            // Brighter track when active; dim when sharpening is off. The sliders still WORK while dim
            // -- a drag re-enables -- they just read as inactive, which is the behaviour the hand-laid
            // version had and the reason this is a colour rather than a disabled node.
            var fill = state.WaveletSharpenEnabled
                ? RGBAColor32.FromFloat(0.45f, 0.72f, 0.78f, 1f)
                : RGBAColor32.FromFloat(0.40f, 0.45f, 0.48f, 1f);

            var rows = ImmutableArray.CreateBuilder<Layout.Node>();

            rows.Add(Layout.Builder.HStack(
                    PanelButton(
                        state.WaveletSharpenEnabled ? "Sharpen: On" : "Sharpen: Off",
                        "WaveletToggle", enabled: true,
                        onPress: () =>
                        {
                            state.WaveletSharpenEnabled = !state.WaveletSharpenEnabled;
                            state.WaveletDirty = true;
                            state.NeedsRedraw = true;
                        },
                        // Widest of the two labels, stated once on the node, so toggling it cannot move
                        // the Reset button beside it.
                        widthSample: "Sharpen: Off",
                        background: state.WaveletSharpenEnabled ? TransportTrackFill : ToolbarButtonBg),
                    PanelButton("Reset", "WaveletReset", enabled: true,
                        onPress: () =>
                        {
                            state.WaveletGains = WaveletSharpenOptions.PlanetaryDefault.Gains;
                            state.WaveletDerived = false;
                            state.DerivedWaveletGains = null;
                            state.WaveletDeriveNote = null;
                            state.WaveletDirty = true;
                            state.NeedsRedraw = true;
                        }),
                    // The derived sharpening's gains for the master on show, worked out in the background and seeded into the
                    // dials (#1159): the planet and telescope come from the section above.
                    PanelButton(state.WaveletDeriving ? "Deriving..." : "Derive", "WaveletDerive", enabled: !state.WaveletDeriving,
                        onPress: () =>
                        {
                            state.WaveletDeriveRequested = true;
                            state.NeedsRedraw = true;
                        },
                        widthSample: "Deriving..."),
                    Layout.Builder.Spacer().HStar())
                .WithGap(WaveletGap)
                .CrossCenter()
                .RowH(BaseFontSize + WaveletGap));

            var caption = state.WaveletDeriving ? "Deriving the gains from this master, about half a minute" : state.WaveletDeriveNote;
            if (caption is not null)
            {
                rows.Add(Layout.Builder.Text(caption, BaseFontSize, ViewerTheme.Palette.DimText).RowH(BaseFontSize + WaveletGap));
            }

            for (var b = 0; b < WaveletBandCount && b < gains.Length; b++)
            {
                rows.Add(WaveletRow(b, gains[b], fill));
            }

            return Layout.Builder.VStack([.. rows]).WithGap(WaveletGap);
        }

        /// <summary>
        /// The wavelet block. Seeds each band's dial from the gains, then measures and paints the tree
        /// through the widget base's own context, so measure, arrange and paint are handed ONE instance
        /// rather than three that have to agree by hand.
        /// </summary>
        private void RenderWaveletControls(ViewerState state, ref float y, float x, float panelWidth)
        {
            DrawSectionHeading(ref y, x, "Wavelet Sharpen", panelWidth);

            var gains = state.WaveletGains;
            for (var b = 0; b < WaveletBandCount && b < gains.Length; b++)
            {
                var band = b;
                _waveletSliders[b].Value = Math.Clamp(gains[b] / WaveletGainMax, 0f, 1f);
                _waveletSliders[b].OnChanged = frac =>
                {
                    if (_state is not { } dragState)
                    {
                        return;
                    }

                    // Touching a layer turns sharpening on. That rule used to live in a public
                    // BeginWaveletDragAt that two hosts had to remember to call; it is now on the only
                    // path that can move a gain at all.
                    dragState.WaveletSharpenEnabled = true;
                    dragState.WaveletGains = dragState.WaveletGains.SetItem(band, frac * WaveletGainMax);
                    dragState.WaveletDirty = true;
                    dragState.NeedsRedraw = true;
                };
            }

            var tree = BuildWaveletTree(state);
            var ctx = MeasureContext();
            var measured = MeasureLayout(tree, new Layout.Size<float>(panelWidth, float.MaxValue));
            var rect = new RectF32(x, y, panelWidth, measured.Height);
            PaintLayout(ArrangeLayout(tree, rect, ctx), ctx);
            y = rect.Bottom + (WaveletGap * DpiScale);
        }

    }
}
