using TianWen.Lib.Astrometry.SOFA;

namespace TianWen.Lib.Devices;

/// <summary>
/// Shared SOFA conversion from a mount-native topocentric (JNOW) position read to J2000.
/// Single source of truth for the conversion tail of
/// <see cref="IMountDriver.GetRaDecJ2000Async(Transform, bool, System.Threading.CancellationToken)"/>
/// and the fake drivers' true-pointing seam (<see cref="Fake.IFakeTruePointingSource"/>) --
/// both must convert identically or the believed-vs-true delta picks up a spurious
/// precession-sized (~22') offset.
/// </summary>
internal static class EquatorialFrameConversion
{
    /// <summary>
    /// Converts a topocentric (JNOW apparent) RA/Dec pair to J2000 via the caller's
    /// <paramref name="transform"/> (site + time already configured). Returns null when
    /// the conversion produces NaN (transform not fully initialised).
    /// </summary>
    /// <param name="transform">Caller-owned transform; not re-entrant if shared.</param>
    /// <param name="raTopocentric">Topocentric RA in hours.</param>
    /// <param name="decTopocentric">Topocentric Dec in degrees.</param>
    public static (double RaJ2000, double DecJ2000)? TopocentricToJ2000(Transform transform, double raTopocentric, double decTopocentric)
    {
        transform.SetTopocentric(raTopocentric, decTopocentric);
        transform.Refresh();

        var raJ2000 = transform.RAJ2000;
        var decJ2000 = transform.DecJ2000;
        if (double.IsNaN(raJ2000) || double.IsNaN(decJ2000))
        {
            return null;
        }
        return (raJ2000, decJ2000);
    }
}
