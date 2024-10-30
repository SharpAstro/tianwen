using System;
using System.Runtime.CompilerServices;
using WorldWideAstronomy;
using static TianWen.Lib.Astrometry.Constants;

namespace TianWen.Lib.Astrometry;

public static class TimeUtils
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToJulian(this DateTimeOffset dateTimeOffset) => dateTimeOffset.UtcDateTime.ToJulian();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToJulian(this DateTime dt) => dt.ToOADate() + OLE_AUTOMATION_JULIAN_DATE_OFFSET;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTime FromJulian(double utc1, double utc2) => DateTime.SpecifyKind(DateTime.FromOADate(utc1 - OLE_AUTOMATION_JULIAN_DATE_OFFSET + utc2), DateTimeKind.Utc);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToSOFAUtcJd(this DateTimeOffset dateTimeOffset, out double utc1, out double utc2)
        => dateTimeOffset.UtcDateTime.ToSOFAUtcJd(out utc1, out utc2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToSOFAUtcJd(this DateTime dt, out double utc1, out double utc2)
    {
        utc1 = default;
        utc2 = default;
        // First calculate the UTC Julian date, then convert this to the equivalent TAI Julian date then convert this to the equivalent TT Julian date
        _ = WWA.wwaDtf2d("UTC", dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second + dt.Millisecond / 1000.0d, ref utc1, ref utc2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToSOFAUtcJdTT(this DateTimeOffset dto, out double utc1, out double utc2, out double tt1, out double tt2)
        => dto.UtcDateTime.ToSOFAUtcJdTT(out utc1, out utc2, out tt1, out tt2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ToSOFAUtcJdTT(this DateTime dt, out double utc1, out double utc2, out double tt1, out double tt2)
    {
        double tai1 = default, tai2 = default;

        dt.ToSOFAUtcJd(out utc1, out utc2);
        _ = WWA.wwaUtctai(utc1, utc2, ref tai1, ref tai2);
        tt1 = default;
        tt2 = default;
        _ = WWA.wwaTaitt(tai1, tai2, ref tt1, ref tt2);
    }

    private static readonly DateTime JD2000 = DateTimeOffset.Parse("2000-01-01T12:00:00Z").UtcDateTime;

    public static double JulianDaysSinceJ2000(this DateTime dt) => (dt - JD2000).TotalDays;
}
