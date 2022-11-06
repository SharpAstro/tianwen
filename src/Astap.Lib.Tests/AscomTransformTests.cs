using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.Ascom;
using Shouldly;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomUtilsTests
{
    [SkippableTheory]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", EventType.SunRiseSunset, -37.884546970458274d, 145.1663117892053d, false, 1, 1, "2022-11-06T06:07:20+11:00", "2022-11-06T19:59:06+11:00")]
    [InlineData("2012-01-13T00:00:00-05:00", EventType.MoonRiseMoonSet, 75d, -75d, true, 1, 1, "2012-01-13T22:36:21-05:00", "2012-01-13T09:38:29-05:00")]
    [InlineData("2012-01-14T00:00:00-05:00", EventType.MoonRiseMoonSet, 75d, -75d, true, 0, 1, "2012-01-14T09:06:07-05:00")]
    [InlineData("2022-11-06T00:00:00+11:00", EventType.AstronomicalTwilight, -37.8845, 145.1663, false, 1, 1, "2022-11-06T04:27:15+11:00", "2022-11-06T21:39:40+11:00")]
    public void GivenDateEventPositionAndOffsetWhenCalcRiseAndSetTimesTheyAreReturned(string dtoStr, EventType eventType, double lat, double @long, bool expAbove, int expRise, int expSet, params string[] dateTimeStrs)
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        using var astroUtils = new AstroUtils();
        var dto = DateTimeOffset.Parse(dtoStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var ret = astroUtils.EventTimes(eventType, dto, lat, @long);
        var (actualAboveHorizon, actualRiseEvents, actualSetEvents) = ret.ShouldNotBeNull();

        actualAboveHorizon.ShouldBe(expAbove);
        actualRiseEvents.Count.ShouldBe(expRise);
        actualSetEvents.Count.ShouldBe(expSet);

        int idx = 0;

        for (var i = 0; i < expRise; i++)
        {
            var expDto = DateTimeOffset.Parse(dateTimeStrs[idx++], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            actualRiseEvents[i].ShouldBeInRange(expDto - TimeSpan.FromMinutes(1), expDto + TimeSpan.FromMinutes(1));
        }

        for (var i = 0; i < expSet; i++)
        {
            var expDto = DateTimeOffset.Parse(dateTimeStrs[idx++], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

            actualSetEvents[i].ShouldBeInRange(expDto - TimeSpan.FromMinutes(1), expDto + TimeSpan.FromMinutes(1));
        }
    }
}

public class AscomTransformTests
{
    [SkippableTheory]
    [InlineData(10.7382722222222, -59.8841527777778, 2459885.98737d, -37.884546970458274d, 145.1663117892053d, 120, 9.41563888888889d, 169.50725d, 10.752333552022904d, -59.99747614464261d)]
    public void GivenJ2000CoordsAndLocationWhenTransformingThenAltAzAndTopocentricIsReturned(double ra2000, double dec2000, double julianUTC, double lat, double @long, double elevation, double expAlt, double expAz, double expRaTopo, double expDecTopo)
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // given
        using ICoordinateTransform transform = new AscomTransform
        {
            SiteLatitude = lat,
            SiteLongitude = @long,
            SiteElevation = elevation,
            JulianDateUTC = julianUTC,
        };
        transform.SetJ2000(ra2000, dec2000);

        // when/then
        transform.ElevationTopocentric.ShouldNotBeNull().ShouldBeInRange(expAlt - 0.1, expAlt + 0.1);
        transform.AzimuthTopocentric.ShouldNotBeNull().ShouldBeInRange(expAz - 0.1, expAz + 0.1);
        transform.RATopocentric.ShouldNotBeNull().ShouldBeInRange(expRaTopo - 0.1, expRaTopo + 0.1);
        transform.DECTopocentric.ShouldNotBeNull().ShouldBeInRange(expDecTopo - 0.1, expDecTopo + 0.1);
    }
}
