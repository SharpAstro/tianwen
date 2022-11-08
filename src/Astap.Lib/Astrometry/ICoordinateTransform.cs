using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Astap.Lib.Astrometry;

public enum RaDecEventTime
{
    AstroDark = 0,
    AstroTwilight = 1,
    Meridian = 2,
    MeridianL1 = 3,
    MeridianL2 = 4,
    MeridianR1 = 5,
    MeridianR2 = 6,
    Balance = 7,
}

public record RaDecEventInfo(DateTimeOffset Time, double Alt);

public interface ICoordinateTransform : IDisposable
{
    /// <summary>
    /// Returns the topocentric azimuth angle of the target (in degrees)
    /// </summary>
    double? AzimuthTopocentric { get; }

    /// <summary>
    /// Returns the Declination in apparent co-ordinates (in degrees)
    /// </summary>
    double? DECApparent { get; }

    /// <summary>
    /// Returns the Declination in J2000 co-ordinates (in degrees)
    /// </summary>
    double? DecJ2000 { get; }

    /// <summary>
    /// Returns the Declination in topocentric co-ordinates (in degrees)
    /// </summary>
    double? DECTopocentric { get; }

    /// <summary>
    /// Returns the topocentric elevation of the target (in degrees)
    /// </summary>
    double? ElevationTopocentric { get; }

    /// <summary>
    /// Sets or returns the Julian date on the Terrestrial Time timescale for which the transform will be made.
    ///
    /// Julian date (Terrestrial Time) of the transform (1757583.5 to 5373484.499999 = 00:00:00 1/1/0100 to 23:59:59.999 31/12/9999)
    ///
    /// Terrestrial Time Julian date that will be used by Transform or zero if the PC's current clock value will be used to calculate the Julian date.
    /// </summary>
    double JulianDateTT { get; set; }

    /// <summary>
    /// Sets or returns the Julian date on the UTC timescale for which the transform will be made.
    ///
    /// Julian date (UTC) of the transform (1757583.5 to 5373484.499999 = 00:00:00 1/1/0100 to 23:59:59.999 31/12/9999)
    ///
    /// UTC Julian date that will be used by Transform or zero if the PC's current clock value will be used to calculate the Julian date.
    /// </summary>
    double JulianDateUTC { get; set; }

    /// <summary>
    /// Returns the Right Ascension in J2000 co-ordinates (in hours).
    /// </summary>
    double? RA2000 { get; }

    /// <summary>
    /// Returns the Right Ascension in apparent co-ordinates (in hours).
    /// </summary>
    double? RAApparent { get; }

    /// <summary>
    /// Returns the Right Ascension in topocentric co-ordinates (in hours).
    /// </summary>
    double? RATopocentric { get; }

    /// <summary>
    /// Gets or sets a flag indicating whether refraction is calculated for topocentric co-ordinates.
    /// </summary>
    bool? Refraction { get; set; }

    /// <summary>
    /// Gets or sets the site elevation above sea level (-300.0 to +10,000.0 metres).
    /// </summary>
    double? SiteElevation { get; set; }

    /// <summary>
    /// Gets or sets the site latitude (-90.0 .. +90.0 degrees, north positive).
    /// </summary>
    double? SiteLatitude { get; set; }

    /// <summary>
    /// Gets or sets the site longitude (-180.0 to +180.0 degrees, east postitive)
    /// </summary>
    double? SiteLongitude { get; set; }

    /// <summary>
    /// Gets or sets the site ambient temperature (-273.15 to 100.0 degrees Celsius)
    /// </summary>
    double? SiteTemperature { get; set; }

    /// <summary>
    /// Causes the transform component to recalculate values derived from the last Set command
    /// </summary>
    void Refresh();

    /// <summary>
    /// Sets the known apparent Right Ascension and Declination coordinates that are to be transformed
    /// </summary>
    /// <param name="ra">RA in apparent co-ordinates (0.0 to 23.999 hours)</param>
    /// <param name="dec">DEC in apparent co-ordinates (-90.0 to +90.0)</param>
    void SetApparent(double ra, double dec);

    /// <summary>
    /// Sets the topocentric azimuth and elevation
    /// </summary>
    /// <param name="azimuth">Topocentric Azimuth in degrees (0.0 to 359.999999 - north zero, east 90 deg etc.)</param>
    /// <param name="elevation">Topocentric elevation in degrees (-90.0 to +90.0)</param>
    void SetAzimuthElevation(double azimuth, double elevation);

    /// <summary>
    /// Sets the known J2000 Right Ascension and Declination coordinates that are to be transformed
    /// </summary>
    /// <param name="ra">RA in J2000 co-ordinates (0.0 to 23.999 hours)</param>
    /// <param name="dec">DEC in J2000 co-ordinates (-90.0 to +90.0)</param>
    void SetJ2000(double ra, double dec);

    /// <summary>
    /// Sets the known topocentric Right Ascension and Declination coordinates that are to be transformed
    /// </summary>
    /// <param name="ra">RA in apparent co-ordinates (0.0 to 23.999 hours)</param>
    /// <param name="dec">DEC in apparent co-ordinates (-90.0 to +90.0)</param>
    void SetTopocentric(double ra, double dec);

    public IReadOnlyDictionary<RaDecEventTime, RaDecEventInfo> CalculateObjElevation(in CelestialObject obj, DateTimeOffset astroDark, DateTimeOffset astroTwilight, double siderealTimeAtAstroDark)
    {
        SetJ2000(obj.RA, obj.Dec);

        var raDecEventTimes = new Dictionary<RaDecEventTime, RaDecEventInfo>(4);

        var hourAngle = TimeSpan.FromHours(Utils.ConditionHA(siderealTimeAtAstroDark - obj.RA));
        var crossMeridianTime = astroDark - hourAngle;

        var darkEvent = raDecEventTimes[RaDecEventTime.AstroDark] = CalcRaDecEventInfo(astroDark);
        var twilightEvent = raDecEventTimes[RaDecEventTime.AstroTwilight] = CalcRaDecEventInfo(astroTwilight);
        var meridianEvent = raDecEventTimes[RaDecEventTime.Meridian] = CalcRaDecEventInfo(crossMeridianTime);

        raDecEventTimes[RaDecEventTime.MeridianL1] = CalcRaDecEventInfo(crossMeridianTime - TimeSpan.FromHours(0.2));
        raDecEventTimes[RaDecEventTime.MeridianL2] = CalcRaDecEventInfo(crossMeridianTime - TimeSpan.FromHours(12));
        raDecEventTimes[RaDecEventTime.MeridianR1] = CalcRaDecEventInfo(crossMeridianTime + TimeSpan.FromHours(0.2));
        raDecEventTimes[RaDecEventTime.MeridianR2] = CalcRaDecEventInfo(crossMeridianTime + TimeSpan.FromHours(12));

        TimeSpan duration;
        DateTimeOffset start;
        if (TryBalanceTimeAroundMeridian(meridianEvent.Time, darkEvent.Time, twilightEvent.Time, out var maybeBalance) && maybeBalance is DateTimeOffset balance)
        {
            raDecEventTimes[RaDecEventTime.Balance] = CalcRaDecEventInfo(balance);
            var absHours = Math.Abs((balance - meridianEvent.Time).TotalHours);
            duration = TimeSpan.FromHours(absHours * 2);
            start = meridianEvent.Time.AddHours(-absHours);
        }
        else
        {
            duration = astroTwilight - astroDark;
            start = astroDark;
        }

        const int iterations = 10;
        var step = duration / 10;
        for (var it = 1; it < iterations; it++)
        {
            start += step;
            raDecEventTimes[RaDecEventTime.Balance + it] = CalcRaDecEventInfo(start);
        }

        return raDecEventTimes;

        RaDecEventInfo CalcRaDecEventInfo(in DateTimeOffset dt)
        {
            JulianDateUTC = dt.ToJulian();
            if (ElevationTopocentric is double alt)
            {
                return new(dt, alt);
            }
            return new(dt, double.NaN);
        }
    }


    internal static bool TryBalanceTimeAroundMeridian(in DateTimeOffset m, in DateTimeOffset d, in DateTimeOffset t, [NotNullWhen(true)] out DateTimeOffset? b)
    {
        var dm = Math.Abs((d - m).TotalHours);
        var tm = Math.Abs((t - m).TotalHours);

        if (dm > tm)
        {
            b = m.AddHours((m > d ? 1 : -1) * dm);
            return true;
        }
        else if (dm == tm)
        {
            b = null;
            return false;
        }
        else
        {
            b = m.AddHours((m > t ? 1 : -1) * tm);
            return true;
        }
    }
}