using Astap.Lib.Astrometry.NOVA;
using Shouldly;
using System;
using System.Globalization;
using Xunit;

namespace Astap.Lib.Tests;

public class CoordinateUtilsTests
{
    [Theory]
    [InlineData("2022-11-05T22:03:25.5847372Z", 2459889.419046111d)]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", 2459889.4244750347d)]
    [InlineData("2022-11-05T11:11:14.6430197-11:00", 2459889.4244750347d)]
    public void GivenDTOWhenConvertToJulianThenItIsReturned(string dtoStr, double expectedJulian)
    {
        var dto = DateTimeOffset.Parse(dtoStr, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

        CoordinateUtils.ToJulian(dto).ShouldBe(expectedJulian);
    }

    [Theory]
    [InlineData("05:23:34.5", 5.392916666666667d)]
    [InlineData("23:54:13.2", 23.903666666666666d)]
    [InlineData("23:59:59.9", 23.999972222222222d)]
    [InlineData("12:00:00", 12d)]
    [InlineData("0:0:0", 0d)]
    public void GivenHMSWHenConvertToHoursItReturnsHoursAsDouble(string hms, double expectedDegrees)
    {
        CoordinateUtils.HMSToHours(hms).ShouldBe(expectedDegrees);
    }

    [Theory]
    [InlineData(5.392916666666667d, "05:23:34.500")]
    [InlineData(23.903666666666666d, "23:54:13.200")]
    [InlineData(23.999972222222222d, "23:59:59.900")]
    [InlineData(12d, "12:00:00")]
    [InlineData(0d, "00:00:00")]
    public void GivenHoursHenConvertToHMSItReturnsHMSAsString(double hours, string expectedHMS)
    {
        CoordinateUtils.HoursToHMS(hours).ShouldBe(expectedHMS);
    }

    [Theory]
    [InlineData("05:23:34.5", 80.89375d)]
    [InlineData("23:54:13.2", 358.555d)]
    [InlineData("23:59:59.9", 359.9995833333333d)]
    [InlineData("12:00:00", 180.0d)]
    [InlineData("0:0:0", 0d)]
    public void GivenHMSWHenConvertToDegreesItReturnsDegreesAsDouble(string hms, double expectedDegrees)
    {
        CoordinateUtils.HMSToDegree(hms).ShouldBe(expectedDegrees);
    }

    [Theory]
    [InlineData("+61:44:24", 61.74d)]
    [InlineData("-12:03:18", -12.055000000000001d)]
    [InlineData("+90:0:0", +90.0d)]
    [InlineData("-89:30:0", -89.5d)]
    [InlineData("-90:0:0", -90.0d)]
    [InlineData("0:0:0", 0.0d)]
    [InlineData("-08:11:11", -8.186388888888889d)]
    [InlineData("-00:38:16.5", -0.6379166666666667d)]
    public void GivenDMSWHenConvertToDegreesItReturnsDegreesAsDouble(string dms, double expectedDegrees)
    {
        CoordinateUtils.DMSToDegree(dms).ShouldBe(expectedDegrees);
    }

    [Theory]
    [InlineData(61.74d, "+61:44:24")]
    [InlineData(-12.055000000000001d, "-12:03:18")]
    [InlineData(+90.0d, "+90:00:00")]
    [InlineData(-89.5d, "-89:30:00")]
    [InlineData(-90.0d, "-90:00:00")]
    [InlineData(0.0d, "+00:00:00")]
    [InlineData(-8.186388888888889d, "-08:11:11")]
    [InlineData(-31.314123299999999, "-31:18:50.844")]
    [InlineData(+12.8d, "+12:48:00")]
    public void GivenDegreesWhenConvertToDMSItReturnsDMSAsString(double degrees, string expectedDMS)
    {
        CoordinateUtils.DegreesToDMS(degrees).ShouldBe(expectedDMS);
    }
}