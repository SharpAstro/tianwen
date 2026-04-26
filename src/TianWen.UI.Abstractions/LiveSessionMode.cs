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

    /// <summary>
    /// Where the polar-alignment routine is in its lifecycle. Drives the
    /// status line and the side-panel button states (Start/Cancel/Done).
    /// </summary>
    public enum PolarAlignmentPhase
    {
        /// <summary>Mode active but routine not yet started; waiting for the user to click Start.</summary>
        Idle = 0,
        /// <summary>Adaptive exposure ramp running on frame 1.</summary>
        ProbingExposure = 1,
        /// <summary>Frame 1 capture complete, mount rotating about the RA axis.</summary>
        Rotating = 2,
        /// <summary>Mount settle wait + frame 2 capture (with retries).</summary>
        Frame2 = 3,
        /// <summary>Phase A solve complete; live refinement loop running.</summary>
        Refining = 4,
        /// <summary>Both <c>IsSettled</c> and <c>IsAligned</c> true; awaiting the user's Done click.</summary>
        Aligned = 5,
        /// <summary>Reverse-axis restore in progress after Done / Cancel.</summary>
        RestoringMount = 6,
        /// <summary>Routine failed (Phase A) — last <see cref="LiveSessionState.PolarStatusMessage"/> contains the reason.</summary>
        Failed = 7,
    }
}
