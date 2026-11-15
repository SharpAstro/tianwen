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
            var histH = BaseHistogramHeight * DpiScale;
            var margin = BaseHistogramMargin * DpiScale;
            // Right edge + top of the image-area pane (abuts the info panel / region edge) from the layout pass.
            var area = _layout.ImageArea;
            // Never wider than the pane it sits in: in a narrow window it would reach left over the file list.
            // A pane that arrived with no width is a narrow window, not a missing layout: only before the first layout pass
            // (the toolbar spans the window once it has run) does the region stand in for the pane.
            var arranged = _layout.Toolbar.Width > 0f;
            var histW = arranged
                ? MathF.Min(BaseHistogramWidth * DpiScale, MathF.Max(0f, area.Width - 2f * margin))
                : BaseHistogramWidth * DpiScale;
            var region = ContentRegion;
            var rightEdge = arranged ? area.X + area.Width : region.X + region.Width;
            var top = (arranged ? area.Y : region.Y + ToolbarHeight) + margin;
            return (rightEdge - histW - margin, top, histW, histH);
        }

        // The LOG toggle, declared whole: its label, its padding and its corner of the histogram. The engine measures the box FROM the
        // label and places it, so there is no second statement of its size to disagree with the first. There was one: the box was summed
        // by hand in device pixels around a label the engine scales from DESIGN units, the label was given the already scaled size, and
        // it was drawn DpiScale times too large for its box and read "L..." on a 2x display. Every size here is in design units.
        //
        // Its hover is the node's: an open popover keeps it unlit although this paints before the popovers, because the popover's
        // claim on the pointer carries across the frame (DIR.Lib 11.14, WindowUiSettings.PointerClaim). The condition this used to
        // test, the viewer's own prediction of which overlays were open, is gone with it.
        private Layout.Node HistogramLogButton(ViewerState state)
        {
            var button = Layout.Builder.Text("LOG", BaseToolbarFontSize, ViewerTheme.Palette.BodyText, hAlign: TextAlign.Center)
                // The button is its padding: the label measures its face's line box (DIR.Lib 11.20), not its ink, which for "LOG" was
                // its cap height alone and had to be given a line box by hand.
                .PadX(BaseButtonPaddingH / 2f)
                .Bg(state.HistogramLogScale ? HistogramLogOnBg : HistogramLogOffBg)
                .Clickable(new HitResult.ButtonHit("HistogramLog"),
                    _ => { state.HistogramLogScale = !state.HistogramLogScale; },
                    CursorKind.Pointer)
                .BgHover(state.HistogramLogScale ? HistogramLogOnHoverBg : HistogramLogOffHoverBg);

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

            // No room in the pane: draw nothing rather than reach over the file list or the info panel.
            if (histW <= 0f)
            {
                return;
            }

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
