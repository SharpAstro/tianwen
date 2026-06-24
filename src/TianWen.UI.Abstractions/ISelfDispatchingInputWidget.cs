namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Marker for a widget that performs its <b>own</b> hit-testing and position-aware dispatch inside
    /// <see cref="DIR.Lib.IWidget.HandleInput"/>, rather than relying on the host's
    /// <c>HitTestAndDispatch</c> + per-region <c>OnClick</c> model.
    /// <para>
    /// The shared image viewer (<see cref="ImageRendererBase{TSurface}"/>) is the canonical implementer: its
    /// toolbar buttons cycle/open dropdowns, and its white-balance / wavelet / transport sliders begin a drag
    /// from the press position -- both need the click <c>x</c>/<c>y</c> that an <c>OnClick</c> handler (which
    /// only receives the modifier flags) cannot carry. So the press must reach <c>HandleViewerMouseDown</c>.
    /// </para>
    /// <para>
    /// When the active tab implements this, <c>GuiEventHandlerBase</c> routes the raw mouse press straight to
    /// <c>HandleInput</c> (after the chrome/sidebar gets first crack) instead of pre-dispatching it via
    /// <c>HitTestAndDispatch</c> and short-circuiting. Standalone hosts (e.g. <c>tianwen-fits</c>) already call
    /// <c>HandleInput</c> directly, so the marker is a no-op there.
    /// </para>
    /// </summary>
    public interface ISelfDispatchingInputWidget
    {
    }
}
