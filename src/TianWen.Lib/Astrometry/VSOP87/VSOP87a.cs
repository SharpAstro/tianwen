using TianWen.Lib.Astrometry.Catalogs;
using TianWen.Lib.Astrometry.Lunar;
using System;
using static TianWen.Lib.Astrometry.SOFA.SofaFunctions;

namespace TianWen.Lib.Astrometry.VSOP87;

public static class VSOP87a
{
    public static bool Reduce(CatalogIndex catIndex, DateTimeOffset time, double latitude, double longitude, out double ra, out double dec, out double az, out double alt, out double distance)
    {
        time.ToSOFAUtcJdTT(out var utc1, out var utc2, out var tt1, out var tt2);
        return Reduce(catIndex, utc1, utc2, tt1, tt2, latitude, longitude, out ra, out dec, out az, out alt, out distance);
    }

    /// <summary>
    /// Returns the body's J2000.0 equatorial RA/Dec (geocentric, light-time corrected,
    /// no precession to date). Use this for plotting on a J2000 sky chart -- the
    /// regular <see cref="Reduce(CatalogIndex, DateTimeOffset, double, double, out double, out double, out double, out double, out double)"/>
    /// overload precesses to the current date and is appropriate for telescope-pointing
    /// or alt/az computations, not for J2000-frame overlay plotting.
    /// </summary>
    public static bool ReduceJ2000(CatalogIndex catIndex, DateTimeOffset time, out double ra, out double dec, out double distance)
    {
        time.ToSOFAUtcJdTT(out _, out _, out var tt1, out var tt2);
        double et = (tt1 - Constants.J2000BASE + tt2) / 365250.0;

        Span<double> earth = stackalloc double[3];
        Span<double> body = stackalloc double[3];

        if (!GetBody(catIndex, et, body) || !GetBody(CatalogIndex.Earth, et, earth))
        {
            ra = double.NaN; dec = double.NaN; distance = double.NaN;
            return false;
        }

        body[0] -= earth[0]; body[1] -= earth[1]; body[2] -= earth[2];

        // Light-time correction (same as Reduce).
        distance = Math.Sqrt(body[0] * body[0] + body[1] * body[1] + body[2] * body[2]);
        distance *= 1.496e+11;
        double lightTime = distance / 299792458.0;
        et -= lightTime / 24.0 / 60.0 / 60.0 / 365250.0;

        GetBody(catIndex, et, body);
        body[0] -= earth[0]; body[1] -= earth[1]; body[2] -= earth[2];

        // Convert VSOP87 ecliptic-of-J2000 to J2000 equatorial. Crucially, do NOT apply
        // Pnm06a (precession + nutation + bias to date) -- callers want the J2000 frame.
        Rotvsop2J2000(body);

        // Geocentric RA/Dec from cartesian.
        double r = Math.Sqrt(body[0] * body[0] + body[1] * body[1] + body[2] * body[2]);
        dec = Math.Acos(body[2] / r);
        ra = Math.Atan2(body[1], body[0]);
        dec = .5 * Math.PI - dec;

        ra = CoordinateUtils.ConditionRA(ra * Constants.RADIANS2HOURS);
        dec *= Constants.RADIANS2DEGREES;
        return true;
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="catIndex"></param>
    /// <param name="utc1"></param>
    /// <param name="utc2"></param>
    /// <param name="tt1"></param>
    /// <param name="tt2"></param>
    /// <param name="latitude"></param>
    /// <param name="longitude"></param>
    /// <param name="ra">RA in JNOW</param>
    /// <param name="dec">Dec in JNOW</param>
    /// <param name="az"></param>
    /// <param name="alt"></param>
    /// <param name="distance">dinstance in meters</param>
    /// <returns></returns>
    public static bool Reduce(CatalogIndex catIndex, double utc1, double utc2, double tt1, double tt2, double latitude, double longitude, out double ra, out double dec, out double az, out double alt, out double distance)
    {
        double et = (tt1 - Constants.J2000BASE + tt2) / 365250.0;

        //Compute initial position
        Span<double> earth = stackalloc double[3];
        Span<double> body = stackalloc double[3];

        if (!GetBody(catIndex, et, body))
        {
            ra = double.NaN;
            dec = double.NaN;
            az = double.NaN;
            alt = double.NaN;
            distance = double.NaN;
            return false;
        }

        if (!GetBody(CatalogIndex.Earth, et, earth))
        {
            ra = double.NaN;
            dec = double.NaN;
            az = double.NaN;
            alt = double.NaN;
            distance = double.NaN;
            return false;
        }

        body[0] -= earth[0];
        body[1] -= earth[1];
        body[2] -= earth[2];

        //Compute light time to body, then recompute for apparent position
        distance = Math.Sqrt(body[0] * body[0] + body[1] * body[1] + body[2] * body[2]);
        distance *= 1.496e+11; //Convert from AU to meters
        double lightTime = distance / 299792458.0;
        et -= lightTime / 24.0 / 60.0 / 60.0 / 365250.0;

        GetBody(catIndex, et, body);
        body[0] -= earth[0];
        body[1] -= earth[1];
        body[2] -= earth[2];

        //Convert VSOP87 coordinates to J2000
        Rotvsop2J2000(body);

        //Get the precession, nutation, and bias matrix
        Span<double> rnpb = stackalloc double[9];
        Pnm06a(tt1, tt2, rnpb);

        Rxp(rnpb, body, body);

        //Use UT1 for Earth Rotation Angle
        double era = Era00(utc1, utc2);

        //Get observer's xyz coordinates in J2000 coords
        double latRad = latitude * Constants.DEGREES2RADIANS;
        double lonRad = longitude * Constants.DEGREES2RADIANS;
        Span<double> observerPV = stackalloc double[6];
        Pvtob(lonRad, latRad, 0, 0, 0, 0, era, observerPV);

        Tr(rnpb, rnpb);
        Rxpv(rnpb, observerPV, observerPV);

        observerPV[0] /= 1.49597870691E+11;
        observerPV[1] /= 1.49597870691E+11;
        observerPV[2] /= 1.49597870691E+11;

        //Convert body position to topocentric
        body[0] -= observerPV[0];
        body[1] -= observerPV[1];
        body[2] -= observerPV[2];

        //Convert coords to polar, which gives RA/DEC
        double r = Math.Sqrt(body[0] * body[0] + body[1] * body[1] + body[2] * body[2]);
        dec = Math.Acos(body[2] / r);
        ra = Math.Atan2(body[1], body[0]);
        // Keep ra in radians for the altaz hour-angle math below; the final
        // [0, 2pi) normalisation happens after the radians->hours conversion
        // via CoordinateUtils.ConditionRA.
        if (ra < 0)
        {
            ra += 2 * Math.PI;
        }
        dec = .5 * Math.PI - dec;

        //Convert to altaz
        double GMST = Gmst06(utc1, utc2, tt1, tt2);

        double h = GMST + lonRad - ra;

        double sinAlt = Math.Sin(dec) * Math.Sin(latRad) + Math.Cos(dec) * Math.Cos(h) * Math.Cos(latRad);
        alt = Math.Asin(sinAlt);

        double cosAz = (Math.Sin(dec) * Math.Cos(latRad) - Math.Cos(dec) * Math.Cos(h) * Math.Sin(latRad)) / Math.Cos(alt);
        az = Math.Acos(cosAz);
        if (Math.Sin(h) > 0)
        {
            az = 2.0 * Math.PI - az;
        }

        ra = CoordinateUtils.ConditionRA(ra * Constants.RADIANS2HOURS);
        dec *= Constants.RADIANS2DEGREES;
        az *= Constants.RADIANS2DEGREES;
        alt *= Constants.RADIANS2DEGREES;

        return true;
    }

    public static bool GetBody(CatalogIndex catIndex, double et, Span<double> body)
    {
        Span<double> earth = stackalloc double[3];
        Span<double> emb = stackalloc double[3];

        switch (catIndex)
        {
            case CatalogIndex.Sol:
                body[0] = 0;
                body[1] = 0;
                body[2] = 0;
                return true; //Sun is at the center for vsop87a
                        //return vsop87e_full.getSun(et);  // "E" is the only version the Sun is not always at [0,0,0]
            case CatalogIndex.Mercury:
                Mercury.GetBody3d(et, body);
                return true;
            case CatalogIndex.Venus:
                Venus.GetBody3d(et, body);
                return true;
            case CatalogIndex.Earth:
                Earth.GetBody3d(et, body);
                return true;
            case CatalogIndex.Mars:
                Mars.GetBody3d(et, body);
                return true;
            case CatalogIndex.Jupiter:
                Jupiter.GetBody3d(et, body);
                return true;
            case CatalogIndex.Saturn:
                Saturn.GetBody3d(et, body);
                return true;
            case CatalogIndex.Uranus:
                Uranus.GetBody3d(et, body);
                return true;
            case CatalogIndex.Neptune:
                Neptune.GetBody3d(et, body);
                return true;
            case CatalogIndex.EarthMoonBarycenter:
                //return [0,0,0]; //Vsop87a is the only version which can compute the moon
                Emb.GetBody3d(et, body);
                return true;
            case CatalogIndex.Moon:
                // Use Meeus simplified lunar ephemeris for geocentric ecliptic coordinates,
                // then convert to heliocentric XYZ so Reduce() can subtract Earth as usual.
                MeeusMoon.GetHeliocentricXYZ(et, body);
                return true;

            default:
                return false;
        }
    }

    public static void Rotvsop2J2000(Span<double> body)
    {
        var a = body[0] + body[1] * 0.000000440360 + body[2] * -0.000000190919;
        var b = body[0] * -0.000000479966 + body[1] * 0.917482137087 + body[2] * -0.397776982902;
        var c = body[1] * 0.397776982902 + body[2] * 0.917482137087;

        body[0] = a;
        body[1] = b;
        body[2] = c;
    }

}
