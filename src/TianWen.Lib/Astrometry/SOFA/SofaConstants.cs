// This C# code is derived from routines published by the International
// Astronomical Union's Standards of Fundamental Astronomy (SOFA) service
// (http://www.iausofa.org). It does not use the "iau" or "sofa" prefix
// and is not endorsed by the IAU.

using System;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Astrometry.SOFA
{
    /// <summary>
    /// Astronomical constants from the SOFA library (sofam.h).
    /// Where possible, aliases reference <see cref="Constants"/> to maintain a single source of truth.
    /// </summary>
    internal static class SofaConstants
    {
        /// <summary>Pi</summary>
        internal const double DPI = Math.PI;

        /// <summary>2Pi</summary>
        internal const double D2PI = TWOPI;

        /// <summary>Radians to degrees</summary>
        internal const double DR2D = RADIANS2DEGREES;

        /// <summary>Degrees to radians</summary>
        internal const double DD2R = DEGREES2RADIANS;

        /// <summary>Radians to arcseconds</summary>
        internal const double DR2AS = RAD2SEC;

        /// <summary>Arcseconds to radians</summary>
        internal const double DAS2R = 4.848136811095359935899141e-6;

        /// <summary>Seconds of time to radians</summary>
        internal const double DS2R = 7.272205216643039903848712e-5;

        /// <summary>Arcseconds in a full circle</summary>
        internal const double TURNAS = 1296000.0;

        /// <summary>Milliarcseconds to radians</summary>
        internal const double DMAS2R = DAS2R / 1e3;

        /// <summary>Length of tropical year B1900 (days)</summary>
        internal const double DTY = 365.242198781;

        /// <summary>Seconds per day</summary>
        internal const double DAYSEC = 86400.0;

        /// <summary>Days per Julian year</summary>
        internal const double DJY = 365.25;

        /// <summary>Days per Julian century</summary>
        internal const double DJC = 36525.0;

        /// <summary>Days per Julian millennium</summary>
        internal const double DJM = 365250.0;

        /// <summary>Reference epoch (J2000.0), Julian Date</summary>
        internal const double DJ00 = J2000BASE;

        /// <summary>Julian Date of Modified Julian Date zero</summary>
        internal const double DJM0 = MODIFIED_JULIAN_DAY_OFFSET;

        /// <summary>Reference epoch (J2000.0), Modified Julian Date</summary>
        internal const double DJM00 = 51544.5;

        /// <summary>1977 Jan 1.0 as MJD</summary>
        internal const double DJM77 = 43144.0;

        /// <summary>TT minus TAI (s)</summary>
        internal const double TTMTAI = TT_TAI_OFFSET;

        /// <summary>Astronomical unit (m, IAU 2012)</summary>
        internal const double DAU = 149597870.7e3;

        /// <summary>Speed of light (m/s)</summary>
        internal const double CMPS = 299792458.0;

        /// <summary>Light time for 1 au (s)</summary>
        internal const double AULT = DAU / CMPS;

        /// <summary>Speed of light (au per day)</summary>
        internal const double DC = DAYSEC / AULT;

        /// <summary>L_G = 1 - d(TT)/d(TCG)</summary>
        internal const double ELG = 6.969290134e-10;

        /// <summary>L_B = 1 - d(TDB)/d(TCB)</summary>
        internal const double ELB = 1.550519768e-8;

        /// <summary>TDB (s) at TAI 1977/1/1.0</summary>
        internal const double TDB0 = -6.55e-5;

        /// <summary>Schwarzschild radius of the Sun (au): 2 * 1.32712440041e20 / (2.99792458e8)^2 / 1.49597870700e11</summary>
        internal const double SRS = 1.97412574336e-8;

        // Reference ellipsoid identifiers
        internal const int WGS84 = 1;
        internal const int GRS80 = 2;
        internal const int WGS72 = 3;
    }
}
