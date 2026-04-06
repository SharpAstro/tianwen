using System;

using TianWen.Lib.Astrometry.VSOP87;

namespace TianWen.Lib.Astrometry.Lunar;

/// <summary>
/// Simplified lunar ephemeris based on Meeus "Astronomical Algorithms" chapter 47.
/// Computes geocentric ecliptic longitude, latitude, and distance, then converts
/// to heliocentric J2000 XYZ for integration with the VSOP87a pipeline.
/// Accuracy: ~0.3° in longitude, ~0.1° in latitude — sufficient for planner use.
/// </summary>
internal static class MeeusMoon
{
    /// <summary>
    /// Computes the Moon's heliocentric XYZ position (in AU, ecliptic VSOP87 frame)
    /// so that <see cref="VSOP87a.Reduce"/> can subtract Earth and convert to topocentric.
    /// </summary>
    /// <param name="et">Julian millennia from J2000 (same as VSOP87a uses).</param>
    /// <param name="body">Output: heliocentric XYZ in AU.</param>
    public static void GetHeliocentricXYZ(double et, Span<double> body)
    {
        // Convert Julian millennia to Julian centuries (Meeus uses T in centuries)
        var T = et * 10.0;

        // Fundamental arguments (degrees)
        // L' — Moon's mean longitude
        var Lp = Normalize(218.3164477 + 481267.88123421 * T - 0.0015786 * T * T + T * T * T / 538841.0);
        // D — Mean elongation of the Moon
        var D = Normalize(297.8501921 + 445267.1114034 * T - 0.0018819 * T * T + T * T * T / 545868.0);
        // M — Sun's mean anomaly
        var M = Normalize(357.5291092 + 35999.0502909 * T - 0.0001536 * T * T);
        // M' — Moon's mean anomaly
        var Mp = Normalize(134.9633964 + 477198.8675055 * T + 0.0087414 * T * T + T * T * T / 69699.0);
        // F — Moon's argument of latitude
        var F = Normalize(93.2720950 + 483202.0175233 * T - 0.0036539 * T * T - T * T * T / 3526000.0);

        // Additional arguments
        var A1 = Normalize(119.75 + 131.849 * T);
        var A2 = Normalize(53.09 + 479264.290 * T);
        var A3 = Normalize(313.45 + 481266.484 * T);

        // Eccentricity correction
        var E = 1.0 - 0.002516 * T - 0.0000074 * T * T;
        var E2 = E * E;

        // Sum of principal terms for longitude (Σl) and distance (Σr)
        double sumL = 0, sumR = 0;

        // Longitude and distance terms (Meeus Table 47.A — largest 60 terms, here top 30)
        sumL += 6288774 * SinD(Mp);
        sumL += 1274027 * SinD(2 * D - Mp);
        sumL += 658314 * SinD(2 * D);
        sumL += 213618 * SinD(2 * Mp);
        sumL += -185116 * SinD(M) * E;
        sumL += -114332 * SinD(2 * F);
        sumL += 58793 * SinD(2 * D - 2 * Mp);
        sumL += 57066 * SinD(2 * D - M - Mp) * E;
        sumL += 53322 * SinD(2 * D + Mp);
        sumL += 45758 * SinD(2 * D - M) * E;
        sumL += -40923 * SinD(M - Mp) * E;
        sumL += -34720 * SinD(D);
        sumL += -30383 * SinD(M + Mp) * E;
        sumL += 15327 * SinD(2 * D - 2 * F);
        sumL += -12528 * SinD(Mp + 2 * F);
        sumL += 10980 * SinD(Mp - 2 * F);
        sumL += 10675 * SinD(4 * D - Mp);
        sumL += 10034 * SinD(3 * Mp);
        sumL += 8548 * SinD(4 * D - 2 * Mp);
        sumL += -7888 * SinD(2 * D + M - Mp) * E;
        sumL += -6766 * SinD(2 * D + M) * E;
        sumL += -5163 * SinD(D - Mp);
        sumL += 4987 * SinD(D + M) * E;
        sumL += 4036 * SinD(2 * D - M + Mp) * E;
        sumL += 3994 * SinD(2 * D + 2 * Mp);
        sumL += 3861 * SinD(4 * D);
        sumL += 3665 * SinD(2 * D - 3 * Mp);
        sumL += -2689 * SinD(M - 2 * Mp) * E;
        sumL += -2602 * SinD(2 * D - Mp + 2 * F);
        sumL += 2390 * SinD(2 * D - M - 2 * Mp) * E;

        // Additional corrections for longitude
        sumL += 3958 * SinD(A1) + 1962 * SinD(Lp - F) + 318 * SinD(A2);

        // Distance terms (Meeus Table 47.A — top 15)
        sumR += -20905355 * CosD(Mp);
        sumR += -3699111 * CosD(2 * D - Mp);
        sumR += -2955968 * CosD(2 * D);
        sumR += -569925 * CosD(2 * Mp);
        sumR += 48888 * CosD(M) * E;
        sumR += -3149 * CosD(2 * F);
        sumR += 246158 * CosD(2 * D - 2 * Mp);
        sumR += -152138 * CosD(2 * D - M - Mp) * E;
        sumR += -170733 * CosD(2 * D + Mp);
        sumR += -204586 * CosD(2 * D - M) * E;
        sumR += -129620 * CosD(M - Mp) * E;
        sumR += 108743 * CosD(D);
        sumR += 104755 * CosD(M + Mp) * E;
        sumR += 10321 * CosD(2 * D - 2 * F);
        sumR += 79661 * CosD(Mp - 2 * F);

        // Latitude terms (Meeus Table 47.B — top 20)
        double sumB = 0;
        sumB += 5128122 * SinD(F);
        sumB += 280602 * SinD(Mp + F);
        sumB += 277693 * SinD(Mp - F);
        sumB += 173237 * SinD(2 * D - F);
        sumB += 55413 * SinD(2 * D - Mp + F);
        sumB += 46271 * SinD(2 * D - Mp - F);
        sumB += 32573 * SinD(2 * D + F);
        sumB += 17198 * SinD(2 * Mp + F);
        sumB += 9266 * SinD(2 * D + Mp - F);
        sumB += 8822 * SinD(2 * Mp - F);
        sumB += 8216 * SinD(2 * D - M - F) * E;
        sumB += 4324 * SinD(2 * D - 2 * Mp - F);
        sumB += 4200 * SinD(2 * D + Mp + F);
        sumB += -3359 * SinD(2 * D + M - F) * E;
        sumB += 2463 * SinD(2 * D - M - Mp + F) * E;
        sumB += 2211 * SinD(2 * D - M + F) * E;
        sumB += 2065 * SinD(2 * D - M - Mp - F) * E;
        sumB += -1870 * SinD(M - Mp - F) * E;
        sumB += 1828 * SinD(4 * D - Mp - F);
        sumB += -1794 * SinD(M + F) * E;

        // Additional corrections for latitude
        sumB += -2235 * SinD(Lp) + 382 * SinD(A3) + 175 * SinD(A1 - F) + 175 * SinD(A1 + F) + 127 * SinD(Lp - Mp) - 115 * SinD(Lp + Mp);

        // Geocentric ecliptic coordinates
        var lambdaDeg = Lp + sumL / 1_000_000.0;  // ecliptic longitude (degrees)
        var betaDeg = sumB / 1_000_000.0;           // ecliptic latitude (degrees)
        var distKm = 385000.56 + sumR / 1000.0;     // distance in km

        // Convert to geocentric equatorial XYZ (AU) in the ecliptic frame
        var lambdaRad = lambdaDeg * Constants.DEGREES2RADIANS;
        var betaRad = betaDeg * Constants.DEGREES2RADIANS;
        var distAU = distKm / 1.496e+8; // km to AU

        var cosB = Math.Cos(betaRad);
        var geoX = distAU * cosB * Math.Cos(lambdaRad);
        var geoY = distAU * cosB * Math.Sin(lambdaRad);
        var geoZ = distAU * Math.Sin(betaRad);

        // Add Earth's heliocentric position to get the Moon's heliocentric position
        // (Reduce() will subtract Earth to get back to geocentric)
        Span<double> earth = stackalloc double[3];
        Earth.GetBody3d(et, earth);

        // geoXYZ is in ecliptic frame, Earth from VSOP87 is also ecliptic → just add
        body[0] = earth[0] + geoX;
        body[1] = earth[1] + geoY;
        body[2] = earth[2] + geoZ;
    }

    /// <summary>
    /// Computes the Moon's illumination fraction (0 = new, 1 = full) and whether it is waxing.
    /// Uses the geocentric elongation between Moon and Sun ecliptic longitudes.
    /// </summary>
    /// <param name="jd">Julian date (TDB).</param>
    /// <returns>Illumination fraction (0–1) and waxing flag.</returns>
    internal static (double Illumination, bool Waxing) GetPhase(double jd)
    {
        var t = (jd - 2451545.0) / 36525.0;

        // Compute Moon's geocentric ecliptic longitude (same first step as GetHeliocentricXYZ)
        var Lp = Normalize(218.3164477 + 481267.88123421 * t);
        var D  = Normalize(297.8501921 + 445267.1114034 * t);
        var M  = Normalize(357.5291092 + 35999.0502909 * t);
        var Mp = Normalize(134.9633964 + 477198.8675055 * t);

        // Simplified Moon longitude (major terms only)
        var moonLon = Lp + 6.289 * SinD(Mp) - 1.274 * SinD(2 * D - Mp) + 0.658 * SinD(2 * D)
                       + 0.214 * SinD(2 * Mp) - 0.186 * SinD(M);

        // Sun's mean longitude (approximate)
        var sunLon = Normalize(280.46646 + 36000.76983 * t);

        // Elongation: difference in ecliptic longitude (Moon - Sun)
        var elongation = Normalize(moonLon - sunLon);

        // Illumination fraction from elongation: 0° = new (0%), 180° = full (100%)
        var halfElongRad = elongation * 0.5 * Constants.DEGREES2RADIANS;
        var sinHalf = Math.Sin(halfElongRad);
        var illumination = sinHalf * sinHalf;

        // Waxing: elongation 0–180° (Moon moving away from Sun)
        var waxing = elongation < 180.0;

        return (illumination, waxing);
    }

    /// <summary>
    /// Returns the Unicode moon phase emoji for the given illumination and hemisphere.
    /// Southern hemisphere sees the illumination mirrored on the X axis — a waxing crescent
    /// (lit on right in north) appears lit on the left in south. We pick the visually
    /// mirrored emoji: e.g. waxing crescent in south uses the waning crescent glyph (🌘).
    /// </summary>
    internal static string GetPhaseEmoji(double illumination, bool waxing, bool southernHemisphere)
    {
        // Southern hemisphere: same phase name, flipped visual → pick the mirrored glyph
        var litOnRight = southernHemisphere ? !waxing : waxing;

        return illumination switch
        {
            < 0.02 => "\U0001F311",                                        // 🌑 New
            < 0.35 => litOnRight ? "\U0001F312" : "\U0001F318",           // 🌒 / 🌘
            < 0.65 => litOnRight ? "\U0001F313" : "\U0001F317",           // 🌓 / 🌗
            < 0.98 => litOnRight ? "\U0001F314" : "\U0001F316",           // 🌔 / 🌖
            _      => "\U0001F315",                                        // 🌕 Full
        };
    }

    private static double Normalize(double degrees)
    {
        var result = degrees % 360.0;
        return result < 0 ? result + 360.0 : result;
    }

    private static double SinD(double degrees) => Math.Sin(degrees * Constants.DEGREES2RADIANS);
    private static double CosD(double degrees) => Math.Cos(degrees * Constants.DEGREES2RADIANS);
}
