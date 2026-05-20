using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class TimeUtilsTests
{
    [Fact]
    public void JulianYearsSinceJ2000_AtJ2000Epoch_IsZero()
    {
        var j2000 = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
        j2000.JulianYearsSinceJ2000().ShouldBe(0.0, tolerance: 1e-12);
    }

    [Fact]
    public void JulianYearsSinceJ2000_ExactJulianYearLater_IsOne()
    {
        // Julian year = 365.25 days exactly.
        var oneJulianYearLater = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero)
            .AddDays(365.25);
        oneJulianYearLater.JulianYearsSinceJ2000().ShouldBe(1.0, tolerance: 1e-12);
    }

    [Fact]
    public void JulianYearsSinceJ2000_TwentySixYearsLater_MatchesHandCalc()
    {
        // 2026-05-20 00:00:00 UTC -> 9626.5 days since J2000.0
        //   2000-01-01T12:00:00 to 2026-05-20T00:00:00 = ?
        //   26 full years (Gregorian)... easier to express as the absolute
        //   day count and divide by 365.25.
        var target = new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero);
        // Hand-computed: (2026 - 2000) years of mostly-Gregorian (with 7 leap days
        // 2000, 04, 08, 12, 16, 20, 24) = 26*365 + 7 = 9497 days from Jan 1 2000.
        // Subtract 0.5 days (J2000 is noon on Jan 1) = 9496.5.
        // Add days from Jan 1 to May 20 in 2026 (non-leap) = 31+28+31+30+19 = 139.
        // Total = 9635.5 days. Divide by 365.25 = 26.3806 yr.
        target.JulianYearsSinceJ2000().ShouldBe(26.3806, tolerance: 1e-3);
    }

    [Fact]
    public void JulianYearsSinceJ2000_BeforeJ2000_IsNegative()
    {
        // 1990-01-01T12:00:00Z is exactly 10 Julian years before J2000.0
        // (10 * 365.25 = 3652.5 days, and 2000-01-01 minus 1990-01-01 = 3653 days
        //  for the calendar-year span; with the noon offset and a 2000 leap year
        //  it works out to ~-10.005 yr).
        var pre = new DateTimeOffset(1990, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var y = pre.JulianYearsSinceJ2000();
        y.ShouldBeLessThan(-9.99);
        y.ShouldBeGreaterThan(-10.01);
    }
}
