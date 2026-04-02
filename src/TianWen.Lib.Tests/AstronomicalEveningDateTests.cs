using System;
using Shouldly;
using TianWen.Lib.Astrometry;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Astrometry")]
public class AstronomicalEveningDateTests
{
    [Theory]
    [InlineData("2026-03-24T22:00:00+01:00", "2026-03-24")] // 10pm — tonight = March 24
    [InlineData("2026-03-24T23:59:59+01:00", "2026-03-24")] // 11:59pm — still March 24
    [InlineData("2026-03-25T00:00:01+01:00", "2026-03-24")] // just past midnight — still March 24's evening
    [InlineData("2026-03-25T03:00:00+01:00", "2026-03-24")] // 3am — still observing March 24's session
    [InlineData("2026-03-25T06:00:00+01:00", "2026-03-24")] // 6am — still before noon, March 24's session
    [InlineData("2026-03-25T11:59:59+01:00", "2026-03-24")] // 11:59am — last moment of March 24's session
    [InlineData("2026-03-25T12:00:00+01:00", "2026-03-25")] // noon — flips to March 25's evening
    [InlineData("2026-03-25T15:00:00+01:00", "2026-03-25")] // 3pm — March 25's evening
    [InlineData("2026-03-25T20:00:00-10:00", "2026-03-25")] // 8pm Hawaii — March 25's evening
    [InlineData("2026-03-26T02:00:00-10:00", "2026-03-25")] // 2am Hawaii — still March 25's session
    public void GivenSiteLocalTimeWhenAstronomicalEveningDateThenReturnsCorrectDate(string isoTime, string expectedDate)
    {
        var siteTime = DateTimeOffset.Parse(isoTime);
        var expected = DateTime.Parse(expectedDate);

        var result = CoordinateUtils.AstronomicalEveningDate(siteTime);

        result.ShouldBe(expected);
    }
}
