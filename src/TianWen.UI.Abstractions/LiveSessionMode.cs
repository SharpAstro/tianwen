namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Which mode the live session view is currently displaying. Controls
    /// which UI panels are visible and which background polling is active.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Preview</b> (default) — pre-session: mount manually connected, OTA
    /// frames captured on demand via the equipment hub, target picker open,
    /// no scheduled observations running.
    /// </para>
    /// <para>
    /// <b>Session</b> — a scheduled session is running; the tab shows
    /// guide-graph, V-curve, exposure log, mount + camera telemetry from the
    /// running <c>ISession</c>. Toggled via <see cref="LiveSessionState.IsRunning"/>.
    /// </para>
    /// <para>
    /// <b>PolarAlign</b> — the polar-alignment routine is running on the
    /// manually connected mount. Reuses the image surface, OTA selector, and
    /// WCS pipeline from preview mode but swaps in a polar-specific side
    /// panel + drives the renderer's <see cref="Overlays.WcsAnnotation"/>
    /// with pole/ring overlays. Mutually exclusive with <c>Session</c> mode.
    /// </para>
    /// </remarks>
    public enum LiveSessionMode
    {
        Preview = 0,
        Session = 1,
        PolarAlign = 2,
    }
}
