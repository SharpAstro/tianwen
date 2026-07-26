namespace TianWen.Lib.Sequencing
{
    /// <summary>
    /// The per-OTA facts a UI needs to <b>render</b> an observed session's telescope column: the camera
    /// label plus which optional devices are fitted, so the focuser / filter rows can be gated without
    /// reaching into <see cref="Setup"/>'s live driver graph.
    /// <para>
    /// Exists so <see cref="ISessionTelemetry"/> stays wire-crossable (docs/plans/remote-profile.md P3):
    /// a remote mirror can supply names and presence flags, but never a <see cref="Telescope"/> with real
    /// drivers hanging off it. Add display facts here rather than widening telemetry back to
    /// <c>Setup</c>.
    /// </para>
    /// </summary>
    /// <param name="CameraName">Display name of the OTA's camera -- the column header.</param>
    /// <param name="HasFocuser">Whether a focuser is fitted (gates the focus position / temperature row).</param>
    /// <param name="HasFilterWheel">Whether a filter wheel is fitted (gates the filter row).</param>
    public readonly record struct TelescopeDisplayInfo(
        string CameraName,
        bool HasFocuser,
        bool HasFilterWheel);
}
