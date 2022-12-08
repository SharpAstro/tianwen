using Astap.Lib.Astrometry.SOFA;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomAstroUtilsTests
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
        var transform = new Transform()
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
}
