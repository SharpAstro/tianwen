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
        // Manual white-balance slider track rects (R, G, B), captured in RenderInfoPanel each frame; map a
        // cursor-X <-> WB multiplier in BeginWhiteBalanceDragAt / UpdateWhiteBalanceDrag. Default/empty when
        // the source is monochrome (no WB sliders drawn).
        private readonly RectF32[] _wbTrackRects = new RectF32[3];

        // White-balance slider range (canonical values live on GrayWorldWhiteBalance so the slider extent and
        // the auto-WB clamp stay in lock-step). Log-mapped so neutral (1.0) sits at the track midpoint and an
        // equal gain/cut is symmetric (0.5x left edge <-> 2.0x right edge).
        private const float WbMin = GrayWorldWhiteBalance.MinMultiplier;
        private const float WbMax = GrayWorldWhiteBalance.MaxMultiplier;

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
            if (_fontPath is null)
            {
                return;
            }

            // Metadata/statistics/cursor/stars are still-image (document) concerns; a SER source has no
            // document, so those sections are skipped and the panel shows just the (shared) white-balance
            // controls + the controls help -- filling the panel strip that the layout reserves regardless.
            var document = source as AstroImageDocument;

            // Info-panel rect from the single layout pass (docked right by the Split's content Dock).
            var panel = _layout.InfoPanel;
            FillRect(panel.X, panel.Y, panel.Width, panel.Height, ViewerTheme.InfoPanelBg);

            var y = panel.Y + PanelPadding;
            var x = panel.X + PanelPadding;

            var maxTextWidth = panel.Width - PanelPadding * 2;

            if (document is not null)
            {
                DrawTextLine(ref y, x, "-- Metadata --", ViewerTheme.Palette.HeaderText);
                foreach (var line in InfoPanelData.GetMetadataLines(document))
                {
                    DrawWrappedTextLine(ref y, x, line, maxTextWidth, ViewerTheme.Palette.BodyText);
                }

                y += FontSize;

                DrawTextLine(ref y, x, "-- Statistics --", ViewerTheme.Palette.HeaderText);
                foreach (var line in InfoPanelData.GetStatisticsLines(document))
                {
                    DrawTextLine(ref y, x, line, ViewerTheme.Palette.BodyText);
                }
            }

            if (state.CursorPixelInfo is not null)
            {
                y += FontSize;
                DrawTextLine(ref y, x, "-- Cursor --", ViewerTheme.Palette.HeaderText);
                foreach (var line in InfoPanelData.GetCursorLines(state))
                {
                    DrawTextLine(ref y, x, line, ViewerTheme.Palette.BodyText);
                }
            }

            // Manual white-balance sliders -- only meaningful for a colour source (3 channels, or a raw
            // Bayer mosaic the GPU debayers into colour). Captures per-channel track rects for the drag.
            var isColour = source.ChannelCount >= 3 || source.SensorType is SensorType.RGGB;
            if (isColour)
            {
                y += FontSize;
                RenderWhiteBalanceControls(state, ref y, x, maxTextWidth);
            }
            else
            {
                _wbTrackRects[0] = _wbTrackRects[1] = _wbTrackRects[2] = default;
            }

            // Wavelet-sharpen layer sliders -- only for the live stacked view (they re-sharpen the stacked
            // master; they have no effect on a raw frame). Sit right under the white-balance sliders.
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

            // Controls help at bottom of panel
            ReadOnlySpan<string> controlLabels =
            [
                "-- Controls --",
                "T: Cycle stretch",
                "S: Toggle stars",
                "+/-: Stretch factor",
                "C: Cycle channel",
                "D: Cycle debayer",
                "W: Color calibrate (SPCC)",
                "N: Toggle neut. background",
                "H: Cycle HDR",
                "G: Toggle grid",
                "V/Shift+V: Histogram/Log",
                "P: Plate solve",
                "Wheel/Ctrl+Wheel: Zoom",
                "Ctrl++/-: Zoom in/out",
                "F/Ctrl+0: Zoom to fit",
                "R/Ctrl+1: Zoom 1:1",
                "Ctrl+2..9: Zoom 1:N",
                "I: Toggle info panel",
                "L: Toggle file list",
                "F11: Fullscreen",
                "Esc: Quit",
            ];

            // Footer pins to the bottom of the info-panel pane (= top of the arranged status bar), not the
            // outer window bottom, so it lands correctly when the viewer is embedded in a content rect.
            var panelBottom = _layout.StatusBar.Height > 0 ? _layout.StatusBar.Y : Height - StatusBarHeight;
            var lineHeight = FontSize + 2f;
            var availableLines = (int)((panelBottom - PanelPadding - y - lineHeight) / lineHeight);
            if (availableLines >= 2)
            {
                var clipped = availableLines < controlLabels.Length;
                var linesToDraw = clipped ? availableLines - 1 : controlLabels.Length;
                var totalLines = clipped ? linesToDraw + 1 : linesToDraw;
                y = panelBottom - lineHeight * totalLines - PanelPadding;
                for (var i = 0; i < linesToDraw; i++)
                {
                    var isHeader = i == 0;
                    DrawTextLine(ref y, x, controlLabels[i],
                        isHeader ? ViewerTheme.Palette.HeaderText : ViewerTheme.Palette.DimText);
                }
                if (clipped)
                {
                    DrawTextLine(ref y, x, "...", ViewerTheme.Palette.DimText);
                }
            }
        }

        // -----------------------------------------------------------------------
        // Manual white-balance sliders (info panel; shared across FITS / TIFF / SER)
        //
        // Three log-mapped sliders (R/G/B) over [WbMin, WbMax] with neutral 1.0 at the track midpoint,
        // plus a Reset. Drag is press + move + release (mirrors the transport scrub): a press begins a
        // drag on the hit channel, mouse-move maps cursor-X -> multiplier, release ends it. A WB change
        // only re-derives the stretch uniforms from cached stats (no pixel pass), so it sets NeedsRedraw,
        // never NeedsTextureUpdate.
        // -----------------------------------------------------------------------

        private void RenderWhiteBalanceControls(ViewerState state, ref float y, float x, float panelWidth)
        {
            DrawTextLine(ref y, x, "-- White Balance --", ViewerTheme.Palette.HeaderText);

            var wb = state.ManualWhiteBalance;
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
                    DrawTrackSlider(trackX, trackW, rowY + FontSize / 2f, rowY, FontSize, frac,
                        fill, hitBand, new WhiteBalanceSliderHit(ch));
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
                    if (_source is { } src && AutoWhiteBalance.GrayWorld(src) is { } auto)
                    {
                        state.ManualWhiteBalance = auto;
                        state.NeedsRedraw = true;
                    }
                });

            const string resetLabel = "Reset WB";
            var resetW = MeasureText(resetLabel, FontSize) + gap * 2f;
            var resetX = x + autoW + gap;
            FillRect(resetX, y, resetW, btnH, ToolbarButtonBg);
            DrawText(resetLabel, resetX + gap, y + gap / 2f, FontSize, ViewerTheme.Palette.BodyText);
            RegisterClickable(resetX, y, resetW, btnH, new HitResult.ButtonHit("ResetWhiteBalance"),
                _ => { state.ManualWhiteBalance = (1f, 1f, 1f); state.NeedsRedraw = true; });
            y += btnH + FontSize;
        }

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
            var wb = state.ManualWhiteBalance;
            state.ManualWhiteBalance = ch switch
            {
                0 => (value, wb.G, wb.B),
                1 => (wb.R, value, wb.B),
                _ => (wb.R, wb.G, value),
            };
            state.NeedsRedraw = true;
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
            DrawTextLine(ref y, x, "-- Wavelet Sharpen --", ViewerTheme.Palette.HeaderText);

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
                    DrawTrackSlider(trackX, trackW, rowY + FontSize / 2f, rowY, FontSize, frac,
                        fill, hitBand, new WaveletSliderHit(b));
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
