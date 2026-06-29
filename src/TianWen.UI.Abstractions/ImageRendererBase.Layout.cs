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
        // Content region (embedding seam)
        //
        // The widget normally owns the whole surface (tianwen-fits is full-screen). When embedded in a host
        // that owns chrome of its own (the GUI's 🪐 tab, with a left sidebar + top status bar + a capture
        // control strip), the host hands it a content rect via SetContentRegion. That rect is simply the
        // ROOT the single layout pass arranges within -- exactly like every GUI tab arranges within the rect
        // GetContentArea() hands it. Toolbar, image pane, info panel and status bar are all leaves of that one
        // arranged tree (see ComputeLayout), so "embed" is "arrange at a different root" with no per-site
        // offset math. The GPU image/histogram quads still project over the FULL surface (projW/projH =
        // Width/Height); only the placement rects move. Default (empty) = full surface, so tianwen-fits is
        // byte-identical.
        // -----------------------------------------------------------------------

        private RectF32 _contentRect;

        /// <summary>The region the layout + chrome are arranged within: the host rect when embedded, else the full surface.</summary>
        private RectF32 ContentRegion =>
            _contentRect is { Width: > 0f, Height: > 0f } ? _contentRect : new RectF32(0f, 0f, Width, Height);

        /// <summary>
        /// Sets the content region the layout pass roots at. Pass an empty rect (default) to use the full
        /// surface. The GPU projection stays full-surface; only the arranged layout/chrome placement moves.
        /// </summary>
        public void SetContentRegion(RectF32 region) => _contentRect = region;

        // -----------------------------------------------------------------------
        // Single-source layout pass
        //
        // The WHOLE widget chrome derives its geometry from ONE arrangement rooted at ContentRegion (the
        // full surface, or the host rect when embedded): an outer Dock pins the toolbar strip to the top and
        // the status bar to the bottom; the remaining middle band holds a Split (file list + draggable
        // divider) wrapping a Dock (right-edge info panel + fill image). Every consumer reads the arranged
        // pane rects + the single image placement below -- no chrome is hand-placed at (0,0,Width,...), so
        // embedding is "arrange at a different root" with no offset math. The Split divider is the draw==hit
        // node, so the resize grab region IS the drawn bar.
        // -----------------------------------------------------------------------

        private readonly record struct ViewerLayout(
            RectF32 Toolbar, RectF32 FileList, RectF32 ImageArea, RectF32 InfoPanel, RectF32 StatusBar);

        private readonly record struct ImagePlacement(float OffsetX, float OffsetY, float DrawW, float DrawH, float Scale);

        private ViewerLayout _layout;
        private ImmutableArray<Layout.ArrangedNode<float>> _layoutArranged;
        private ImagePlacement _placement;

        /// <summary>
        /// The on-screen rectangle the image is currently drawn into (fit / zoom / pan applied), in surface
        /// pixels. Empty (zero size) before the first frame. Exposed so a subclass can draw an overlay aligned
        /// to the displayed image -- e.g. the planetary ROI rectangle on the live stream, or a host that
        /// composites a guide-star reticle on top after <see cref="Render"/> returns.
        /// </summary>
        public RectF32 CurrentImageRect => new RectF32(_placement.OffsetX, _placement.OffsetY, _placement.DrawW, _placement.DrawH);

        /// <summary>
        /// The arranged image-area rectangle (the region between the toolbar and the info panel / file list /
        /// status bar), in surface pixels, as of the last <see cref="Render"/>. Exposed so a subclass can clamp
        /// an on-image overlay to where the image is actually shown -- not over the surrounding chrome.
        /// </summary>
        protected RectF32 ImageAreaRect => _layout.ImageArea;

        /// <summary>Design-unit thickness of the file-list resize divider (the Split divider IS the grab bar).</summary>
        private const float BaseFileListDividerWidth = 6f;

        private void ComputeLayout(ViewerState state, string fontPath)
        {
            Layout.Node content = state.ShowInfoPanel
                ? Layout.Builder.Dock(
                    Layout.Builder.Fill(key: "image"),
                    Layout.Builder.Right(Layout.Builder.Fill(key: "infoPanel"), BaseInfoPanelWidth))
                : Layout.Builder.Fill(key: "image");

            Layout.Node middle = state.ShowFileList
                ? Layout.Builder.Split(
                    Layout.Builder.Fill(key: "fileList"),
                    content,
                    Layout.Axis.Horizontal,
                    firstExtent: state.FileListWidthBase,
                    dividerThickness: BaseFileListDividerWidth,
                    dividerHit: new ResizeHandleHit("FileList"),
                    dividerColor: state.IsResizingFileList ? ResizeHandleActiveColor : ResizeHandleIdleColor)
                : content;

            // Outer chrome as part of the SAME arrangement: toolbar pinned top, status bar pinned bottom
            // (design-unit extents -- the engine scales them by DpiScale, matching the old ToolbarHeight /
            // StatusBarHeight pixel reservations), the middle band fills the remainder. With HideChrome the
            // toolbar/status rows are dropped entirely so the image (+ optional panels) fills the whole region
            // -- an embedded live preview wants no standalone-viewer chrome (Render also skips painting them).
            Layout.Node root = state.HideChrome
                ? middle
                : Layout.Builder.Dock(
                    middle,
                    Layout.Builder.Top(Layout.Builder.Fill(key: "toolbar"), BaseToolbarHeight),
                    Layout.Builder.Bottom(Layout.Builder.Fill(key: "statusBar"), BaseStatusBarHeight));

            _layoutArranged = ArrangeLayout(root, ContentRegion, fontPath, DpiScale);

            RectF32 toolbar = default, fileList = default, image = default, infoPanel = default, statusBar = default;
            foreach (var (node, b) in _layoutArranged)
            {
                if (node is Layout.Node.Leaf { Content: Layout.Content.Fill fill })
                {
                    var r = new RectF32(b.X, b.Y, b.Width, b.Height);
                    switch (fill.Key)
                    {
                        case "toolbar": toolbar = r; break;
                        case "fileList": fileList = r; break;
                        case "image": image = r; break;
                        case "infoPanel": infoPanel = r; break;
                        case "statusBar": statusBar = r; break;
                    }
                }
            }

            // Reserve the transport strip at the bottom of the image pane for a sequence, shrinking the
            // image area so the strip never overlaps the picture. The file list / info panel keep their
            // full height -- the transport belongs to the image column only.
            _transportRect = default;
            if (state.IsSequence)
            {
                var th = MathF.Min(TransportHeight, image.Height);
                var shrunk = new RectF32(image.X, image.Y, image.Width, image.Height - th);
                _transportRect = new RectF32(image.X, shrunk.Y + shrunk.Height, image.Width, th);
                image = shrunk;
            }

            _layout = new ViewerLayout(toolbar, fileList, image, infoPanel, statusBar);
        }

        private void ComputeImagePlacement(ViewerState state)
        {
            var area = _layout.ImageArea;
            if (ImageWidth <= 0 || ImageHeight <= 0)
            {
                _placement = new ImagePlacement(area.X, area.Y, 0f, 0f, state.Zoom);
                return;
            }

            var fitScale = MathF.Min(area.Width / ImageWidth, area.Height / ImageHeight);
            if (state.ZoomToFit)
            {
                state.Zoom = fitScale;
            }

            var scale = state.Zoom;
            var drawW = ImageWidth * scale;
            var drawH = ImageHeight * scale;
            var centeredX = area.X + (area.Width - drawW) / 2f;
            var centeredY = area.Y + (area.Height - drawH) / 2f;
            var offsetX = centeredX + state.PanOffset.X;
            var offsetY = centeredY + state.PanOffset.Y;

            // Confine the image to its viewport: zoomed IN (image larger than the area) it must stay covering
            // the area; zoomed OUT (smaller) it must stay fully inside. So a drag can't fling the image off
            // into the chrome / off-screen. Both cases reduce to clamping the top-left into the slack range.
            offsetX = ConfineToViewport(offsetX, area.X, area.Width, drawW);
            offsetY = ConfineToViewport(offsetY, area.Y, area.Height, drawH);

            // Write the clamped position back so a drag held against the edge doesn't accumulate hidden offset
            // (the image would otherwise "stick" until you dragged all the slack back).
            state.PanOffset = (offsetX - centeredX, offsetY - centeredY);
            _placement = new ImagePlacement(offsetX, offsetY, drawW, drawH, scale);
        }

        // Clamp a top-left coordinate so a draw of <paramref name="drawSize"/> stays confined to the viewport
        // axis [areaStart, areaStart + areaSize]: covering it when larger, inside it when smaller.
        private static float ConfineToViewport(float offset, float areaStart, float areaSize, float drawSize)
        {
            var slack = areaSize - drawSize;
            var lo = areaStart + MathF.Min(0f, slack);
            var hi = areaStart + MathF.Max(0f, slack);
            return Math.Clamp(offset, lo, hi);
        }

    }
}
