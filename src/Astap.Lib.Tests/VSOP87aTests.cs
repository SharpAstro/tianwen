using Astap.Lib.Astrometry.Catalogs;
using Astap.Lib.Astrometry.VSOP87;
using Shouldly;
using System;
using System.Globalization;
using Xunit;

namespace Astap.Lib.Tests;

public class VSOP87aTests
{
    [Theory]
    [InlineData(CatalogIndex.Moon, "2020-04-11T16:00:00Z", 274.236400, 38.2464000, 16.61305867, -20.90403, 261.7692, -24.3259)]
    [InlineData(CatalogIndex.Sol, "2022-11-29T10:40:00+11:00", 145.1663118d, -37.884547d, 16.3253006d, -21.43031427d, 73.1644087d, 54.3085913d)]
    public void GivenPlanetAndPositionWhenReducingOrbitThenRaDecAzAltIsReturned(CatalogIndex catIdx, string dateStr, double @long, double lat, double expRa, double expDec, double expAz, double expAlt)
    {
        // given
        var dto = DateTimeOffset.Parse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        // when
        VSOP87a.Reduce(catIdx, dto, lat, @long, out var actualRa, out var actualDec, out var actualAz, out var actualAlt);

        // then
        actualRa.ShouldBeInRange(expRa - 0.1, expRa + 0.1);
        actualDec.ShouldBeInRange(expDec - 0.1, expDec + 0.1);
        actualAz.ShouldBeInRange(expAz - 0.1, expAz + 0.1);
        actualAlt.ShouldBeInRange(expAlt - 0.1, expAlt + 0.1);
    }
}
