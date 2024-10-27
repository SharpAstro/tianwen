using System;

namespace TianWen.Lib;

public static class TimeSpanHelper
{
    public static TimeSpan Round(this TimeSpan span, TimeSpanRoundingType type, MidpointRounding mode = MidpointRounding.ToEven) => type switch
    {
        TimeSpanRoundingType.Second => TimeSpan.FromSeconds(Math.Round(span.TotalSeconds, 0, mode)),
        TimeSpanRoundingType.TenthMinute => TimeSpan.FromSeconds(Math.Round(span.TotalSeconds / 6, 0, mode) * 6),
        TimeSpanRoundingType.QuarterMinute => TimeSpan.FromSeconds(Math.Round(span.TotalSeconds / 15, 0, mode) * 15),
        TimeSpanRoundingType.HalfMinute => TimeSpan.FromSeconds(Math.Round(span.TotalSeconds / 30, 0, mode) * 30),
        TimeSpanRoundingType.Minute => TimeSpan.FromSeconds(Math.Round(span.TotalSeconds / 60, 0, mode) * 60),
        TimeSpanRoundingType.QuarterHour => TimeSpan.FromMinutes(Math.Round(span.TotalMinutes / 15, 0, mode) * 15),
        TimeSpanRoundingType.HalfHour => TimeSpan.FromMinutes(Math.Round(span.TotalMinutes / 30, 0, mode) * 30),
        TimeSpanRoundingType.Hour => TimeSpan.FromMinutes(Math.Round(span.TotalMinutes / 60, 0, mode) * 60),
        _ => throw new ArgumentException($"Unknown rounding type {type}", nameof(type))
    };

    public static TimeSpan ModuloHours(this TimeSpan span, int hours) => span.Modulo(TimeSpan.FromHours(hours));

    public static TimeSpan Modulo(this TimeSpan span, TimeSpan mod) => TimeSpan.FromHours(span.TotalHours % mod.TotalHours);
}

public enum TimeSpanRoundingType
{
    Second,
    TenthMinute,
    QuarterMinute,
    HalfMinute,
    Minute,
    QuarterHour,
    HalfHour,
    Hour,
}
