using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
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
        /// <summary>
        /// Region id for the file-list rows, shared by the registration and the press handler's
        /// fall-through so the two cannot disagree about which regions are rows.
        /// </summary>
        /// <summary>
        /// Region id for the file-list rows, shared by the registration, the press handlers' fall-through
        /// and the hover tracking, so none of them can disagree about which regions are rows.
        ///
        /// PUBLIC because a host may route its own presses -- the standalone viewer's Program.cs does --
        /// and it has to be able to recognise a row hit in order to let it through to the scroll
        /// controller. There are two press dispatchers over this widget and both must exclude rows.
        /// </summary>
        public const string FileListId = "FileList";

        /// <summary>The header's pseudo-row index, so it shares the rows' hit shape.</summary>
        public const int HeaderRowIndex = -1;

        /// <summary>Extra vertical room per file row, on top of the font size.</summary>
        private const float FileListRowLeading = 9f;

        private readonly ListScrollController _fileListScroll =
            new ListScrollController { SnapToAtom = true, Mode = ScrollBarMode.Interactive };

        /// <summary>
        /// File-list scroll surface for hosts with bespoke mouse dispatch (the standalone viewer's
        /// <c>Program.cs</c> mouse-down handler); the embedded <see cref="HandleInput"/> path routes
        /// internally. Viewport-gated by the controller -- returns <c>false</c> for presses elsewhere.
        /// </summary>
        public bool HandleFileListInput(InputEvent evt) => _fileListScroll.HandleInput(evt);

        /// <summary>
        /// The pane header: <c>Files</c>, plus the containing folder in brackets when there is room for
        /// it, with the full path as a hover tooltip.
        ///
        /// The folder earns its place because every name in the list below is a sibling -- the one thing
        /// the rows cannot tell you is WHICH folder you are looking at, and a viewer that rebinds on
        /// open/drop can change that underneath you. Only the leaf name is shown: the full path is
        /// routinely longer than the pane is wide, so it would be ellipsised down to nothing useful,
        /// which is what the tooltip is for instead.
        /// </summary>
        private void RenderFileListHeader(ViewerState state, float lx, float y, float height)
        {
            const string title = "Files";
            var titleX = lx + PanelPadding;
            DrawText(title, titleX, y, FontSize, ViewerTheme.Palette.HeaderText);

            if (state.CurrentFolder is not { Length: > 0 } folder)
            {
                return;
            }

            var trimmed = Path.TrimEndingDirectorySeparator(folder);
            var leaf = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(leaf))
            {
                // A drive root has no leaf; the path IS the label.
                leaf = trimmed;
            }

            // Prefer parent/leaf, fall back to leaf alone. One folder name is often not enough to place
            // it -- 'My' says nothing, 'Astro/My' does -- and the pane is usually wide enough for both,
            // so showing only the leaf wastes the room it has. Forward slash regardless of platform:
            // this is a label, not a path to hand back to the filesystem.
            var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
            var withParent = string.IsNullOrEmpty(parent) ? leaf : parent + "/" + leaf;

            // Whatever is left of the pane after the title, in the DIM chrome colour: the folder is
            // context, and it must not compete with the file names it sits above.
            var suffixX = titleX + MeasureText(title, FontSize) + FontSize * 0.4f;
            var available = lx + FileListWidth - PanelPadding - suffixX;
            if (available <= FontSize)
            {
                // Narrower than a single glyph: drawing '(...)' here would be chrome with no content.
                return;
            }

            // Widest candidate that fits, then ellipsise the shortest rather than the longest -- an
            // ellipsised 'Astro/My' would read worse than a complete 'My'.
            var suffix = $"({withParent})";
            if (MeasureText(suffix, FontSize) > available)
            {
                suffix = $"({leaf})";
                if (MeasureText(suffix, FontSize) > available)
                {
                    suffix = TextFit.TrimToWidth(Renderer, suffix, FontPath, FontFallback, FontSize,
                        available, TextTrim.End);
                }
            }
            DrawText(suffix, suffixX, y, FontSize, ViewerTheme.Palette.DimText);

            // The tooltip is the FULL path, and it is offered whenever the pointer is over the header --
            // not only when the leaf was trimmed. The leaf being legible does not tell you where it is,
            // which is the question the header raises.
            var mouse = state.MouseScreenPosition;
            var headerRect = new RectF32(lx, y, FileListWidth, height);
            if (!state.OverlayOwnsPointer && headerRect.Contains(mouse.X, mouse.Y))
            {
                _hoveredTooltip = (folder, lx, y, height);
            }

            // Registered as the same list with index -1 rather than as bespoke chrome, so the hover
            // tracking in HandleViewerMouseMove can recognise the header with the one pattern it
            // already matches for rows. No OnClick, same as a row: the header does nothing on click.
            RegisterClickable(headerRect.X, headerRect.Y, headerRect.Width, headerRect.Height,
                new HitResult.ListItemHit(FileListId, HeaderRowIndex), onClick: null,
                cursor: CursorKind.Default);
        }

        /// <summary>
        /// Where a row's label starts vertically, centring the text's line box in the row.
        /// <see cref="DrawText(string, float, float, float, RGBAColor32)"/> top-aligns within a line box
        /// of <c>fontSize * 1.3</c>, so centring the ROW means centring that box, not the font size.
        ///
        /// Shared with the hover tooltip, which draws the untruncated name over the row: the two must
        /// use one expression or the revealed text sits a pixel or two off and its underscores double up
        /// against the row beneath.
        /// </summary>
        internal float RowTextY(float rowY, float rowHeight) => rowY + (rowHeight - FontSize * 1.3f) * 0.5f;

        private void RenderFileList(ViewerState state)
        {
            // Pane geometry from the single arranged layout (the Split's first pane), not re-derived from
            // the outer toolbar/status heights.
            var fl = _layout.FileList;
            var lx = fl.X;
            var listTop = fl.Y;
            var listHeight = fl.Height;

            FillRect(lx, listTop, FileListWidth, listHeight, ViewerTheme.FileListBg);

            if (string.IsNullOrEmpty(FontPath))
            {
                return;
            }

            var y = listTop + PanelPadding;
            var headerHeight = FontSize + 4f;
            RenderFileListHeader(state, lx, y, headerHeight);
            y += headerHeight;

            FillRect(lx + PanelPadding, y, FileListWidth - PanelPadding * 2, 1, ViewerTheme.Palette.Separator);
            y += 3f;

            // Rows were FontSize + 4, which left the names looking packed -- the descenders of one
            // row nearly touching the caps of the next. The text offset below is DERIVED from this
            // height rather than fixed, so changing the spacing keeps the label centred.
            var itemHeight = FontSize + FileListRowLeading;

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
                // Selection (the loaded file) is deliberately NOT gated on the overlay -- it should stay
                // highlighted regardless; only hover is, and why is stated on OverlayOwnsPointer.
                var isHovered = !state.OverlayOwnsPointer && rowRect.Contains(mouseX, mouseY);

                if (isSelected)
                {
                    FillRect(rowRect.X + 2, rowRect.Y, rowRect.Width - 4, rowRect.Height, ViewerTheme.Palette.Selection);
                }
                else if (isHovered)
                {
                    FillRect(rowRect.X + 2, rowRect.Y, rowRect.Width - 4, rowRect.Height, FileListHoverBg);
                }

                // MEASURED, not estimated. This was an assumed 0.6-em advance, which over-trims a
                // narrow-glyph name and lets a wide one overflow the pane -- and DrawText does not
                // bound a run to its rect, so the overflow paints over the image. TextFit is the
                // shared implementation and measures through the fallback chain, so a name needing a
                // fallback face is cut at the right length rather than against the wrong font.
                var textWidth = rowRect.Width - PanelPadding * 2f;
                var displayName = TextFit.TrimToWidth(
                    Renderer, fileName, FontPath, FontFallback, FontSize, textWidth, TextTrim.End);

                DrawText(displayName, rowRect.X + PanelPadding, RowTextY(rowRect.Y, rowRect.Height), FontSize,
                    isSelected ? FileListItemTextSelected : FileListItemText);

                // A DECLARED region per row. Deliberately with no OnClick: the press/release still
                // goes to the scroll controller, which is what makes drag-to-scroll work and fires
                // selection on the tap RELEASE. Registering an OnClick here would select on PRESS and
                // a touch drag would open whatever row the finger started on.
                //
                // So what the region is FOR is everything a geometry-only row could not do: it puts
                // the row in the region tree (so the inspector can see and drive it -- click_label on
                // a file name works, which is the acceptance test this pane used to fail), and it
                // states the cursor instead of leaving the query to fall through to the pane beneath.
                // ListItemHit and NOT a bespoke hit type, for a reason worth recording: the inspector
                // derives a region's label from the hit, and it only labels ButtonHit, ListItemHit and
                // SliderHit -- an app-specific subtype reports its type name and a NULL label. A custom
                // type therefore bought tidiness at the cost of the visibility this registration exists
                // to provide. ButtonHit is not an option either: the press handler runs every ButtonHit
                // whose action parses as a ToolbarAction, and a file can be named anything.
                //
                // NO OnClick, deliberately. The press must reach the scroll controller below or
                // drag-to-scroll dies and nothing selects at all -- selection is taken on the tap
                // RELEASE. An OnClick here would fire on PRESS, so a drag would open whichever row the
                // pointer started on. The press handler matches this hit by ListId to fall through.
                RegisterClickable(rowRect.X, rowRect.Y, rowRect.Width, rowRect.Height,
                    new HitResult.ListItemHit(FileListId, fileIndex), onClick: null, cursor: CursorKind.Default);

                // Only when the name is actually cut -- a tooltip repeating a fully visible label is
                // noise. Anchored on the row so it appears where the pointer is.
                if (isHovered && !string.Equals(displayName, fileName, StringComparison.Ordinal))
                {
                    _hoveredTooltip = (fileName, rowRect.X, rowRect.Y, rowRect.Height);
                }
            }

            // Interactive scrollbar (grabbable thumb; no-op when the list fits).
            _fileListScroll.DrawScrollBar(FillRect);

            // The resize divider between the file list and the content area is the Split's draw==hit divider
            // node, painted once in Render() from the single layout pass.
        }
    }
}
