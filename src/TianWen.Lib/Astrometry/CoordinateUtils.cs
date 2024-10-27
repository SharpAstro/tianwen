using System;
using System.Globalization;

namespace TianWen.Lib.Astrometry;

public static class CoordinateUtils
{
    /// <summary>
    /// Flexible routine to range a number into a given range between a lower and an higher bound.
    /// </summary>
    /// <param name="value">Value to be ranged</param>
    /// <param name="lowerBound">Lowest value of the range</param>
    /// <param name="lowerEqual">Boolean flag indicating whether the ranged value can have the lower bound value</param>
    /// <param name="upperBound">Highest value of the range</param>
    /// <param name="upperEqual">Boolean flag indicating whether the ranged value can have the upper bound value</param>
    /// <returns>The ranged nunmber as a double</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the lower bound is greater than the upper bound.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if LowerEqual and UpperEqual are both false and the ranged value equals
    /// one of these values. This is impossible to handle as the algorithm will always violate one of the rules!</exception>
    /// <remarks>
    /// UpperEqual and LowerEqual switches control whether the ranged value can be equal to either the upper and lower bounds. So,
    /// to range an hour angle into the range 0 to 23.999999.. hours, use this call:
    /// <code>RangedValue = Range(InputValue, 0.0, true, 24.0, false)</code>
    /// <para>The input value will be returned in the range where 0.0 is an allowable value and 24.0 is not i.e. in the range 0..23.999999..</para>
    /// <para>It is not permissible for both LowerEqual and UpperEqual to be false because it will not be possible to return a value that is exactly equal
    /// to either lower or upper bounds. An exception is thrown if this scenario is requested.</para>
    /// </remarks>
    public static double Range(double value, double lowerBound, bool lowerEqual, double upperBound, bool upperEqual)
    {
        double ModuloValue;
        if (lowerBound >= upperBound)
            throw new ArgumentOutOfRangeException(nameof(lowerBound), lowerBound, "LowerBound must be less than UpperBound");

        ModuloValue = upperBound - lowerBound;

        if (lowerEqual)
        {
            if (upperEqual)
            {
                do
                {
                    if (value < lowerBound)
                    {
                        value += ModuloValue;
                    }
                    if (value > upperBound)
                    {
                        value -= ModuloValue;
                    }
                }
                while (!(value >= lowerBound) & value <= upperBound);
            }
            else
            {
                do
                {
                    if (value < lowerBound)
                    {
                        value += ModuloValue;
                    }
                    if (value >= upperBound)
                    {
                        value -= ModuloValue;
                    }
                }
                while (!(value >= lowerBound) & value < upperBound);
            }
        }
        else if (upperEqual)
        {
            do
            {
                if (value <= lowerBound)
                {
                    value += ModuloValue;
                }
                if (value > upperBound)
                {
                    value -= ModuloValue;
                }
            }
            while (!(value > lowerBound) & value <= upperBound);
        }
        else
        {
            if (value == lowerBound)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The supplied value equals the LowerBound. This can not be ranged when LowerEqual and UpperEqual are both false ");
            }
            if (value == upperBound)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The supplied value equals the UpperBound. This can not be ranged when LowerEqual and UpperEqual are both false ");
            }

            do
            {
                if (value <= lowerBound)
                {
                    value += ModuloValue;
                }
                if (value >= upperBound)
                {
                    value -= ModuloValue;
                }
            }
            while (!(value > lowerBound) & value < upperBound);
        }
        return value;
    }

    public static double ConditionHA(double ha) => Range(ha, -12, true, +12, true);

    public static double ConditionRA(double ra) => Range(ra, 0, true, 24, false);

    public static double HMSToHours(string? hms)
    {
        const double minToHours = 1.0 / 60.0;
        const double secToHours = 1.0 / 3600.0;

        if (hms is null)
        {
            return double.NaN;
        }

        var split = hms.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec)
        )
        {
            return Math.FusedMultiplyAdd(sec, secToHours, Math.FusedMultiplyAdd(min, minToHours, hours));
        }
        else if (split?.Length == 2
            && double.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out min)
        )
        {
            return Math.FusedMultiplyAdd(min, minToHours, hours);
        }
        else
        {
            return double.NaN;
        }
    }

    public static double HMSToDegree(string? hms)
    {
        const double minToDegs = 15.0 / 60.0;
        const double secToDegs = 15.0 / 3600.0;

        var split = hms?.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec)
        )
        {
            return Math.FusedMultiplyAdd(sec, secToDegs, Math.FusedMultiplyAdd(min, minToDegs, hours * 15.0));
        }
        else if (split?.Length == 2
            && double.TryParse(split[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out min)
        )
        {
            return Math.FusedMultiplyAdd(min, minToDegs, hours * 15.0);
        }
        else
        {
            return double.NaN;
        }
    }

    public static string HoursToHMS(double hours)
    {
        var hoursInt = (int)Math.Floor(hours);
        var min = (hours - hoursInt) * 60d;
        var minInt = (int)Math.Floor(min);
        var sec = (min - minInt) * 60d;
        var secInt = (int)Math.Floor(sec);
        var secFrac = (int)Math.Round((sec - secInt) * 1000d);
        if (secFrac >= 1000)
        {
            secFrac -= 1000;
            secInt += 1;
        }
        if (secInt >= 60)
        {
            secInt -= 60;
            minInt += 1;
        }
        if (minInt >= 60)
        {
            minInt -= 60;
            hoursInt += 1;
        }
        var hasMS = secFrac > 0;
        return $"{hoursInt:D2}:{minInt:D2}:{secInt:D2}{(hasMS ? $".{secFrac:D3}" : "")}";
    }

    /// <summary>
    /// Hours to HH:MM.T (LX200 legacy format), where T is a tenth of a minute.
    /// </summary>
    /// <param name="hours">Hours in 24h format</param>
    /// <returns>HH:MM.T formatted string</returns>
    public static string HoursToHMT(double hours)
    {
        var span = TimeSpan.FromHours(Math.Abs(hours)).Round(TimeSpanRoundingType.TenthMinute).ModuloHours(24);

        return $"{span.Hours:D2}:{span.Minutes:D2}.{span.Seconds / 6:0}";
    }

    public static string DegreesToDMS(double degrees, bool withPlus = true)
        => $"{(Math.Sign(degrees) >= 0 ? (withPlus ? "+" : "") : "-")}{HoursToHMS(Math.Abs(degrees))}";

    public static double DMSToDegree(string dms)
    {
        const double minToDeg = 1.0 / 60.0;
        const double secToDeg = 1.0 / 3600.0;

        var span = dms.AsSpan().Trim();
        int sign;
        if (span.Length > 0 && span[0] == '-')
        {
            sign = -1;
            span = span[1..];
        }
        else
        {
            sign = 1;
        }

        var split = new string(span).Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (split?.Length == 3
            && double.TryParse(split[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var deg)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            && double.TryParse(split[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var sec)
        )
        {
            return sign * (Math.Abs(deg) + (min * minToDeg) + (sec * secToDeg));
        }
        else if (split?.Length == 2
            && double.TryParse(split[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out deg)
            && double.TryParse(split[1], NumberStyles.None, CultureInfo.InvariantCulture, out min)
        )
        {
            return sign * Math.FusedMultiplyAdd(min, minToDeg, Math.Abs(deg));
        }
        else
        {
            return double.NaN;
        }
    }


    /// <summary>
    /// Calculates precession, RA, Dec is in 24h, -90..90 degrees format in <paramref name="epoch1"/>.
    /// Response is the same format but in epoch <paramref name="epoch2"/>.
    /// </summary>
    /// <param name="ra1Rad">RA for <paramref name="epoch1"/> in 24h</param>
    /// <param name="dec1Rad">Dec for <paramref name="epoch1"/> in -90..90 degrees</param>
    /// <param name="epoch1">Origin epoch (years AD)</param>
    /// <param name="epoch2">Target epoch (years AD)</param>
    /// <returns>(RA, Dec), in (24h, -90..90 degrees), precessed to <paramref name="epoch2"/>, where the epoch is in years AD.</returns>
    public static (double RA2, double Dec2) Precess(double ra1, double dec1, double epoch1, double epoch2)
    {
        const double ConvH = Math.PI / 12.0;
        const double ConvD = Math.PI / 180.0;

        var (ra2Rad, dec2Dec) = PrecessRadians(ra1 * ConvH, dec1 * ConvD, epoch1, epoch2);

        return (ra2Rad /= ConvH, dec2Dec /= ConvD);
    }

    /// <summary>
    /// Calculates precession.
    /// Original comment:
    /// Herget precession, see p. 9 of Publ. Cincinnati Obs., No. 24.
    /// </summary>
    /// <param name="ra1Rad">RA for <paramref name="epoch1"/> in radians</param>
    /// <param name="dec1Rad">Dec for <paramref name="epoch1"/> in radians</param>
    /// <param name="epoch1">Origin epoch (years AD)</param>
    /// <param name="epoch2">Target epoch (years AD)</param>
    /// <returns>(RA, Dec), in radians, precessed to <paramref name="epoch2"/>, where the epoch is in years AD.</returns>
    public static (double RA, double Dec) PrecessRadians(double ra1Rad, double dec1Rad, double epoch1, double epoch2)
    {
        var cdr = Math.PI / 180.0;
        var csr = cdr / 3600.0;
        var a = Math.Cos(dec1Rad);
        var x1 = new double[] { a * Math.Cos(ra1Rad), a * Math.Sin(ra1Rad), Math.Sin(dec1Rad) };
        var t = 0.001 * (epoch2 - epoch1);
        var st = 0.001 * (epoch1 - 1900.0);
        a = csr * t * (23042.53 + st * (139.75 + 0.06 * st) + t * (30.23 - 0.27 * st + 18.0 * t));
        var b = csr * t * t * (79.27 + 0.66 * st + 0.32 * t) + a;
        var c = csr * t * (20046.85 - st * (85.33 + 0.37 * st) + t * (-42.67 - 0.37 * st - 41.8 * t));
        var sina = Math.Sin(a);
        var sinb = Math.Sin(b);
        var sinc = Math.Sin(c);
        var cosa = Math.Cos(a);
        var cosb = Math.Cos(b);
        var cosc = Math.Cos(c);
        var r = new double[3,3];
        r[0, 0] = cosa * cosb * cosc - sina * sinb;
        r[0, 1] = -cosa * sinb - sina * cosb * cosc;
        r[0, 2] = -cosb * sinc;
        r[1, 0] = sina * cosb + cosa * sinb * cosc;
        r[1, 1] = cosa * cosb - sina * sinb * cosc;
        r[1, 2] = -sinb * sinc;
        r[2, 0] = cosa * sinc;
        r[2, 1] = -sina * sinc;
        r[2, 2] = cosc;
        var x2 = new double[3];
        for (var i = 0; i < 3; i++)
        {
            x2[i] = r[i, 0] * x1[0] + r[i, 1] * x1[1] + r[i, 2] * x1[2];
        }
        var ra2 = Math.Atan2(x2[1], x2[0]);
        if (ra2 < 0.0)
        {
            ra2 += 2.0 * Math.PI;
        }
        var dec2 = Math.Asin(x2[2]);
        return (ra2, dec2);
    }
}
