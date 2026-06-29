using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Seam for hosting the full planetary capture view (the shared image viewer + capture-control strip)
    /// inside the renderer-agnostic <c>LiveSessionTab</c>, the same way the chromeless preview viewer
    /// (<see cref="ImageRendererBase{TSurface}"/>) is injected for the preview/polar modes. In
    /// <see cref="LiveSessionMode.Planetary"/> the Live Session screen renders this instead of the preview
    /// viewer and forwards mouse input to it.
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
        /// Renders the planetary capture view (left control panel + the shared image viewer) into
        /// <paramref name="contentRect"/>. The <see cref="ViewerState"/> is taken from
        /// <paramref name="controller"/> (the DI-singleton the capture loop shares), so wavelet/stretch
        /// changes round-trip to the live stack. <paramref name="focuser"/> is the active OTA's focuser
        /// telemetry (<see cref="PreviewOTATelemetry.Unknown"/> when none), driving the panel's focuser
        /// readout + jog row -- the jog buttons post the same <c>JogFocuserSignal</c> the Live Session OTA
        /// panel uses (one focuser-control path, shared via the signal).
        /// </summary>
        void RenderPlanetary(PlanetaryCaptureController? controller, PreviewOTATelemetry focuser,
            RectF32 contentRect, float dpiScale, string fontPath);

        /// <summary>Forwards a raw input event to the view's own hit dispatch (toolbar / sliders / strip).</summary>
        bool HandleInput(InputEvent evt);
    }
}
