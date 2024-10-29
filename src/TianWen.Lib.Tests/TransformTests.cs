using TianWen.Lib.Astrometry.SOFA;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;
using Microsoft.Extensions.Time.Testing;

namespace TianWen.Lib.Tests;

public class TransformTests
{
    [Theory]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", EventType.SunRiseSunset, -37.884546970458274d, 145.1663117892053d, 110, false, 1, 1, 2, "06:07:00", "19:59:00")]
    [InlineData("2012-01-13T00:00:00-05:00", EventType.MoonRiseMoonSet, 75d, -75d, 100, true, 1, 1, 15, "22:41:40", "09:33:00")]
    [InlineData("2012-01-14T00:00:00-05:00", EventType.MoonRiseMoonSet, 75d, -75d, 100, true, 0, 1, 15, "09:00:00")]
    [InlineData("2022-12-03T00:00:00+11:00", EventType.MoonRiseMoonSet, -37.88444444444444, 145.16583333333335, 120, true, 1, 1, 3, "15:36:00", "02:57:08.5871021")]
    [InlineData("2022-12-01T00:00:00+11:00", EventType.SunRiseSunset, -37.8845, 145.1663, 100, false, 1, 1, 1, "05:51:00", "20:25:00")]
    [InlineData("2022-11-06T00:00:00+11:00", EventType.AstronomicalTwilight, -37.8845, 145.1663, 120, false, 1, 1, 1, "04:27:00", "21:40:00")]
    [InlineData("2022-12-01T00:00:00+11:00", EventType.AstronomicalTwilight, -37.8845, 145.1663, 120, false, 1, 1, 1, "04:00:00", "22:17:00")]
    [InlineData("2022-12-01T00:00:00+1:00", EventType.AstronomicalTwilight, 51.395, 8.064, 400, false, 1, 1, 3, "06:09:00", "18:23:00")]
    [InlineData("2022-12-21T00:00:00+1:00", EventType.AstronomicalTwilight, 51.395, 8.064, 400, false, 1, 1, 3, "06:26:14", "18:25:00")]
    public void GivenDateEventPositionAndOffsetWhenCalcRiseAndSetTimesTheyAreReturned(string dtoStr, EventType eventType, double lat, double @long, double elevation, bool expAbove, int expRise, int expSet, double accuracy, params string[] dateTimeStrs)
    {
        var dto = DateTimeOffset.Parse(dtoStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        bool actualAboveHorizon;
        IReadOnlyList<TimeSpan> actualRiseEvents;
        IReadOnlyList<TimeSpan> actualSetEvents;
        var transform = new Transform(TimeProvider.System)
        {
            SiteLatitude = lat,
            SiteLongitude = @long,
            SiteElevation = elevation,
            SitePressure = 1024,
            SiteTemperature = 20,
            Refraction = true,
            DateTimeOffset = dto - dto.TimeOfDay
        };

        (actualAboveHorizon, actualRiseEvents, actualSetEvents) = transform.EventTimes(eventType);

        transform.DateTime.ShouldBe(transform.DateTimeOffset.UtcDateTime);
        actualAboveHorizon.ShouldBe(expAbove);
        actualRiseEvents.Count.ShouldBe(expRise);
        actualSetEvents.Count.ShouldBe(expSet);

        int idx = 0;

        for (var i = 0; i < expRise; i++)
        {
            var expHours = TimeSpan.ParseExact(dateTimeStrs[idx++], "c", CultureInfo.InvariantCulture);

            actualRiseEvents[i].ShouldBeInRange(expHours - TimeSpan.FromMinutes(accuracy), expHours + TimeSpan.FromMinutes(accuracy));
        }

        for (var i = 0; i < expSet; i++)
        {
            var expHours = TimeSpan.ParseExact(dateTimeStrs[idx++], "c", CultureInfo.InvariantCulture);

            actualSetEvents[i].ShouldBeInRange(expHours - TimeSpan.FromMinutes(accuracy), expHours + TimeSpan.FromMinutes(accuracy));
        }
    }

    [Theory]
    [InlineData(10.7382722222222, -59.8841527777778, 2459885.98737d, 145.1663117892053d, -37.884546970458274d, 120, 9.41563888888889d, 169.50725d, 10.752333552022904d, -59.99747614464261d)]
    public void GivenJ2000CoordsAndLocationWhenTransformingThenAltAzAndTopocentricIsReturned(double ra2000, double dec2000, double julianUTC, double @long, double lat, double elevation, double expAlt, double expAz, double expRaTopo, double expDecTopo)
    {
        // given
        var transform = new Transform(TimeProvider.System)
        {
            SiteLatitude = lat,
            SiteLongitude = @long,
            SiteElevation = elevation,
            JulianDateUTC = julianUTC
        };
        transform.SetJ2000(ra2000, dec2000);

        // when/then
        transform.ElevationTopocentric.ShouldBeInRange(expAlt - 0.2, expAlt + 0.2);
        transform.AzimuthTopocentric.ShouldBeInRange(expAz - 0.2, expAz + 0.2);
        transform.RATopocentric.ShouldBeInRange(expRaTopo - 0.2, expRaTopo + 0.2);
        transform.DECTopocentric.ShouldBeInRange(expDecTopo - 0.2, expDecTopo + 0.2);
    }

    [Theory]
    [InlineData("2021-11-28T12:37:30.8322741+11:00", 145.1663117892053d, 15.781732997527493d)]
    [InlineData("2021-11-28T11:37:30.8322741+10:00", 145.1663117892053d, 15.781732997527493d)]
    [InlineData("2024-10-29T20:04:00.0000000+11:00", 145.1663117892053d, 21.2901893215026d)]
    [InlineData("1998-08-10T23:10:00.0000000Z", 1.916666666d, 20.576062887455556d)]
    public void GivenLocalDateTimeAndSiteLongitudeWhenSiderealTimeThenItIsReturnedFrom0To24h(string utc, double @long, double expected)
    {
        // given
        var dto = DateTimeOffset.ParseExact(utc, "o", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var transform = new Transform(new FakeTimeProvider(dto)) { SiteLongitude = @long };

        // when / then
        transform.LocalSiderealTime.ShouldBeInRange(expected - 0.01, expected + 0.01);
    }
}
