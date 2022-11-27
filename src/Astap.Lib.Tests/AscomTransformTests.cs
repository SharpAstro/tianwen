using Astap.Lib.Astrometry.SOFA;
using Shouldly;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomTransformTests
{
    [Theory]
    [InlineData(10.7382722222222, -59.8841527777778, 2459885.98737d, -37.884546970458274d, 145.1663117892053d, 120, 9.41563888888889d, 169.50725d, 10.752333552022904d, -59.99747614464261d)]
    public void GivenJ2000CoordsAndLocationWhenTransformingThenAltAzAndTopocentricIsReturned(double ra2000, double dec2000, double julianUTC, double lat, double @long, double elevation, double expAlt, double expAz, double expRaTopo, double expDecTopo)
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
}
