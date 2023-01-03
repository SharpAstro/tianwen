using Astap.Lib.Astrometry;
using Shouldly;
using System;
using System.Globalization;
using Xunit;

namespace Astap.Lib.Tests;

public class TimeUtilTests
{
    [Theory]
    [InlineData("2022-11-05T22:03:25.5847372Z", 2459889.419046111d)]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", 2459889.4244750347d)]
    [InlineData("2022-11-05T11:11:14.6430197-11:00", 2459889.4244750347d)]
    public void GivenDTOWhenToJulianThenItIsReturned(string dtoStr, double expectedJulian)
    {
        // given
        var dto = DateTimeOffset.ParseExact(dtoStr, "o", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        // when / then
        dto.ToJulian().ShouldBe(expectedJulian);
    }

    [Theory]
    [InlineData("2022-11-05T22:03:25.5847372Z", 2459889.419046111d)]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", 2459889.4244750347d)]
    [InlineData("2022-11-05T11:11:14.6430197-11:00", 2459889.4244750347d)]
    public void GivenDTUtcWhenToJulianThenItIsReturned(string dtStr, double expectedJulian)
    {
        // given
        var dt = DateTime.ParseExact(dtStr, "o", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

        // when / then
        dt.ToJulian().ShouldBe(expectedJulian);
    }

    [Theory]
    [InlineData("2022-11-05T22:03:25.5847372Z", 2459889, .419046111d)]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", 2459889, .4244750347d)]
    [InlineData("2022-11-05T11:11:14.6430197-11:00", 2459889, .4244750347d)]
    public void GivenJulianDateWhenToDTThenItIsSame(string expectedDtStr, double jd1, double jd2)
    {
        // given
        var dt = DateTime.ParseExact(expectedDtStr, "o", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

        // when / then
        TimeUtils.FromJulian(jd1, jd2).ShouldBeInRange(dt - TimeSpan.FromMilliseconds(1), dt + TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [InlineData("2022-11-05T22:03:25.5847372Z", 2459888.5d, 0.9190461111111111d)]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", 2459888.5d, 0.9244750347222221d)]
    [InlineData("2022-11-05T11:11:14.6430197-11:00", 2459888.5d, 0.9244750347222221d)]
    [InlineData("2023-11-05T11:11:14.6430197-11:00", 2460253.5d, 0.9244750347222221d)]
    public void GivenDTOWhenToSOFAUtcJdThenTwoUtcComponentsAreReturned(string dtoStr, double expectedUtc1, double expectedUtc2)
    {
        // given
        var dto = DateTimeOffset.ParseExact(dtoStr, "o", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        // when
        dto.ToSOFAUtcJd(out var actualUtc1, out var actualUtc2);

        // then
        actualUtc1.ShouldBe(expectedUtc1);
        actualUtc2.ShouldBe(expectedUtc2);
    }

    [Theory]
    [InlineData("2022-11-05T22:03:25.5847372Z", 2459888.5d, 0.9198468518518519d)]
    [InlineData("2022-11-06T09:11:14.6430197+11:00", 2459888.5d, 0.9252757754629629d)]
    [InlineData("2022-11-05T11:11:14.6430197-11:00", 2459888.5d, 0.9252757754629629d)]
    [InlineData("2023-11-05T11:11:14.6430197-11:00", 2460253.5d, 0.9252757754629629d)]
    public void GivenDTOWhenToSOFAUtcJdTTThenTwoTTComponentsAreReturned(string dtoStr, double expectedTT1, double expectedTT2)
    {
        // given
        var dto = DateTimeOffset.ParseExact(dtoStr, "o", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        // when
        dto.ToSOFAUtcJdTT(out var _, out var _, out var actualTt1, out var actualTt2);

        // then
        actualTt1.ShouldBe(expectedTT1);
        actualTt2.ShouldBe(expectedTT2);
    }
}
