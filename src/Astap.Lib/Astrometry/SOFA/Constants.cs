using System;

namespace Astap.Lib.Astrometry.SOFA
{
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
    }
}
