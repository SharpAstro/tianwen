using System;
using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Vertical scrollbar helper for pixel-based lists. Encapsulates the three bits
    /// every scrollable list needs and nothing more:
    ///
    /// <list type="bullet">
    /// <item><see cref="Width"/> / <see cref="ContentWidth"/> — reserve the right-edge column.</item>
    /// <item><see cref="HandleWheel"/> — clamp a wheel delta to a new row-indexed offset.</item>
    /// <item><see cref="Draw"/> — paint the track + thumb (no-op when the list fits).</item>
    /// </list>
    ///
    /// Callers pass their widget's protected <c>FillRect</c> as the <paramref name="fillRect"/>
    /// delegate since <c>PixelWidgetBase&lt;T&gt;.FillRect</c> lives behind <c>protected</c>
    /// in DIR.Lib. Per-frame method-group conversion is trivial.
    ///
    /// Row-indexed only — no smooth sub-row scrolling, matching the terminal-side
    /// <c>Console.Lib.ScrollableList&lt;T&gt;</c> behavior.
    /// </summary>
    /// <summary>
    /// Per-scrollbar drag state. The widget owning a scrollbar stores one of these and
    /// passes it by <c>ref</c> to <see cref="ScrollBar.HandleMouseDown"/> and
    /// <see cref="ScrollBar.HandleMouseUp"/>. Struct copy semantics are fine because
    /// each scrollbar has exactly one drag-in-flight at a time.
    /// </summary>
    public struct ScrollBarDragState
    {
        public bool IsDragging;
        public float GripOffsetY;
    }

    public static class ScrollBar
    {
        public const float BaseWidth      = 6f;
        public const float MinThumbHeight = 20f;
        public const int   WheelStep      = 3;

        public static readonly RGBAColor32 DefaultTrackColor = new RGBAColor32(0x22, 0x22, 0x2a, 0xff);
        public static readonly RGBAColor32 DefaultThumbColor = new RGBAColor32(0x44, 0x44, 0x55, 0xff);

        /// <summary>Pixel width of the scrollbar column at the given DPI.</summary>
        public static float Width(float dpiScale) => BaseWidth * dpiScale;

        /// <summary>
        /// Width available for row content after reserving the scrollbar column.
        /// Returns <paramref name="totalWidth"/> unchanged when the list fits.
        /// </summary>
        public static float ContentWidth(float totalWidth, int totalItems, int visibleRows, float dpiScale)
            => totalItems > visibleRows ? totalWidth - Width(dpiScale) : totalWidth;

        /// <summary>
        /// Clamp a wheel delta into a new row-indexed scroll offset.
        /// Positive <paramref name="scrollY"/> scrolls up (toward the list start).
        /// </summary>
        public static int HandleWheel(float scrollY, int offset, int totalItems, int visibleRows)
        {
            var maxOffset = Math.Max(0, totalItems - visibleRows);
            return Math.Clamp(offset - (int)scrollY * WheelStep, 0, maxOffset);
        }

        /// <summary>
        /// Dispatch a mouse-down event against the scrollbar geometry. Returns:
        /// <list type="bullet">
        /// <item><c>null</c> when the click was not inside the scrollbar column — caller continues hit-testing.</item>
        /// <item>a new offset when the click was on the track above/below the thumb (page up / page down).</item>
        /// <item>the current <paramref name="offset"/> unchanged when the click started a drag on the thumb
        ///       (<see cref="ScrollBarDragState.IsDragging"/> becomes true).</item>
        /// </list>
        /// </summary>
        public static int? HandleMouseDown(
            ref ScrollBarDragState state,
            float mouseX, float mouseY,
            float trackX, float trackY, float trackH,
            int totalItems, int visibleRows, int offset, float dpiScale)
        {
            if (totalItems <= visibleRows) return null;
            var width = Width(dpiScale);
            if (mouseX < trackX || mouseX >= trackX + width) return null;
            if (mouseY < trackY || mouseY >= trackY + trackH) return null;

            var maxOffset = Math.Max(1, totalItems - visibleRows);
            var thumbH = Math.Max(MinThumbHeight * dpiScale, trackH * visibleRows / (float)totalItems);
            var thumbY = trackY + (trackH - thumbH) * offset / (float)maxOffset;

            if (mouseY >= thumbY && mouseY < thumbY + thumbH)
            {
                // Grabbed the thumb — remember where inside the thumb the click landed so
                // drag updates preserve the same relative grip point.
                state.IsDragging = true;
                state.GripOffsetY = mouseY - thumbY;
                return offset;
            }

            // Track click above or below the thumb → page by one viewport.
            return mouseY < thumbY
                ? Math.Clamp(offset - visibleRows, 0, maxOffset)
                : Math.Clamp(offset + visibleRows, 0, maxOffset);
        }

        /// <summary>
        /// Dispatch a mouse-move event while a drag is in progress. Returns the new offset
        /// or <c>null</c> when not dragging. Y can be outside the track — by desktop
        /// scrollbar convention, drag continues while the button is held.
        /// </summary>
        public static int? HandleMouseMove(
            in ScrollBarDragState state,
            float mouseY,
            float trackY, float trackH,
            int totalItems, int visibleRows, float dpiScale)
        {
            if (!state.IsDragging) return null;
            var maxOffset = Math.Max(1, totalItems - visibleRows);
            var thumbH = Math.Max(MinThumbHeight * dpiScale, trackH * visibleRows / (float)totalItems);
            var trackUsable = Math.Max(1f, trackH - thumbH);

            var targetThumbY = mouseY - state.GripOffsetY;
            var normalized = Math.Clamp((targetThumbY - trackY) / trackUsable, 0f, 1f);
            return (int)MathF.Round(normalized * maxOffset);
        }

        /// <summary>Ends a drag. Returns whether we were dragging (caller may want to mark a redraw).</summary>
        public static bool HandleMouseUp(ref ScrollBarDragState state)
        {
            var was = state.IsDragging;
            state.IsDragging = false;
            return was;
        }

        /// <summary>
        /// Paint the scrollbar track + thumb at the right edge of a list region.
        /// No-op when <paramref name="totalItems"/> <c>&lt;=</c> <paramref name="visibleRows"/>.
        /// </summary>
        /// <param name="fillRect">Widget's <c>FillRect</c> (typically passed as a method group).</param>
        /// <param name="trackX">X coordinate of the scrollbar column (right edge of list).</param>
        /// <param name="trackY">Y coordinate of the list top.</param>
        /// <param name="trackH">Pixel height of the list region.</param>
        public static void Draw(
            Action<float, float, float, float, RGBAColor32> fillRect,
            float trackX, float trackY, float trackH,
            int totalItems, int visibleRows, int offset, float dpiScale,
            RGBAColor32? track = null, RGBAColor32? thumb = null)
        {
            if (totalItems <= visibleRows) return;

            var width     = Width(dpiScale);
            var maxOffset = Math.Max(1, totalItems - visibleRows);

            fillRect(trackX, trackY, width, trackH, track ?? DefaultTrackColor);

            var thumbH = Math.Max(MinThumbHeight * dpiScale, trackH * visibleRows / (float)totalItems);
            var thumbY = trackY + (trackH - thumbH) * offset / (float)maxOffset;
            fillRect(trackX + 1f, thumbY, width - 2f, thumbH, thumb ?? DefaultThumbColor);
        }
    }
}
