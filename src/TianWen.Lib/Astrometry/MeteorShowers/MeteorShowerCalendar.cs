using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.Lib.Astrometry.MeteorShowers;

/// <summary>
/// When a shower peaks in a given year: the instant the Sun's longitude reaches the peak's. Never a stored date, since a
/// date drifts against the calendar by the leap cycle and the longitude does not (docs/plans/yearly-planner.md, E).
/// </summary>
public static class MeteorShowerCalendar
{
    /// <summary>The obliquity of the ecliptic at J2000.0, degrees.</summary>
    private const double ObliquityJ2000Deg = 23.4392911;

    /// <summary>The Sun's mean motion in longitude, degrees per day; the Newton step's slope (its real rate is within 4 % of it).</summary>
    private const double MeanSunRateDegPerDay = 360.0 / 365.2422;

    /// <summary>The solve ends within this many degrees of the target, about a tenth of a second of time.</summary>
    private const double ToleranceDeg = 1e-6;

    /// <summary>A Newton step on a near-linear function converges in four or five; this bounds the loop, never the answer.</summary>
    private const int MaxIterations = 16;

    /// <summary>Degrees reduced to [0, 360).</summary>
    internal static double Wrap360(double degrees)
    {
        var wrapped = degrees % 360.0;
        return wrapped < 0 ? wrapped + 360.0 : wrapped;
    }

    /// <summary>Degrees reduced to [-180, 180).</summary>
    private static double Wrap180(double degrees) => Wrap360(degrees + 180.0) - 180.0;

    /// <summary>
    /// The Sun's geocentric ecliptic longitude at <paramref name="time"/>, in degrees [0, 360), referred to the equinox of
    /// J2000 as the IMO's solar longitudes are, from the app's own VSOP87 Sun (light-time corrected, which is the Sun's
    /// aberration). NaN when the ephemeris cannot reduce the Sun.
    /// </summary>
    public static double SunLongitude(DateTimeOffset time)
    {
        if (!VSOP87a.ReduceJ2000(CatalogIndex.Sol, time, out var raHours, out var decDeg, out _))
        {
            return double.NaN;
        }

        var (sinRa, cosRa) = Math.SinCos(raHours * 15.0 * Math.PI / 180.0);
        var (sinEps, cosEps) = Math.SinCos(ObliquityJ2000Deg * Math.PI / 180.0);
        var tanDec = Math.Tan(decDeg * Math.PI / 180.0);
        return Wrap360(Math.Atan2((sinRa * cosEps) + (tanDec * sinEps), cosRa) * 180.0 / Math.PI);
    }

    /// <summary>
    /// The first instant at or after <paramref name="from"/> when the Sun reaches <paramref name="lambda"/> (equinox J2000,
    /// degrees), UTC; null only when the ephemeris fails. A Newton iteration on the longitude, bounded.
    /// </summary>
    public static DateTimeOffset? NextSunLongitude(double lambda, DateTimeOffset from)
    {
        var start = SunLongitude(from);
        if (double.IsNaN(start))
        {
            return null;
        }

        var time = from.ToUniversalTime().AddDays(Wrap360(lambda - start) / MeanSunRateDegPerDay);
        for (var i = 0; i < MaxIterations; i++)
        {
            var error = Wrap180(SunLongitude(time) - lambda);
            if (double.IsNaN(error))
            {
                return null;
            }

            if (Math.Abs(error) < ToleranceDeg)
            {
                break;
            }

            time = time.AddDays(-error / MeanSunRateDegPerDay);
        }

        return time;
    }

    /// <summary>
    /// The instant of the peak of <paramref name="shower"/> in calendar <paramref name="year"/> (UTC): the first time on or after
    /// the year's January 1st that the Sun reaches the peak's longitude. The Quadrantids' longitude (283.15) is first reached
    /// a few days into January, so a peak is never lost to the turn of the year.
    /// </summary>
    public static DateTimeOffset? PeakInstant(MeteorShower shower, int year)
        => NextSunLongitude(shower.PeakLambda, new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The peak of <paramref name="shower"/> nearest <paramref name="time"/>: the one within half a year of it, so a night at
    /// the end of December finds the Quadrantids' peak of the new year.
    /// </summary>
    public static DateTimeOffset? PeakNear(MeteorShower shower, DateTimeOffset time)
    {
        var sun = SunLongitude(time);
        if (double.IsNaN(sun))
        {
            return null;
        }

        var offsetDeg = Wrap180(shower.PeakLambda - sun);
        return NextSunLongitude(shower.PeakLambda, time.ToUniversalTime().AddDays((offsetDeg - 1.0) / MeanSunRateDegPerDay));
    }
}
