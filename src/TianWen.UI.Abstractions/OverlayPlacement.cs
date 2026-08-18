using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Where a near-an-anchor overlay goes: a tooltip, a dropdown, a context menu. Pure geometry, so the
    /// one fiddly part -- keeping the box on screen -- has a single implementation.
    /// </summary>
    /// <remarks>
    /// <para>Only the PLACEMENT is shared, not the painting: a tooltip is two filled rects and a text run,
    /// and passing three draw delegates around to save three lines would cost more than it removes. What
    /// must not be duplicated is the clamping, because the case it handles is invisible until it bites --
    /// the rightmost button in a bar, whose tooltip runs off the edge and reads as the tooltip being
    /// broken rather than as being clipped.</para>
    /// <para>DIR.Lib already carries the other half of this: <c>TabItem.Tooltip</c> and
    /// <c>DropdownItem.Tooltip</c> declare the TEXT beside the item, and deliberately do not draw it --
    /// "a tooltip is painted outside the strip, over whatever content is adjacent to it, and a widget that
    /// clips to its own bounds cannot". This is the host's side of that contract.</para>
    /// </remarks>
    public static class OverlayPlacement
    {
        /// <summary>Design-unit padding between the box edge and its text.</summary>
        public const float BasePadding = 6f;

        /// <summary>
        /// Clamps a box of the given width so it sits inside the viewport horizontally.
        /// </summary>
        /// <remarks>
        /// <para>The shared half of every anchored overlay, and the reason this is not written per call
        /// site: the case it handles is invisible until it bites, and then it reads as the overlay being
        /// broken rather than as its anchor sitting near an edge. It bit twice -- a tooltip on the
        /// rightmost toolbar button, and then the help dropdown itself, once that button was pinned to
        /// the right edge and its menu (wider than the button) still opened at the button's left x.</para>
        /// <para>A box WIDER than the viewport pins to the left rather than centring on a negative
        /// origin: clipping its right edge loses the end of each line, which is recoverable, while a
        /// negative origin loses the start of every line, which is not.</para>
        /// </remarks>
        public static float ClampX(float x, float width, float viewportWidth)
            => viewportWidth > width ? Math.Clamp(x, 0f, viewportWidth - width) : 0f;

        /// <summary>Clamps a box of the given height so it sits inside the viewport vertically.</summary>
        /// <remarks>Independent of <see cref="ClampX"/> on purpose: a dropdown wants its x kept on
        /// screen but its y left alone under its button, because the menu itself scrolls when it
        /// outgrows the window and lifting it would fight that.</remarks>
        public static float ClampY(float y, float height, float viewportHeight)
            => viewportHeight > height ? Math.Clamp(y, 0f, viewportHeight - height) : 0f;

        /// <summary>Which side of the anchor point the box sits on.</summary>
        public enum Anchor
        {
            /// <summary>Below the anchor: a toolbar button, where the anchor is the button's bottom-left.</summary>
            Below,

            /// <summary>Right of the anchor, vertically centred on it: a vertical rail of tabs.</summary>
            RightOf,
        }

        /// <summary>The placed box plus where its text starts.</summary>
        public readonly record struct Placed(RectF32 Box, float TextX, float Padding);

        /// <summary>
        /// Places a tooltip box of the given text extent, clamped inside the viewport.
        /// </summary>
        public static Placed Place(
            Anchor anchor,
            float anchorX, float anchorY,
            float textWidth, float textHeight,
            float dpiScale,
            float viewportWidth, float viewportHeight)
        {
            var pad = BasePadding * MathF.Max(dpiScale, 0.01f);
            var w = textWidth + pad * 2f;
            var h = textHeight + pad;

            var x = anchorX;
            var y = anchor is Anchor.RightOf ? anchorY - h * 0.5f : anchorY;

            x = ClampX(x, w, viewportWidth);
            y = ClampY(y, h, viewportHeight);

            return new Placed(new RectF32(x, y, w, h), x + pad, pad);
        }
    }
}
