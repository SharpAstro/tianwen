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

    public static SiteContext Create(double siteLat, double siteLon, ITimeProvider timeProvider)
    {
        if (double.IsNaN(siteLat) || double.IsNaN(siteLon))
        {
            return default;
        }

        var lst = ComputeLST(timeProvider.GetUtcNow(), siteLon);
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
