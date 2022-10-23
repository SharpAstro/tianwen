using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.Ascom;
using Shouldly;
using System.Runtime.InteropServices;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomTransformTests
{
    [SkippableTheory]
    [InlineData(187.82916666666668d, -63.74333333333333d, 2459885.98732d, -37.88d, 145.167d, 120, 11.84d, 182.61d)]
    public void GivenJ2000CoordsAndLocationWhenTransformingThenAltAzIsReturned(double ra2000, double dec2000, double julianUTC, double lat, double @long, double elevation, double expAlt, double expAz)
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

        // given
        ICoordinateTransform transform = new AscomTransform
        {
            SiteLatitude = lat,
            SiteLongitude = @long,
            SiteElevation = elevation,
            JulianDateUTC = julianUTC,
        };
        transform.SetJ2000(ra2000 / 15, dec2000);

        // when
        transform.ElevationTopocentric.ShouldNotBeNull().ShouldBeInRange(expAlt - 0.1, expAlt + 0.1);
        transform.AzimuthTopocentric.ShouldNotBeNull().ShouldBeInRange(expAz - 0.1, expAz + 0.1);
    }
}
