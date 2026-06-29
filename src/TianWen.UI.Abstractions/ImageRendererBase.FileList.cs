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
            var visibleCount = (int)((listHeight - (y - listTop)) / itemHeight);
            var mouseX = state.MouseScreenPosition.X;
            var mouseY = state.MouseScreenPosition.Y;

            for (var i = 0; i < visibleCount && i + state.FileListScrollOffset < state.ImageFileNames.Count; i++)
            {
                var fileIndex = i + state.FileListScrollOffset;
                var fileName = state.ImageFileNames[fileIndex];
                var itemY = y + i * itemHeight;

                var isSelected = fileIndex == state.SelectedFileIndex;
                // Suppress hover highlight while a dropdown overlay is open: the pointer is captured
                // by the dropdown, so the list underneath must not react to it. Selection (the loaded
                // file) is NOT gated -- it should stay highlighted regardless.
                var isHovered = !state.ToolbarDropdown.IsOpen
                    && mouseX >= lx && mouseX < lx + FileListWidth
                    && mouseY >= itemY && mouseY < itemY + itemHeight;

                if (isSelected)
                {
                    FillRect(lx + 2, itemY, FileListWidth - 4, itemHeight, ViewerTheme.Palette.Selection);
                }
                else if (isHovered)
                {
                    FillRect(lx + 2, itemY, FileListWidth - 4, itemHeight, FileListHoverBg);
                }

                var maxChars = (int)((FileListWidth - PanelPadding * 2) / (FontSize * 0.6f));
                var displayName = fileName.Length > maxChars ? fileName[..(maxChars - 2)] + ".." : fileName;

                DrawText(displayName, lx + PanelPadding, itemY + 2f, FontSize,
                    isSelected ? FileListItemTextSelected : FileListItemText);

                RegisterClickable(lx, itemY, FileListWidth, itemHeight, new HitResult.ListItemHit("FileList", fileIndex));
            }

            if (state.ImageFileNames.Count > visibleCount)
            {
                var scrollFraction = (float)state.FileListScrollOffset / Math.Max(1, state.ImageFileNames.Count - visibleCount);
                var scrollBarH = Math.Max(20f, listHeight * visibleCount / state.ImageFileNames.Count);
                var scrollBarY = listTop + scrollFraction * (listHeight - scrollBarH);
                FillRect(lx + FileListWidth - 4, scrollBarY, 3, scrollBarH, ScrollBarColor);
            }

            // The resize divider between the file list and the content area is now the Split's
            // draw==hit divider node, painted once in Render() from the single layout pass -- no
            // more hand-rolled FillRect handle + widened RegisterClickable straddling the boundary.
        }

        /// <summary>
        /// Hit-tests the file list sidebar and returns the file index, or -1.
        /// </summary>
        public int HitTestFileList(float screenX, float screenY, ViewerState state)
        {
            var fl = _layout.FileList;
            if (!state.ShowFileList || screenX < fl.X || screenX >= fl.X + FileListWidth)
            {
                return -1;
            }

            var listTop = fl.Y;
            var headerOffset = PanelPadding + FontSize + 4f + 3f;
            var itemHeight = FontSize + 4f;
            var relY = screenY - listTop - headerOffset;

            if (relY < 0)
            {
                return -1;
            }

            var itemIndex = (int)(relY / itemHeight) + state.FileListScrollOffset;
            if (itemIndex >= 0 && itemIndex < state.ImageFileNames.Count)
            {
                return itemIndex;
            }

            return -1;
        }

    }
}
