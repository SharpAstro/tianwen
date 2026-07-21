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
        // File list sidebar
        // -----------------------------------------------------------------------

        // File-list scroll controller (DIR.Lib atom model): row-snapped, fully interactive. It owns the
        // continuous scroll offset (so the trackpad wheel accumulator survives frame-to-frame) + the
        // correct Count-visible bound; ScanFolder requests an initial top via
        // ViewerState.PendingFileListScrollTop, applied once below. Rows are NOT registered clickables:
        // both viewer hosts route the press/move/release to the controller (the embedded path via
        // HandleViewerMouse*, the standalone via HandleFileListInput), so drag-to-scroll and the
        // grabbable thumb work, and select fires on the tap RELEASE (TakeAtomTap) like Planner/Equipment
        // -- a touch drag over the list scrolls it instead of selecting the row under the finger.
        private readonly ListScrollController _fileListScroll =
            new ListScrollController { SnapToAtom = true, Mode = ScrollBarMode.Interactive };

        /// <summary>
        /// File-list scroll surface for hosts with bespoke mouse dispatch (the standalone viewer's
        /// <c>Program.cs</c> mouse-down handler); the embedded <see cref="HandleInput"/> path routes
        /// internally. Viewport-gated by the controller -- returns <c>false</c> for presses elsewhere.
        /// </summary>
        public bool HandleFileListInput(InputEvent evt) => _fileListScroll.HandleInput(evt);

        private void RenderFileList(ViewerState state)
        {
            // Pane geometry from the single arranged layout (the Split's first pane), not re-derived from
            // the outer toolbar/status heights.
            var fl = _layout.FileList;
            var lx = fl.X;
            var listTop = fl.Y;
            var listHeight = fl.Height;

            FillRect(lx, listTop, FileListWidth, listHeight, ViewerTheme.FileListBg);

            if (_fontPath is null)
            {
                return;
            }

            var y = listTop + PanelPadding;
            DrawText("Files", lx + PanelPadding, y, FontSize, ViewerTheme.Palette.HeaderText);
            y += FontSize + 4f;

            FillRect(lx + PanelPadding, y, FileListWidth - PanelPadding * 2, 1, ViewerTheme.Palette.Separator);
            y += 3f;

            var itemHeight = FontSize + 4f;

            // Hand the controller this frame's geometry (viewport = the items area below the header, one atom
            // = one file row); it owns the offset + wheel/drag/thumb math and reserves the scrollbar column,
            // and VisibleRows() owns row placement + the overflow cutoff (fixing the old Count-1 bound).
            var itemsRect = new RectF32(lx, y, FileListWidth, listTop + listHeight - y);
            _fileListScroll.SetExtent(itemsRect, itemHeight, state.ImageFileNames.Count, DpiScale);

            // Apply ScanFolder's one-shot requested top (clamped to the current geometry), then clear it. This
            // is a single jump, never a per-frame write, so it does not reset the controller's fractional offset.
            if (state.PendingFileListScrollTop is { } top)
            {
                _fileListScroll.AtomOffset = top;
                state.PendingFileListScrollTop = null;
            }

            var mouseX = state.MouseScreenPosition.X;
            var mouseY = state.MouseScreenPosition.Y;

            foreach (var (fileIndex, rowRect) in _fileListScroll.VisibleRows())
            {
                var fileName = state.ImageFileNames[fileIndex];

                var isSelected = fileIndex == state.SelectedFileIndex;
                // Suppress hover highlight while a dropdown overlay is open: the pointer is captured
                // by the dropdown, so the list underneath must not react to it. Selection (the loaded
                // file) is NOT gated -- it should stay highlighted regardless.
                var isHovered = !state.ToolbarDropdown.IsOpen && rowRect.Contains(mouseX, mouseY);

                if (isSelected)
                {
                    FillRect(rowRect.X + 2, rowRect.Y, rowRect.Width - 4, rowRect.Height, ViewerTheme.Palette.Selection);
                }
                else if (isHovered)
                {
                    FillRect(rowRect.X + 2, rowRect.Y, rowRect.Width - 4, rowRect.Height, FileListHoverBg);
                }

                var maxChars = (int)((rowRect.Width - PanelPadding * 2) / (FontSize * 0.6f));
                var displayName = fileName.Length > maxChars ? fileName[..(maxChars - 2)] + ".." : fileName;

                DrawText(displayName, rowRect.X + PanelPadding, rowRect.Y + 2f, FontSize,
                    isSelected ? FileListItemTextSelected : FileListItemText);
            }

            // Interactive scrollbar (grabbable thumb; no-op when the list fits).
            _fileListScroll.DrawScrollBar(FillRect);

            // The resize divider between the file list and the content area is the Split's draw==hit divider
            // node, painted once in Render() from the single layout pass.
        }
    }
}
