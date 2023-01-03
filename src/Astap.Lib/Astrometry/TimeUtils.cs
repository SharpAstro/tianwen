using System;
using System.Runtime.CompilerServices;
using WorldWideAstronomy;

namespace Astap.Lib.Astrometry;

public static class TimeUtils
{
    const double OAOffset = 2415018.5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToJulian(this DateTimeOffset dateTimeOffset) => dateTimeOffset.UtcDateTime.ToJulian();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToJulian(this DateTime dt) => dt.ToOADate() + OAOffset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static DateTime FromJulian(double utc1, double utc2) => DateTime.SpecifyKind(DateTime.FromOADate(utc1 - OAOffset + utc2), DateTimeKind.Utc);

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
}
