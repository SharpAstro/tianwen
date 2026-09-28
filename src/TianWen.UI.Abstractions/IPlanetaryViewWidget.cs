using DIR.Lib;

namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Seam for hosting the full planetary capture view (the shared image viewer + capture-control strip)
    /// inside the renderer-agnostic <c>LiveSessionTab</c>, the same way the chromeless preview viewer
    /// (<see cref="ImageRendererBase{TSurface}"/>) is injected for the preview/polar modes. In
    /// <see cref="LiveSessionMode.Planetary"/> the Live Session screen renders this instead of the preview
    /// viewer.
    /// <para>
    /// The Vulkan implementation (<c>VkPlanetaryTab</c>) is the full <c>VkImageRenderer</c> + capture strip, and
    /// its controls (the toolbar, the sliders, Start/Stop, the steppers) are regions it registers on ITSELF. The
    /// window's router reaches them only through a composite that lists the view as a child, which is why the view
    /// is handed over as a <see cref="Widget"/>: the Live Session tab lists it (see its <c>Children</c>), and only a
    /// press no region claimed is forwarded to the view's own <see cref="IWidget.HandleInput"/> (a pan, the PiP drag).
    /// </para>
    /// </summary>
    public interface IPlanetaryViewWidget<TSurface>
    {
        /// <summary>The view as the widget it is, so the tab hosting it can list it as a child.</summary>
        PixelWidgetBase<TSurface> Widget { get; }

        /// <summary>
        /// Renders the planetary capture view (left control panel + the shared image viewer) into
        /// <paramref name="contentRect"/>. The <see cref="ViewerState"/> is taken from
        /// <paramref name="controller"/> (the DI-singleton the capture loop shares), so wavelet/stretch
        /// changes round-trip to the live stack. <paramref name="focuser"/> is the active OTA's focuser
        /// telemetry (<see cref="PreviewOTATelemetry.Unknown"/> when none), driving the panel's focuser
        /// readout + jog row -- the jog buttons post the same <c>JogFocuserSignal</c> the Live Session OTA
        /// panel uses (one focuser-control path, shared via the signal). Font comes from the implementer's
        /// own inherited <c>FontPath</c> (host-set), not a parameter.
        /// </summary>
        void RenderPlanetary(PlanetaryCaptureController? controller, PreviewOTATelemetry focuser,
            RectF32 contentRect);
    }
}
