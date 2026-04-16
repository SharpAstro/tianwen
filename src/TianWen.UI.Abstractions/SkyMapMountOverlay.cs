namespace TianWen.UI.Abstractions
{
    /// <summary>
    /// Snapshot of mount pointing for the sky-map reticle overlay. Populated by the
    /// event loop from the polled <c>PreviewMountState</c> (preview mode) or
    /// <c>session.MountState</c> (session running). Coordinates are J2000 — the caller
    /// is responsible for converting native-frame mount reports to J2000 before
    /// writing this snapshot (see <c>IMountDriver.GetRaDecJ2000Async</c>).
    /// </summary>
    /// <param name="RaJ2000">Right ascension in hours, J2000.</param>
    /// <param name="DecJ2000">Declination in degrees, J2000.</param>
    /// <param name="DisplayName">User-facing mount name for the overlay label.</param>
    /// <param name="IsSlewing">True while the mount is actively slewing — the renderer
    /// may choose a pulsing / dashed reticle style to highlight motion.</param>
    /// <param name="IsTracking">True while sidereal tracking is engaged.</param>
    /// <param name="SensorFovDeg">Camera sensor FOV (width, height) in degrees. Null
    /// when no camera is connected or focal length / pixel size is unavailable —
    /// the overlay draws just the reticle crosshair, no rectangle.</param>
    public readonly record struct SkyMapMountOverlay(
        double RaJ2000,
        double DecJ2000,
        string DisplayName,
        bool IsSlewing,
        bool IsTracking,
        (double WidthDeg, double HeightDeg)? SensorFovDeg = null);
}
