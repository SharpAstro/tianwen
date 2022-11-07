using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.Ascom;
using Shouldly;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Astap.Lib.Tests;

public class AscomTransformTests : IDisposable
{
    private readonly ICoordinateTransform _transform;
    private readonly SemaphoreSlim _semaphore;

    public AscomTransformTests()
    {
        _transform = new CoordinateTransform();
        _semaphore = new(1, 1);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _transform.Dispose();
    }

    [SkippableTheory]
    [InlineData(10.7382722222222, -59.8841527777778, 2459885.98737d, -37.884546970458274d, 145.1663117892053d, 120, 9.41563888888889d, 169.50725d, 10.752333552022904d, -59.99747614464261d)]
    public async Task GivenJ2000CoordsAndLocationWhenTransformingThenAltAzAndTopocentricIsReturned(double ra2000, double dec2000, double julianUTC, double lat, double @long, double elevation, double expAlt, double expAz, double expRaTopo, double expDecTopo)
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Debugger.IsAttached);

        await _semaphore.WaitAsync();
        try
        {
            // given
            _transform.SiteLatitude = lat;
            _transform.SiteLongitude = @long;
            _transform.SiteElevation = elevation;
            _transform.JulianDateUTC = julianUTC;
            _transform.SetJ2000(ra2000, dec2000);

            // when/then
            _transform.ElevationTopocentric.ShouldNotBeNull().ShouldBeInRange(expAlt - 0.1, expAlt + 0.1);
            _transform.AzimuthTopocentric.ShouldNotBeNull().ShouldBeInRange(expAz - 0.1, expAz + 0.1);
            _transform.RATopocentric.ShouldNotBeNull().ShouldBeInRange(expRaTopo - 0.1, expRaTopo + 0.1);
            _transform.DECTopocentric.ShouldNotBeNull().ShouldBeInRange(expDecTopo - 0.1, expDecTopo + 0.1);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
