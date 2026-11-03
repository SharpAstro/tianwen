using System;
using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.VSOP87;
using TianWen.Lib.Sequencing;

namespace TianWen.Lib.Astrometry;

/// <summary>
/// The Sun's altitude at a place and instant, and whether that instant is twilight: ONE routine for a session's sky-flat gate
/// and a frame's grade, both of which used to call <see cref="VSOP87a.Reduce(CatalogIndex, DateTimeOffset, double, double, out double, out double, out double, out double, out double)"/>
/// for the same number in their own words.
/// </summary>
public static class SolarAltitude
{
    /// <summary>The Sun's altitude below which the sky is astronomically dark, in degrees.</summary>
    public const double AstronomicalDarkDeg = -18.0;

    /// <summary>
    /// The Sun's apparent altitude in degrees at <paramref name="time"/> seen from (<paramref name="latitude"/>,
    /// <paramref name="longitude"/>), or <see cref="double.NaN"/> when the site is not known (a NaN coordinate, as
    /// <c>ImageMeta</c> carries a frame without SITELAT) or the ephemeris cannot reduce it.
    /// </summary>
    public static double Degrees(DateTimeOffset time, double latitude, double longitude)
        => double.IsFinite(latitude) && double.IsFinite(longitude)
            && VSOP87a.Reduce(CatalogIndex.Sol, time, latitude, longitude, out _, out _, out _, out var altitude, out _)
            ? altitude
            : double.NaN;

    /// <summary>
    /// Which twilight <paramref name="time"/> falls in, or null when the Sun is at or below <paramref name="darkBelowDeg"/> (the
    /// sky dark enough) or its altitude is not known. Dawn when the Sun is rising, Dusk when it is setting, told by its altitude
    /// a few minutes later, which also settles the polar case where a day has no noon to compare with.
    /// </summary>
    /// <remarks>
    /// A frame past astronomical dark is one the owner rejects by hand (#1951: every one of the 2026-05-14 dawn frames named
    /// <c>BAD_</c> began at the Sun's -18 degrees), so the grade reads this as a cause, not a quality: it says the frame was
    /// taken in a bright sky, whatever its stars look like.
    /// </remarks>
    public static TwilightPeriod? Twilight(DateTimeOffset time, double latitude, double longitude, double darkBelowDeg = AstronomicalDarkDeg)
    {
        var now = Degrees(time, latitude, longitude);
        if (!double.IsFinite(now) || now <= darkBelowDeg)
        {
            return null;
        }
        var later = Degrees(time + TimeSpan.FromMinutes(5), latitude, longitude);
        return later > now ? TwilightPeriod.Dawn : TwilightPeriod.Dusk;
    }
}
