using System;
using System.Globalization;

namespace Astap.Lib.Astrometry
{
    public static class Utils
    {
        public static double HMSToDegree(string? hms)
        {
            const double minToDeg = 15 / 60.0;
            const double secToDeg = 15 / 3600.0;

            var split = hms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (split?.Length == 3
                && double.TryParse(split[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var hours)
                && double.TryParse(split[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var min)
                && double.TryParse(split[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var sec)
            )
            {
                return (15 * hours) + (min * minToDeg) + (sec * secToDeg);
            }
            else
            {
                return double.NaN;
            }
        }

        public static double DMSToDegree(string dms)
        {
            const double minToDeg = 15 / 60.0;
            const double secToDeg = 15 / 3600.0;

            var split = dms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (split?.Length == 3
                && double.TryParse(split[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var deg)
                && double.TryParse(split[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var min)
                && double.TryParse(split[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var sec)
            )
            {
                return deg + (min * minToDeg) + (sec * secToDeg);
            }
            else
            {
                return double.NaN;
            }
        }
    }
}
