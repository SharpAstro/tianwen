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
        // -----------------------------------------------------------------------
        // Histogram overlay
        // -----------------------------------------------------------------------

        private (float Left, float Top, float Width, float Height) GetHistogramRect(ViewerState state)
        {
            var histW = BaseHistogramWidth * DpiScale;
            var histH = BaseHistogramHeight * DpiScale;
            var margin = BaseHistogramMargin * DpiScale;
            // Right edge + top of the image-area pane (abuts the info panel / region edge) from the layout pass.
            var area = _layout.ImageArea;
            var region = ContentRegion;
            var rightEdge = area.Width > 0 ? area.X + area.Width : region.X + region.Width;
            var top = (area.Height > 0 ? area.Y : region.Y + ToolbarHeight) + margin;
            return (rightEdge - histW - margin, top, histW, histH);
        }

        private (float X, float Y, float W, float H) GetHistogramLogButtonRect(ViewerState state)
        {
            var (histLeft, histTop, histW, _) = GetHistogramRect(state);
            var btnW = MeasureText("LOG", ToolbarFontSize) + ButtonPaddingH;
            var btnH = ToolbarFontSize + 4f * DpiScale;
            var btnX = histLeft + histW - btnW - 2f * DpiScale;
            var btnY = histTop + 2f * DpiScale;
            return (btnX, btnY, btnW, btnH);
        }

        /// <summary>
        /// Returns true if the given screen position hits the histogram LOG button.
        /// </summary>
        public bool HitTestHistogramLog(float screenX, float screenY, ViewerState state)
        {
            if (!state.ShowHistogram || GetHistogramDisplay() is not { ChannelCount: > 0 })
            {
                return false;
            }
            var (bx, by, bw, bh) = GetHistogramLogButtonRect(state);
            return screenX >= bx && screenX < bx + bw && screenY >= by && screenY < by + bh;
        }

        private void RenderHistogram(IPreviewSource source, ViewerState state)
        {
            if (GetHistogramDisplay() is not { ChannelCount: > 0 } histogramDisplay)
            {
                return;
            }

            var stretch = source.ComputeStretchUniforms(
                state.StretchMode, state.StretchParameters,
                bgNeutralizationStrength: state.BackgroundNeutralizationStrength,
                manualWhiteBalance: state.ManualWhiteBalance);

            var (histLeft, histTop, histW, histH) = GetHistogramRect(state);

            // Semi-transparent background
            FillRect(histLeft, histTop, histW, histH, ViewerTheme.HistogramBg);

            RenderHistogramQuad(stretch, histogramDisplay, state,
                histLeft, histTop, histLeft + histW, histTop + histH, Width, Height);

            // Draw LOG button in upper-right corner of histogram
            if (!string.IsNullOrEmpty(FontPath))
            {
                var (bx, by, bw, bh) = GetHistogramLogButtonRect(state);
                var mouseX = state.MouseScreenPosition.X;
                var mouseY = state.MouseScreenPosition.Y;
                var hovered = !state.ToolbarDropdown.IsOpen && mouseX >= bx && mouseX < bx + bw && mouseY >= by && mouseY < by + bh;

                if (state.HistogramLogScale)
                {
                    FillRect(bx, by, bw, bh, hovered ? HistogramLogOnHoverBg : HistogramLogOnBg);
                }
                else
                {
                    FillRect(bx, by, bw, bh, hovered ? HistogramLogOffHoverBg : HistogramLogOffBg);
                }

                var textY = by + (bh - ToolbarFontSize) / 2f;
                DrawText("LOG", bx + ButtonPaddingH / 2f, textY, ToolbarFontSize, ViewerTheme.Palette.BodyText);

                RegisterClickable(bx, by, bw, bh, new HitResult.ButtonHit("HistogramLog"),
                    _ => { state.HistogramLogScale = !state.HistogramLogScale; });
            }
        }

    }
}
