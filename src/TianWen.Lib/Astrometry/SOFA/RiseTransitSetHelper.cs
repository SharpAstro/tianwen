using System;

namespace TianWen.Lib.Astrometry.SOFA;

/// <summary>
/// Rise / transit / set times for a fixed-position celestial object (stars, galaxies,
/// nebulae) at a given site. Uses Meeus algorithm 15 — sufficient accuracy (±1 min)
/// for observation planning and the sky map info panel.
/// <para>
/// For solar-system bodies (Sun, Moon, planets) the full <see cref="Transform.EventTimes"/>
/// path must be used: those move enough during the night that fixed RA/Dec is wrong.
/// </para>
/// </summary>
public static class RiseTransitSetHelper
{
    // Standard atmospheric refraction at horizon for a point source (stars, DSOs).
    // Meeus Astronomical Algorithms 2nd ed., p. 101: h0 = -0 deg 34'.
    private const double StandardHorizonAltitudeDeg = -34.0 / 60.0;

    // Mean sidereal-to-solar time ratio: LST advances 24h 3m 56.555s per 24h of UT.
    // 1.00273790935 sidereal hours per mean solar hour.
    private const double SiderealRate = 1.00273790935;

    /// <summary>
    /// Compute rise, transit, and set for a fixed RA/Dec at the given site.
    /// Times are the events nearest to <paramref name="nearUtc"/> — picked within
    /// ±12 h of that reference so the info panel shows "tonight's" events.
    /// </summary>
    /// <param name="raHours">Right ascension in hours, 0..24.</param>
    /// <param name="decDeg">Declination in degrees, -90..+90.</param>
    /// <param name="siteLatDeg">Observer latitude, degrees. NaN returns false.</param>
    /// <param name="siteLonDeg">Observer longitude, degrees east positive. NaN returns false.</param>
    /// <param name="nearUtc">Reference time — events are chosen within ±12 h of this.</param>
    /// <param name="rise">Nearest rise event, UTC.</param>
    /// <param name="transit">Nearest upper-meridian transit, UTC.</param>
    /// <param name="set">Nearest set event, UTC.</param>
    /// <param name="circumpolar">True when the object never sets (always above horizon).</param>
    /// <param name="neverRises">True when the object never rises (always below horizon).</param>
    /// <returns>
    /// True when inputs are valid. Callers must still inspect <paramref name="circumpolar"/>
    /// and <paramref name="neverRises"/>: in those cases <paramref name="rise"/> and
    /// <paramref name="set"/> are <see cref="DateTimeOffset.MinValue"/>.
    /// </returns>
    public static bool TryComputeRiseTransitSet(
        double raHours, double decDeg,
        double siteLatDeg, double siteLonDeg,
        DateTimeOffset nearUtc,
        out DateTimeOffset rise,
        out DateTimeOffset transit,
        out DateTimeOffset set,
        out bool circumpolar,
        out bool neverRises)
    {
        rise = DateTimeOffset.MinValue;
        transit = DateTimeOffset.MinValue;
        set = DateTimeOffset.MinValue;
        circumpolar = false;
        neverRises = false;

        if (double.IsNaN(siteLatDeg) || double.IsNaN(siteLonDeg)
            || double.IsNaN(raHours) || double.IsNaN(decDeg))
        {
            return false;
        }

        var utcNow = nearUtc.ToUniversalTime();
        var lst0 = SiteContext.ComputeLST(utcNow, siteLonDeg);

        // Transit: LST = RA. Solve (RA - LST0) as hours of LST, then convert to UT.
        // Pick the offset within (-12, +12] so we report the *nearest* transit.
        var deltaLstHours = WrapHours(raHours - lst0);
        transit = utcNow + TimeSpan.FromHours(deltaLstHours / SiderealRate);

        // Rise / set via hour angle at horizon.
        var (sinLat, cosLat) = Math.SinCos(double.DegreesToRadians(siteLatDeg));
        var (sinDec, cosDec) = Math.SinCos(double.DegreesToRadians(decDeg));
        var sinH0 = Math.Sin(double.DegreesToRadians(StandardHorizonAltitudeDeg));

        // cos(H0) = (sin(h0) - sin(lat) sin(dec)) / (cos(lat) cos(dec))
        var denom = cosLat * cosDec;
        if (Math.Abs(denom) < 1e-12)
        {
            // Pole: every object is either circumpolar or never rises depending on dec sign.
            if ((siteLatDeg >= 0 && decDeg >= 0) || (siteLatDeg < 0 && decDeg < 0))
            {
                circumpolar = true;
            }
            else
            {
                neverRises = true;
            }
            return true;
        }

        var cosH0 = (sinH0 - sinLat * sinDec) / denom;
        if (cosH0 < -1.0)
        {
            circumpolar = true;
            return true;
        }
        if (cosH0 > 1.0)
        {
            neverRises = true;
            return true;
        }

        var h0Hours = double.RadiansToDegrees(Math.Acos(cosH0)) / 15.0;
        // Convert sidereal-hour offset to UT-hour offset.
        var h0UtHours = h0Hours / SiderealRate;
        rise = transit - TimeSpan.FromHours(h0UtHours);
        set = transit + TimeSpan.FromHours(h0UtHours);
        return true;
    }

    // Wrap into (-12, +12] to pick the nearest event to the reference time.
    private static double WrapHours(double h)
    {
        h %= 24.0;
        if (h > 12.0) h -= 24.0;
        else if (h <= -12.0) h += 24.0;
        return h;
    }
}
