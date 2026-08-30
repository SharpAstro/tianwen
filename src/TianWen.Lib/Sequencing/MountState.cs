using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

/// <summary>
/// Polled mount state snapshot for the live session UI.
/// <para>
/// <see cref="RightAscension"/> / <see cref="Declination"/> are in the mount's native
/// epoch (<see cref="IMountDriver.EquatorialSystem"/>, typically topocentric).
/// <see cref="RaJ2000"/> / <see cref="DecJ2000"/> are the precessed J2000 coordinates,
/// populated via the poller's cached SOFA <see cref="Astrometry.SOFA.Transform"/>.
/// Sky-map overlays should always read the J2000 fields so they render in the
/// same frame as stars/catalog objects. When the mount already reports J2000 the
/// J2000 fields simply mirror the native ones.
/// </para>
/// </summary>
public readonly record struct MountState(
    double RightAscension,
    double Declination,
    double HourAngle,
    PointingState PierSide,
    bool IsSlewing,
    bool IsTracking,
    double RaJ2000 = double.NaN,
    double DecJ2000 = double.NaN,

    /// <summary>
    /// GEOMETRIC altitude in degrees, derived from <see cref="HourAngle"/>, <see cref="Declination"/>
    /// and the site latitude; NaN when the site or the pointing is unknown. Unrefracted on purpose --
    /// it feeds the mechanical horizon limit, and a tripod leg is not lifted by the atmosphere.
    /// </summary>
    double Altitude = double.NaN,
    /// <summary>
    /// The RA axis angle from the counterweight-down home, degrees in (-180, 180]
    /// (<see cref="IMountDriver.GetAxisAngleAsync"/>); NaN when the driver cannot model its axis (the
    /// driver's null, carried as NaN like every other unknown here). Feeds the meridian safety limit's
    /// mechanical tier, which wins over the hour-angle estimate when present.
    /// </summary>
    double PrimaryAxisAngleDeg = double.NaN);
