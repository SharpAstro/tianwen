using System;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    partial class ImageRendererBase<TSurface>
    {
        /// <summary>
        /// Writes text to the system clipboard. Set by the host, because a clipboard is a platform
        /// service (SDL in the desktop hosts, the browser's async API in the web one) and this
        /// assembly has no platform.
        /// <para>
        /// A null callback is not an error: the copy items are still offered and still report what
        /// they resolved in the status bar, which is the half a user can read back to you. Hiding
        /// them instead would make a host that merely forgot the wiring look like a build without
        /// the feature.
        /// </para>
        /// </summary>
        public Action<string>? SetClipboardText { get; set; }

        /// <summary>
        /// Right-click on the image: the values already under the cursor, as text one can paste.
        /// </summary>
        /// <remarks>
        /// <para><b>It copies what the readout shows, and computes nothing.</b> Every mouse move
        /// already resolves <see cref="ViewerState.CursorPixelInfo"/> -- position, per-channel sample,
        /// and RA/Dec where the document has a WCS -- so this is a formatter over state that exists.
        /// The DISPLAYED colour (the post-stretch hex) is deliberately not offered: it is a different
        /// number, the GPU owns it, and recomputing it on the CPU is a feature of its own rather than
        /// a menu item.</para>
        /// <para><b>Why a menu rather than modifier-clicks.</b> A chord has to be documented somewhere
        /// to be discovered, and the only place it could be is the panel nobody opens until something
        /// is already wrong. The menu also has somewhere to grow -- a share link needs a home.</para>
        /// <para><b>It reuses the toolbar's dropdown</b> rather than adding a second overlay type:
        /// <see cref="ViewerState.ToolbarDropdown"/> already owns keyboard claim, hover highlight,
        /// scrolling, dismissal and -- through <see cref="ViewerState.OverlayOwnsPointer"/> -- the
        /// z-order answer for hover. A parallel menu would need all of that again.</para>
        /// <para><b>Both press dispatchers must call this.</b> The viewer has two (the standalone
        /// host's own, and <c>HandleViewerMouseDown</c> for the embedded case); wiring only one of
        /// them is the split that left single-click selection broken in the standalone host.</para>
        /// </remarks>
        /// <returns><see langword="true"/> when a menu was opened, so the caller stops treating the
        /// press as the start of a pan.</returns>
        public bool TryOpenImageContextMenu(ViewerState state, float px, float py)
        {
            // Inside the image viewport only: a right-click on the toolbar reverse-cycles a button,
            // and one on the file list or a panel belongs to whatever is there. The same arranged rect
            // the pan gesture tests against.
            var area = _layout.ImageArea;
            if (px < area.X || px >= area.X + area.Width || py < area.Y || py >= area.Y + area.Height)
            {
                return false;
            }

            var items = BuildContextMenuItems(state, px, py);
            if (items.IsEmpty)
            {
                return false;
            }

            // A zero-size anchor at the cursor: OpenDropdown places the menu at bounds.Y + Height and
            // widens it to the longest label, so its top-left lands under the pointer while ClampX
            // keeps it on screen near the right edge.
            OpenDropdown(
                state,
                new RectF32(new Vector2(px, py), Vector2.Zero),
                items.Select(static i => i.Label).ToImmutableArray(),
                (index, _) =>
                {
                    if ((uint)index < (uint)items.Length)
                    {
                        CopyToClipboard(state, items[index].Description, items[index].Payload);
                    }
                });
            return true;
        }

        /// <summary>
        /// Resolves the pixel under the cursor and asks <see cref="ImageContextMenu"/> for its items.
        /// Payloads are therefore fixed WHEN THE MENU OPENS, never when an item is picked: the pointer
        /// has to move to reach an item, so a payload read on selection would describe the pixel under
        /// the menu instead of the one that was right-clicked.
        /// </summary>
        private ImmutableArray<ImageContextMenuItem> BuildContextMenuItems(ViewerState state, float px, float py)
        {
            var area = _layout.ImageArea;

            // Resolve the pixel from THIS press rather than trusting the last mouse-move to have left
            // one: a press is not always preceded by a move over the image (a synthesized click, a
            // touch tap, a window that just took focus under the pointer), and a menu that silently
            // fails to open in those cases is indistinguishable from the feature being absent.
            // Through the same converter the readout uses, so there is no second copy of the zoom and
            // pan arithmetic.
            ViewerActions.UpdateCursorFromScreenPosition(
                _document, state, px, py, area.X, area.Y, area.Width, area.Height);

            // Ask for EVERY channel where a document can answer: the per-move readout samples only the
            // channel on screen (deliberately -- it runs on every mouse move over a large master), and
            // a copied value naming one of three channels is the ambiguity this avoids. One call per
            // right-click, so the reason for that thrift does not apply here.
            var info = _document is { } document && state.CursorImagePosition is { } at
                ? document.GetPixelInfo(at.X, at.Y)
                : state.CursorPixelInfo;

            if (info is not { } pixel)
            {
                return ImmutableArray<ImageContextMenuItem>.Empty;
            }

            var image = _document?.UnstretchedImage;
            var fovDeg = image is { } img
                ? SkyAtlasLink.FieldOfViewDeg(_document?.Wcs, img.Width, img.Height)
                : null;

            return ImageContextMenu.ItemsFor(pixel, fovDeg, image?.ImageMeta.ExposureStartTime);
        }

        private void CopyToClipboard(ViewerState state, string description, string payload)
        {
            if (SetClipboardText is { } setter)
            {
                setter(payload);
                state.StatusMessage = $"Copied {description}";
            }
            else
            {
                // No clipboard on this host: still say what it was, so the value is at least readable.
                state.StatusMessage = $"{description}: {payload.Replace('\n', ' ')}";
            }
            state.NeedsRedraw = true;
        }
    }
}
