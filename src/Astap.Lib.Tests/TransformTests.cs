using Astap.Lib.Astrometry.SOFA;
using Shouldly;
using System;
using System.Globalization;
using Xunit;

namespace Astap.Lib.Tests;

public class TransformTests
{
    [Theory]
    [InlineData(10.7382722222222, -59.8841527777778, 2459885.98737d, 145.1663117892053d, -37.884546970458274d, 120, 9.41563888888889d, 169.50725d, 10.752333552022904d, -59.99747614464261d)]
    public void GivenJ2000CoordsAndLocationWhenTransformingThenAltAzAndTopocentricIsReturned(double ra2000, double dec2000, double julianUTC, double @long, double lat, double elevation, double expAlt, double expAz, double expRaTopo, double expDecTopo)
    {
        // given
        var transform = new Transform
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
    [InlineData("2022-11-28T12:37:30.8322741+11:00", 145.1663117892053d, 15.76)]
    [InlineData("2023-11-28T12:37:30.8322741+11:00", 145.1663117892053d, 15.76)]
    [InlineData("2021-11-28T11:37:30.8322741+10:00", 145.1663117892053d, 15.781)]
    public void GivenLocalDateTimeAndSiteLongitudeWhenSiderealTimeThenItIsReturnedFrom0To24h(string utc, double @long, double expected)
    {
        // given
        var dto = DateTimeOffset.ParseExact(utc, "o", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        // when / then
        Transform.LocalSiderealTime(dto, @long).ShouldBeInRange(expected - 0.01, expected + 0.01);
    }
}
