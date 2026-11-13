using System;
using System.Linq;
using Shouldly;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Astrometry.MeteorShowers;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A year's peak is the instant the Sun reaches the peak's longitude (docs/plans/yearly-planner.md, E), held against the
/// times the IMO's 2026 and 2027 Meteor Shower Calendars publish. Pure computation: no clock, no network, no wait.
/// </summary>
public class MeteorShowerPeakTests
{
    /// <summary>The calendar's peaks are stated to the hour or a few hours' span, and the longitude to 0.1 degree (2.4 h).</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromHours(3);

    [Theory]
    [InlineData("QUA", 2026, 2026, 1, 3, 21, 0)] // "January 3 close to 21h UT"
    [InlineData("QUA", 2027, 2027, 1, 4, 3, 25)] // "January 4, 03h25m UT"
    [InlineData("PER", 2026, 2026, 8, 13, 3, 0)] // "August 13, 02h to 04h UT"
    [InlineData("PER", 2027, 2027, 8, 13, 9, 0)] // "August 13, 08h to 10h UT"
    [InlineData("GEM", 2026, 2026, 12, 14, 14, 0)] // "December 14, 14h UT"
    [InlineData("GEM", 2027, 2027, 12, 14, 20, 0)] // "centred around 20h UT"
    public void APeakLandsWithinAFewHoursOfTheImoCalendar(string code, int year, int py, int pm, int pd, int ph, int pmin)
    {
        var shower = MeteorShowerTable.ByCode(code).ShouldNotBeNull();
        var published = new DateTimeOffset(py, pm, pd, ph, pmin, 0, TimeSpan.Zero);

        var peak = MeteorShowerCalendar.PeakInstant(shower, year).ShouldNotBeNull();

        (peak - published).Duration().ShouldBeLessThan(Tolerance, $"{code} {year} solved {peak:u}, published {published:u}");
    }

    [Fact]
    public void TheSunReachesThePeakLongitudeExactly()
    {
        foreach (var shower in MeteorShowerTable.All)
        {
            var peak = MeteorShowerCalendar.PeakInstant(shower, 2027).ShouldNotBeNull();
            var error = MeteorShowerCalendar.SunLongitude(peak) - shower.PeakLambda;
            Math.Abs(((error + 540.0) % 360.0) - 180.0).ShouldBeLessThan(1e-4, shower.Code);
        }
    }

    [Fact]
    public void ALongitudeIsNotACalendarDate()
    {
        var gem2026 = MeteorShowerCalendar.PeakInstant(MeteorShowerTable.ByCode("GEM")!, 2026).ShouldNotBeNull();
        var gem2027 = MeteorShowerCalendar.PeakInstant(MeteorShowerTable.ByCode("GEM")!, 2027).ShouldNotBeNull();

        // Same longitude, one year on: about six hours later in the leap cycle, never the same clock time.
        (gem2027 - gem2026).TotalDays.ShouldBeInRange(365.0, 366.0);
        (gem2027 - gem2026).TotalDays.ShouldNotBe(365.0, 0.1);
    }

    [Fact]
    public void ANewYearNightFindsTheQuadrantidsOfTheYearAhead()
    {
        var peak = MeteorShowerCalendar.PeakNear(MeteorShowerTable.ByCode("QUA")!, new DateTimeOffset(2026, 12, 30, 0, 0, 0, TimeSpan.Zero))
            .ShouldNotBeNull();

        peak.Year.ShouldBe(2027);
        peak.Month.ShouldBe(1);
    }

    [Fact]
    public void AWindowThatStraddlesLongitudeZeroIsJudgedByDistanceFromItsStart()
    {
        var straddling = new MeteorShower("TST", 0, "Test", 350, 10, 0, 0, 0, 0, 0, 0, 0, 0);

        straddling.IsActiveAt(355).ShouldBeTrue();
        straddling.IsActiveAt(5).ShouldBeTrue();
        straddling.IsActiveAt(180).ShouldBeFalse();
    }
}

/// <summary>
/// The showers active on a night at a site, each with its radiant moved by the drift and its expected hourly rate, over the
/// night calendar's own <see cref="NightSummary"/> (no second dark window, no second Moon).
/// </summary>
public class MeteorShowerNightTests
{
    private const double NorthLatitude = 50.0;
    private const double SouthLatitude = -60.0;

    /// <summary>A night built by hand, so the Moon is the test's to set: <paramref name="moonFree"/> of the dark window.</summary>
    private static NightSummary Night(DateOnly evening, double illumination, TimeSpan moonFree, double hours = 6)
    {
        var start = new DateTimeOffset(evening.ToDateTime(new TimeOnly(21, 0)), TimeSpan.Zero);
        return new NightSummary(evening, start, start.AddHours(hours), EventType.AmateurAstronomicalTwilight,
            illumination, true, moonFree, null);
    }

    private static MeteorShowerNight Find(System.Collections.Immutable.ImmutableArray<MeteorShowerNight> nights, string code)
        => nights.Single(n => n.Shower.Code == code);

    [Fact]
    public void ARadiantBelowTheHorizonAllNightGivesARateOfZero()
    {
        // Perseid radiant at +58 never clears the horizon at 60 S (highest altitude -28 degrees).
        var night = Night(new DateOnly(2026, 8, 12), 0.0, TimeSpan.FromHours(6));

        var perseids = Find(MeteorShowerNights.For(SouthLatitude, 0.0, night), "PER");

        perseids.MeanSinAltitude.ShouldBe(0.0);
        perseids.ExpectedRate.ShouldBe(0.0);
    }

    [Fact]
    public void AHighRadiantOnADarkNightGivesARateBelowTheZhrAndAboveZero()
    {
        var night = Night(new DateOnly(2026, 8, 12), 0.0, TimeSpan.FromHours(6));

        var perseids = Find(MeteorShowerNights.For(NorthLatitude, 10.0, night), "PER");

        perseids.ExpectedRate.ShouldBeGreaterThan(10.0);
        perseids.ExpectedRate.ShouldBeLessThanOrEqualTo(perseids.Shower.Zhr);
        perseids.LimitingMagnitude.ShouldBe(MeteorShowerNights.ReferenceLimitingMagnitude);
    }

    [Fact]
    public void AFullMoonLowersTheRate()
    {
        var evening = new DateOnly(2026, 12, 13);
        var dark = MeteorShowerNights.For(NorthLatitude, 10.0, Night(evening, 0.0, TimeSpan.FromHours(6)));
        var moonlit = MeteorShowerNights.For(NorthLatitude, 10.0, Night(evening, 1.0, TimeSpan.Zero));

        var darkRate = Find(dark, "GEM").ExpectedRate;
        var moonRate = Find(moonlit, "GEM").ExpectedRate;

        moonRate.ShouldBeLessThan(darkRate);
        Find(moonlit, "GEM").LimitingMagnitude.ShouldBeLessThan(MeteorShowerNights.ReferenceLimitingMagnitude);
    }

    [Fact]
    public void AFullMoonBelowTheHorizonCostsNothing()
    {
        var evening = new DateOnly(2026, 12, 13);
        var dark = MeteorShowerNights.For(NorthLatitude, 10.0, Night(evening, 0.0, TimeSpan.FromHours(6)));
        var moonDown = MeteorShowerNights.For(NorthLatitude, 10.0, Night(evening, 1.0, TimeSpan.FromHours(6)));

        Find(moonDown, "GEM").ExpectedRate.ShouldBe(Find(dark, "GEM").ExpectedRate);
    }

    [Fact]
    public void ANightOutsideEveryActivityWindowListsNoShower()
    {
        var night = Night(new DateOnly(2026, 3, 1), 0.0, TimeSpan.FromHours(6));

        MeteorShowerNights.For(NorthLatitude, 10.0, night).ShouldBeEmpty();
    }

    [Fact]
    public void TheRadiantIsMovedByTheDriftToTheNight()
    {
        // The Geminids drift about +1 degree of RA a day: two days before the peak it is two degrees west of the table's.
        var early = Find(MeteorShowerNights.For(NorthLatitude, 10.0, Night(new DateOnly(2026, 12, 11), 0.0, TimeSpan.FromHours(6))), "GEM");
        var late = Find(MeteorShowerNights.For(NorthLatitude, 10.0, Night(new DateOnly(2026, 12, 15), 0.0, TimeSpan.FromHours(6))), "GEM");

        (late.RadiantRaDeg - early.RadiantRaDeg).ShouldBe(4.0, 0.5);
    }

    [Fact]
    public void ThePeakNightSaysSo()
    {
        var peakNight = Find(MeteorShowerNights.For(NorthLatitude, 10.0, Night(new DateOnly(2026, 12, 14), 0.0, TimeSpan.FromHours(6))), "GEM");
        var earlyNight = Find(MeteorShowerNights.For(NorthLatitude, 10.0, Night(new DateOnly(2026, 12, 8), 0.0, TimeSpan.FromHours(6))), "GEM");

        peakNight.IsPeakNight.ShouldBeTrue();
        earlyNight.IsPeakNight.ShouldBeFalse();
    }
}
