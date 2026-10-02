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

        // The LOG toggle, declared whole: its label, its padding and its corner of the histogram. The engine measures the box FROM the
        // label and places it, so there is no second statement of its size to disagree with the first. There was one: the box was summed
        // by hand in device pixels around a label the engine scales from DESIGN units, the label was given the already scaled size, and
        // it was drawn DpiScale times too large for its box and read "L..." on a 2x display. Every size here is in design units.
        //
        // The hover CONDITION is still the viewer's own prediction, and has to be: hover is resolved at PAINT time, this surface paints
        // before the popovers, and an open popover claims the pointer only when IT paints. The host cannot declare the claim itself --
        // the claim is per WINDOW, and in the GUI this viewer is one widget among several (see the note at the top of Render). So the
        // prediction is consulted ONCE here, on the node, instead of being folded into a hand-computed hover boolean; .Bg always sets a
        // value, which is why the hover colour is applied conditionally rather than passed as default.
        private Layout.Node HistogramLogButton(ViewerState state)
        {
            var button = Layout.Builder.Text("LOG", BaseToolbarFontSize, ViewerTheme.Palette.BodyText, hAlign: TextAlign.Center)
                .PadX(BaseButtonPaddingH / 2f)
                // A line box, as the viewer's other declared buttons have (the tone and white balance panels): the engine measures a
                // text by its INK, which for "LOG" is its cap height alone.
                .RowH(BaseToolbarFontSize + 4f)
                .Bg(state.HistogramLogScale ? HistogramLogOnBg : HistogramLogOffBg)
                .Clickable(new HitResult.ButtonHit("HistogramLog"),
                    _ => { state.HistogramLogScale = !state.HistogramLogScale; },
                    CursorKind.Pointer);

            if (!state.OverlayOwnsPointer)
            {
                button = button.BgHover(state.HistogramLogScale ? HistogramLogOnHoverBg : HistogramLogOffHoverBg);
            }

            return Layout.Builder.Anchored(button, Layout.DockSide.Right, margin: 2f);
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
            // places it, paints it, resolves its hover and binds its click from the one arranged rect,
            // so the draw and the hit test cannot disagree -- which is the whole reason the chrome is
            // moving to trees (docs/plans/viewer-layout-engine.md). It is arranged into the histogram's
            // own rect and pins itself to the corner (HistogramLogButton).
            if (!string.IsNullOrEmpty(FontPath))
            {
                RenderLayout(HistogramLogButton(state), new RectF32(histLeft, histTop, histW, histH));
            }
        }

    }
}
