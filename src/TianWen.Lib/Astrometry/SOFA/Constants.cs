using System;

namespace TianWen.Lib.Astrometry.SOFA;

internal static class Constants
{
    internal const double HOURS2RADIANS = Math.PI / 12.0d;
    internal const double DEGREES2RADIANS = Math.PI / 180.0d;
    internal const double RADIANS2HOURS = 12.0d / Math.PI;
    internal const double RADIANS2DEGREES = 180.0d / Math.PI;

    internal const double OLE_AUTOMATION_JULIAN_DATE_OFFSET = 2415018.5; // Offset of OLE automation dates from Julian dates
    internal const double JULIAN_DATE_MINIMUM_VALUE = -657435.0 + OLE_AUTOMATION_JULIAN_DATE_OFFSET; // Minimum valid Julian date value (1/1/0100 00:00:00) - because DateTime.FromOADate has this limit
    internal const double JULIAN_DATE_MAXIMUM_VALUE = 2958465.99999999 + OLE_AUTOMATION_JULIAN_DATE_OFFSET; // Maximum valid Julian date value (31/12/9999 23:59:59.999) - because DateTime.FromOADate has this limit

    internal const double TT_TAI_OFFSET = 32.184; // 32.184 seconds
    internal const double MODIFIED_JULIAN_DAY_OFFSET = 2400000.5; // This is the offset of Modified Julian dates from true Julian dates
    internal const double TROPICAL_YEAR_IN_DAYS = 365.24219;
    internal const double J2000BASE = 2451545.0; // TDB Julian date of epoch J2000.0.

    internal const double STANDARD_PRESSURE = 1013.25d; // Standard atmospheric pressure (hPa)
    internal const double ABSOLUTE_ZERO_CELSIUS = -273.15d; // Absolute zero expressed in Celsius
    internal const double KMAU = 149597870.0; //Astronomical Unit in kilometres.
    internal const double MAU = 149597870000.0; //Astronomical Unit in meters.
    internal const double C = 173.14463348; // Speed of light in AU/Day.
    internal const double GS = 1.32712438E+20; // Heliocentric gravitational constant.
    internal const double EARTHRAD = 6378.14; //Radius of Earth in kilometres.
    internal const double F = 0.00335281; //Earth ellipsoid flattening.
    internal const double OMEGA = 0.00007292115; //Rotational angular velocity of Earth in radians/sec.
    internal const double TWOPI = 6.2831853071795862; //Value of pi in radians.
    internal const double RAD2SEC = 206264.80624709636; //Angle conversion constants.
    internal const double DEG2RAD = 0.017453292519943295;
    internal const double RAD2DEG = 57.295779513082323;
    internal const double SIDEREAL_RATE = 15.0417; // approx. rate of sky moving in arcseconds/second

    // Physical constants
    internal const double MOON_RADIUS = 1737.0; // km
    internal const double EARTH_RADIUS = 6378.0; // km
    internal const double SUN_RADIUS = 696342.0; // km
    internal const double MERCURY_RADIUS = 2439.7; // km
    internal const double VENUS_RADIUS = 2439.7; // km
    internal const double MARS_RADIUS = 3396.2; // km
    internal const double JUPITER_RADIUS = 69911.0; // km
    internal const double SATURN_RADIUS = 6051.8; // km
    internal const double NEPTUNE_RADIUS = 24767.0; // km
    internal const double URANUS_RADIUS = 24973.0; // km
    internal const double PLUTO_RADIUS = 1153.0; // km

    // Fixed event definitions
    internal const double SUN_RISE = -50.0 / 60.0; // degrees
    internal const double CIVIL_TWILIGHT = -6.0; // degrees
    internal const double NAUTICAL_TWILIGHT = -12.0; // degrees
    internal const double AMATEUR_ASRONOMICAL_TWILIGHT = -15.0; // degrees
    internal const double ASTRONOMICAL_TWILIGHT = -18.0; // degrees
}
