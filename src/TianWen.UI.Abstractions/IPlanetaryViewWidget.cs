using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Seam for hosting the full planetary capture view (the shared image viewer + capture-control strip)
    /// inside the renderer-agnostic <c>LiveSessionTab</c>, the same way <see cref="IMiniViewerWidget"/> is
    /// injected for the preview/polar mini viewer. In <see cref="LiveSessionMode.Planetary"/> the Live
    /// Session screen renders this instead of the mini viewer and forwards mouse input to it.
    /// <para>
    /// The Vulkan implementation (<c>VkPlanetaryTab</c>) is the full <c>VkImageRenderer</c> + capture strip;
    /// it does its own position-aware hit dispatch (toolbar / wavelet + WB sliders / Start-Stop), so the host
    /// forwards raw mouse events to <see cref="DIR.Lib.IWidget.HandleInput"/> rather than going through the
    /// host's per-region OnClick path.
    /// </para>
    /// </summary>
    public interface IPlanetaryViewWidget
    {
        /// <summary>
        /// Renders the planetary capture view (viewer + control strip) into <paramref name="contentRect"/>.
        /// The <see cref="ViewerState"/> is taken from <paramref name="controller"/> (the DI-singleton the
        /// capture loop shares), so wavelet/stretch changes round-trip to the live stack.
        /// </summary>
        void RenderPlanetary(PlanetaryCaptureController? controller, RectF32 contentRect, float dpiScale, string fontPath);

        /// <summary>Forwards a raw input event to the view's own hit dispatch (toolbar / sliders / strip).</summary>
        bool HandleInput(InputEvent evt);
    }
}
