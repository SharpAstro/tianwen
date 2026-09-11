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
        // Wavelet-sharpen layer slider track rects (6 a-trous scales, finest first), captured in
        // RenderWaveletControls each frame; map a cursor-X <-> per-layer gain. Only drawn for the live
        // stacked view. Linear gain in [0, WaveletGainMax]; neutral 1.0.
        private readonly RectF32[] _waveletTrackRects = new RectF32[6];
        private const float WaveletGainMax = 5f;

        // -----------------------------------------------------------------------
        // Info panel
        // -----------------------------------------------------------------------

        private void RenderInfoPanel(IPreviewSource source, ViewerState state)
        {
            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            // Metadata/statistics/cursor/stars are still-image (document) concerns; a SER source has no
            // document, so those sections are skipped and the panel shows only what applies to a
            // sequence (the wavelet controls of the stacked view) -- filling the strip the layout
            // reserves regardless. The white balance is a toolbar popover now, shared by both.
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
                foreach (var line in InfoPanelData.GetMetadataLines(document))
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
                    var (header, rows) = InfoPanelData.GetStatisticsTable(document);
                    DrawTable(ref y, x, maxTextWidth, header, rows);
                }
            }

            // The white-balance sliders used to be a section here. They are a popover under the
            // toolbar's white-balance button now (ImageRendererBase.WhiteBalancePanel.cs): three
            // sliders and two buttons most frames never touch were standing open under the
            // statistics, and the button can say from across the bar what the section could not,
            // that a white balance is in force.

            // Wavelet-sharpen layer sliders -- only for the live stacked view (they re-sharpen the stacked
            // master; they have no effect on a raw frame).
            if (state.ShowStacked)
            {
                y += FontSize;
                RenderWaveletControls(state, ref y, x, maxTextWidth);
            }
            else
            {
                for (var i = 0; i < _waveletTrackRects.Length; i++)
                {
                    _waveletTrackRects[i] = default;
                }
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

        private void RenderWaveletControls(ViewerState state, ref float y, float x, float panelWidth)
        {
            DrawSectionHeading(ref y, x, "Wavelet Sharpen", panelWidth);

            var gap = 6f * DpiScale;
            var btnH = FontSize + gap;

            // On/Off toggle (active = blue) + Reset-to-default, both self-contained via OnClick.
            var toggleLabel = state.WaveletSharpenEnabled ? "Sharpen: On" : "Sharpen: Off";
            var toggleW = MeasureText(toggleLabel, FontSize) + gap * 2f;
            FillRect(x, y, toggleW, btnH, state.WaveletSharpenEnabled ? TransportTrackFill : ToolbarButtonBg);
            DrawText(toggleLabel, x + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(x, y, toggleW, btnH, new HitResult.ButtonHit("WaveletToggle"),
                _ => { state.WaveletSharpenEnabled = !state.WaveletSharpenEnabled; state.WaveletDirty = true; state.NeedsRedraw = true; });

            const string resetLabel = "Reset";
            var resetW = MeasureText(resetLabel, FontSize) + gap * 2f;
            var resetX = x + toggleW + gap;
            FillRect(resetX, y, resetW, btnH, ToolbarButtonBg);
            DrawText(resetLabel, resetX + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(resetX, y, resetW, btnH, new HitResult.ButtonHit("WaveletReset"),
                _ => { state.WaveletGains = WaveletSharpenOptions.PlanetaryDefault.Gains; state.WaveletDirty = true; state.NeedsRedraw = true; });
            y += btnH + gap;

            var rowH = FontSize + gap;
            var labelW = MeasureText("6", FontSize) + gap;
            var valueW = MeasureText("0.0", FontSize) + gap;
            // Brighter track fill when active; dim when sharpening is off (the sliders still work -- a drag
            // re-enables -- but read as inactive).
            var fill = state.WaveletSharpenEnabled
                ? RGBAColor32.FromFloat(0.45f, 0.72f, 0.78f, 1f)
                : RGBAColor32.FromFloat(0.40f, 0.45f, 0.48f, 1f);

            var gains = state.WaveletGains;
            for (var b = 0; b < _waveletTrackRects.Length; b++)
            {
                if (b >= gains.Length)
                {
                    _waveletTrackRects[b] = default;
                    continue;
                }

                var rowY = y;
                DrawText((b + 1).ToString(), x, rowY, FontSize, ViewerTheme.Palette.BodyText);

                var trackX = x + labelW;
                var trackRight = x + panelWidth - valueW;
                var trackW = MathF.Max(0f, trackRight - trackX);
                if (trackW > 0f)
                {
                    var frac = Math.Clamp(gains[b] / WaveletGainMax, 0f, 1f);
                    var hitBand = new RectF32(trackX, rowY - gap / 2f, trackW, FontSize + gap);
                    _waveletTrackRects[b] = hitBand;
                    DrawTrackSlider(trackX, trackW, rowY, FontSize, frac,
                        fill, hitBand, new WaveletSliderHit(b), TrackChrome, DpiScale);
                }
                else
                {
                    _waveletTrackRects[b] = default;
                }

                DrawText(gains[b].ToString("0.0"), trackRight, rowY, FontSize, ViewerTheme.Palette.DimText);
                y = rowY + rowH;
            }
        }

        /// <summary>
        /// Begins a wavelet-layer slider drag (press on a layer track). Public so both mouse-down paths
        /// (FitsViewer Program + GUI viewer tab) dispatch identically, mirroring <see cref="BeginWhiteBalanceDragAt"/>.
        /// Touching a layer turns sharpening on.
        /// </summary>
        public void BeginWaveletDragAt(int band, float px)
        {
            if (_state is not { } state || (uint)band >= (uint)_waveletTrackRects.Length)
            {
                return;
            }

            state.WaveletDragBand = band;
            state.WaveletSharpenEnabled = true;
            UpdateWaveletDrag(px);
        }

        // Maps a cursor X onto a per-layer gain for the active drag band against its captured track rect.
        private void UpdateWaveletDrag(float px)
        {
            if (_state is not { } state)
            {
                return;
            }
            var b = state.WaveletDragBand;
            if ((uint)b >= (uint)_waveletTrackRects.Length || _waveletTrackRects[b].Width <= 0f || b >= state.WaveletGains.Length)
            {
                return;
            }

            var frac = TrackFrac(_waveletTrackRects[b], px);
            state.WaveletGains = state.WaveletGains.SetItem(b, frac * WaveletGainMax);
            state.WaveletDirty = true;
            state.NeedsRedraw = true;
        }

    }
}
