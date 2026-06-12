using System;
using DIR.Lib;
using Shouldly;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for the Stellarium-style sky-map time scrubber: the pure
/// <see cref="SkyMapState.FormatOffset"/> and <see cref="SkyMapState.ComputeMidnightOffset"/>
/// helpers, plus a guard that a scrubbed viewing time actually moves the sun-altitude band
/// (and therefore the sky palette) the way the live <c>viewingTime = baseTime + TimeOffset</c>
/// derivation in <see cref="SkyMapTab{TSurface}.Render"/> relies on.
/// </summary>
public class SkyMapTimeScrubTests
{
    [Theory]
    [InlineData(0, "+0")]              // zero -> live
    [InlineData(0.4, "+0")]           // sub-minute truncates to zero
    [InlineData(180, "+3h")]          // single unit
    [InlineData(-300, "-5h")]         // single unit, negative
    [InlineData(-90, "-1h 30m")]      // two units, hour + minute
    [InlineData(65, "+1h 5m")]        // no leading-zero padding on the minor unit
    [InlineData(30, "+30m")]          // sub-hour
    [InlineData(-10, "-10m")]         // single minute unit
    [InlineData(3060, "+2d 3h")]      // 2 days 3 hours
    [InlineData(-1560, "-1d 2h")]     // 1 day 2 hours, negative
    [InlineData(10080, "+1w")]        // exactly one week
    [InlineData(20160, "+2w")]        // two weeks
    [InlineData(12960, "+1w 2d")]     // 9 days -> 1 week 2 days
    public void FormatOffset_RendersLargestTwoNonZeroUnits(double totalMinutes, string expected)
    {
        var offset = TimeSpan.FromMinutes(totalMinutes);

        SkyMapState.FormatOffset(offset).ShouldBe(expected);
    }

    [Theory]
    [InlineData(21, 30, 150)]    // evening -> jump forward to tonight's upcoming 00:00 (+2h30m)
    [InlineData(2, 0, -120)]     // small hours -> jump back to the night's 00:00 (-2h)
    [InlineData(11, 59, -719)]   // just before noon boundary -> back to this morning's 00:00
    [InlineData(12, 1, 719)]     // just after noon boundary -> forward to tomorrow's 00:00
    [InlineData(0, 0, 0)]        // already at midnight -> no shift
    [InlineData(12, 0, 720)]     // exactly noon -> forward to tomorrow's 00:00 (+12h)
    public void ComputeMidnightOffset_LandsOnTheCurrentNightsMidnight(int hour, int minute, int expectedMinutes)
    {
        // Date and offset are arbitrary -- the result is a frame-independent duration.
        var nowLocal = new DateTimeOffset(2026, 6, 12, hour, minute, 0, TimeSpan.FromHours(2));

        var offset = SkyMapState.ComputeMidnightOffset(nowLocal);

        offset.TotalMinutes.ShouldBe(expectedMinutes, 1e-6);
    }

    [Fact]
    public void ScrubbingTwelveHoursFromWinterNoon_MovesSkyFromDayToNight()
    {
        // Vienna in mid-January: clock noon is solidly day (sun ~+20 deg), and noon + 12h is
        // deep astronomical night (lower culmination ~-62 deg). This mirrors the Render
        // derivation viewingTime = baseTime + TimeOffset feeding GetSunAltitudeDegCached.
        const double siteLat = 48.21;
        const double siteLon = 16.37;
        var baseTime = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(1));
        var scrubbed = baseTime + TimeSpan.FromHours(12); // TimeOffset = +12h

        // Separate states so the 10 s sun-altitude cache never bleeds one band into the other.
        var dayAlt = new SkyMapState().GetSunAltitudeDegCached(baseTime, siteLat, siteLon);
        var nightAlt = new SkyMapState().GetSunAltitudeDegCached(scrubbed, siteLat, siteLon);

        dayAlt.ShouldBeGreaterThan(5.0);    // daylight band
        nightAlt.ShouldBeLessThan(-18.0);   // full-night band

        var dayColor = SkyMapState.SkyBackgroundColorForSunAltitude(dayAlt);
        var nightColor = SkyMapState.SkyBackgroundColorForSunAltitude(nightAlt);

        dayColor.ShouldBe(new RGBAColor32(0x28, 0x34, 0x50, 0xFF));   // daylight dusty blue
        nightColor.ShouldBe(new RGBAColor32(0x02, 0x03, 0x08, 0xFF)); // night
        nightColor.ShouldNotBe(dayColor);
    }
}
