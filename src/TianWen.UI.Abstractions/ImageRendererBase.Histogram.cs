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

        // The LOG toggle's label, one node for the paint and the test seam. A layout node's font size is in DESIGN units (the engine
        // scales it by DpiScale), while the box it is drawn in is in device pixels: given ToolbarFontSize, already scaled, the label was
        // drawn DpiScale times too large for its box and read "L..." on a 2x display.
        private Layout.Node HistogramLogLabel()
            => Layout.Builder.Text("LOG", BaseToolbarFontSize, ViewerTheme.Palette.BodyText, hAlign: TextAlign.Center);

        /// <summary>Test seam: the LOG label's measured width, as the engine draws it, and the width of the box it is drawn in.</summary>
        internal (float Label, float Box) HistogramLogLabelFit(ViewerState state)
            => (MeasureLayout(HistogramLogLabel(), new Layout.Size<float>(float.MaxValue, float.MaxValue)).Width, GetHistogramLogButtonRect(state).W);

        private (float X, float Y, float W, float H) GetHistogramLogButtonRect(ViewerState state)
        {
            var (histLeft, histTop, histW, _) = GetHistogramRect(state);
            var btnW = MeasureText("LOG", ToolbarFontSize) + ButtonPaddingH;
            var btnH = ToolbarFontSize + 4f * DpiScale;
            var btnX = histLeft + histW - btnW - 2f * DpiScale;
            var btnY = histTop + 2f * DpiScale;
            return (btnX, btnY, btnW, btnH);
        }

        private void RenderHistogram(IPreviewSource source, ViewerState state)
        {
            // Taken here, the first time the overlay is drawn after an upload, rather than at the upload:
            // see UploadDocumentTexturesCore. Hidden, the source is never asked.
            if (_histogramUploadPending)
            {
                _histogramUploadPending = false;
                UploadHistogramData(source);
            }

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

            // The LOG button is a DECLARED node, not four hand-laid rects. The engine measures it,
            // paints it, resolves its hover and binds its click from the one arranged rect, so the
            // draw and the hit test cannot disagree -- which is the whole reason the chrome is moving
            // to trees (docs/plans/viewer-layout-engine.md).
            //
            // The hover CONDITION is still the viewer's own prediction, and has to be: hover is
            // resolved at PAINT time, this surface paints before the popovers, and an open popover
            // claims the pointer only when IT paints. The host cannot declare the claim itself --
            // the claim is per WINDOW, and in the GUI this viewer is one widget among several (see
            // the note at the top of Render). So the prediction is consulted ONCE here, on the node,
            // instead of being folded into a hand-computed hover boolean; .Bg always sets a value,
            // which is why the hover colour is applied conditionally rather than passed as default.
            if (!string.IsNullOrEmpty(FontPath))
            {
                var (bx, by, bw, bh) = GetHistogramLogButtonRect(state);
                var fill = state.HistogramLogScale ? HistogramLogOnBg : HistogramLogOffBg;
                var hoverFill = state.HistogramLogScale ? HistogramLogOnHoverBg : HistogramLogOffHoverBg;

                var button = HistogramLogLabel()
                    .Bg(fill)
                    .Clickable(new HitResult.ButtonHit("HistogramLog"),
                        _ => { state.HistogramLogScale = !state.HistogramLogScale; },
                        CursorKind.Pointer);

                if (!state.OverlayOwnsPointer)
                {
                    button = button.BgHover(hoverFill);
                }

                RenderLayout(button, new RectF32(bx, by, bw, bh));
            }
        }

    }
}
