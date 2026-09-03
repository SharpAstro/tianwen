using System;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Astrometry.SOFA;

/// <summary>
/// Lightweight precomputed observer site data. Caches LST and sin/cos/tan(lat)
/// for fast horizon checks and coordinate conversions without the full SOFA pipeline.
/// Use <see cref="Transform"/> when you need precession, nutation, or refraction;
/// use <see cref="SiteContext"/> when you only need horizon geometry or LST.
/// </summary>
/// <remarks>
/// Potential uses beyond the sky map:
/// - Mount drivers that allocate a full <see cref="Transform"/> only for LST
///   (SkywatcherMountDriverBase, SgpMountDriverBase, FakeMountDriver)
/// - NeuralGuideFeatures which manually stores sinLat/cosLat for altitude computation
/// </remarks>
public readonly record struct SiteContext
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double LST { get; init; }
    public double SinLat { get; init; }
    public double CosLat { get; init; }
    public double TanLat { get; init; }
    public bool IsValid { get; init; }

    /// <summary>
    /// Compute Local Sidereal Time in hours from UTC and longitude.
    /// Uses the IAU 1982 GMST formula (accurate to ~1 second).
    /// Functionally equivalent to <see cref="Transform.CalculateLocalSiderealTime"/>
    /// but operates on <see cref="DateTimeOffset"/> and is allocation-free.
    /// </summary>
    public static double ComputeLST(DateTimeOffset utcNow, double lonDeg)
    {
        var jd = 2451545.0 + (utcNow - new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero)).TotalDays;
        var T = (jd - 2451545.0) / 36525.0;

        // GMST in degrees (IAU 1982 formula)
        var gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0)
                    + 0.000387933 * T * T - T * T * T / 38710000.0;
        gmst = ((gmst % 360.0) + 360.0) % 360.0;

        var lst = (gmst + lonDeg) / 15.0; // convert to hours
        return ((lst % 24.0) + 24.0) % 24.0;
    }

    public static SiteContext Create(double siteLat, double siteLon, DateTimeOffset utcNow)
    {
        if (double.IsNaN(siteLat) || double.IsNaN(siteLon))
        {
            return default;
        }

        var lst = ComputeLST(utcNow, siteLon);
        var (sinLat, cosLat) = Math.SinCos(siteLat * Math.PI / 180.0);
        return new SiteContext
        {
            Latitude = siteLat,
            Longitude = siteLon,
            LST = lst,
            SinLat = sinLat,
            CosLat = cosLat,
            TanLat = Math.Tan(siteLat * Math.PI / 180.0),
            IsValid = true
        };
    }

    public static SiteContext Create(double siteLat, double siteLon, ITimeProvider timeProvider)
        => Create(siteLat, siteLon, timeProvider.GetUtcNow());

    /// <summary>
    /// Returns true if the given RA/Dec is above the horizon (altitude &gt; 0).
    /// sin(alt) = sin(lat)*sin(dec) + cos(lat)*cos(dec)*cos(HA)
    /// </summary>
    public bool IsAboveHorizon(double ra, double dec)
    {
        if (!IsValid)
        {
            return true; // no site info → show everything
        }

        var ha = (LST - ra) * Math.PI / 12.0;
        var (sinDec, cosDec) = Math.SinCos(dec * Math.PI / 180.0);
        return SinLat * sinDec + CosLat * cosDec * Math.Cos(ha) >= 0;
    }

    /// <summary>
    /// Geometric altitude in degrees for an hour angle (HOURS) and declination (degrees), or
    /// <see cref="double.NaN"/> when the site is unknown or either input is NaN.
    /// </summary>
    /// <remarks>
    /// <para><b>Geometric, deliberately: no refraction.</b> This exists for the mechanical horizon
    /// limit, and a tripod leg is not lifted by the atmosphere. Refraction raises a body by up to
    /// ~34 arcmin at the horizon, so a refracted altitude would report the tube higher than it is
    /// and the limit would fire late -- in the one regime where being late is the whole problem.
    /// Use <see cref="Transform"/> when you want what the eye would see.</para>
    ///
    /// <para>Takes an hour angle rather than an RA because the caller that needs this reads HA
    /// straight off the mount, and going RA -> HA via <see cref="LST"/> would re-introduce a clock
    /// the mount has already accounted for.</para>
    /// </remarks>
    public double AltitudeDegrees(double hourAngleHours, double decDeg)
        => IsValid ? AltitudeFrom(SinLat, CosLat, hourAngleHours, decDeg) : double.NaN;

    /// <summary>
    /// The same geometric altitude for a caller that holds a latitude but no site context -- a
    /// guide-feature builder, say, which never needs LST.
    /// </summary>
    public static double AltitudeDegrees(double latitudeDeg, double hourAngleHours, double decDeg)
    {
        if (double.IsNaN(latitudeDeg))
        {
            return double.NaN;
        }

        var (sinLat, cosLat) = Math.SinCos(latitudeDeg * Math.PI / 180.0);
        return AltitudeFrom(sinLat, cosLat, hourAngleHours, decDeg);
    }

    /// <summary>
    /// <b>The one implementation of geometric altitude.</b> Everything above funnels here.
    /// </summary>
    /// <remarks>
    /// This existed three times before it existed once: <c>CometObservability.AltitudeDeg</c>
    /// (which already borrowed <see cref="ComputeLST"/> and then hand-rolled the rest), the inline
    /// block in <c>NeuralGuideFeatures</c> that this type's own remarks already named as a
    /// candidate, and a fourth copy added with the mount safety limits. They were identical down to
    /// the <see cref="Math.Clamp(double, double, double)"/>.
    /// <para>
    /// <see cref="IsAboveHorizon"/> is deliberately NOT routed through this: it answers a cheaper
    /// question (sign only) and skipping the <see cref="Math.Asin(double)"/> matters on the sky
    /// map's per-star path. <c>SOFAHelpers.AltitudeFromAstrom</c> and
    /// <see cref="Transform.ElevationTopocentric"/> are not duplicates either -- they are the SOFA
    /// apparent place and the REFRACTED altitude, which is what the planner and a human eye want
    /// and what a mechanical limit must not use. Nor is <c>VSOP87a</c>'s: it converts equatorial to
    /// horizontal wholesale, in radians, off SOFA's <c>Gmst06</c> rather than this type's IAU-1982
    /// approximation, and its altitude feeds its azimuth formula directly. Leave it alone.
    /// </para>
    /// </remarks>
    private static double AltitudeFrom(double sinLat, double cosLat, double hourAngleHours, double decDeg)
    {
        if (double.IsNaN(hourAngleHours) || double.IsNaN(decDeg))
        {
            return double.NaN;
        }

        var ha = hourAngleHours * Math.PI / 12.0;
        var (sinDec, cosDec) = Math.SinCos(decDeg * Math.PI / 180.0);
        var sinAlt = sinLat * sinDec + cosLat * cosDec * Math.Cos(ha);
        return Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Geometric azimuth in degrees [0, 360) from north through east, for an hour angle (HOURS) and
    /// declination (degrees) at <paramref name="latitudeDeg"/>; NaN when any input is NaN. The
    /// companion of <see cref="AltitudeDegrees(double, double, double)"/> and, like it, refraction-free
    /// (refraction moves a body along the vertical, so the azimuth is exact either way).
    /// </summary>
    public static double AzimuthDegrees(double latitudeDeg, double hourAngleHours, double decDeg)
    {
        if (double.IsNaN(latitudeDeg) || double.IsNaN(hourAngleHours) || double.IsNaN(decDeg))
        {
            return double.NaN;
        }

        var ha = hourAngleHours * Math.PI / 12.0;
        var (sinLat, cosLat) = Math.SinCos(latitudeDeg * Math.PI / 180.0);
        var (sinDec, cosDec) = Math.SinCos(decDeg * Math.PI / 180.0);
        var az = Math.Atan2(-cosDec * Math.Sin(ha), sinDec * cosLat - cosDec * sinLat * Math.Cos(ha));
        return CoordinateUtils.ConditionDegrees(az * 180.0 / Math.PI);
    }

    /// <summary>
    /// Returns the Dec at which altitude = 0 for the given RA.
    /// dec_horizon = atan(-cos(HA) / tan(lat))
    /// </summary>
    public double HorizonDec(double ra)
    {
        if (Math.Abs(TanLat) < 1e-10)
        {
            return 0;
        }
        var ha = (LST - ra) * Math.PI / 12.0;
        return Math.Atan(-Math.Cos(ha) / TanLat) * 180.0 / Math.PI;
    }
}
