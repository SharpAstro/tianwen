using GeoTimeZone;
using Shouldly;
using System;
using TianWen.Lib.Astrometry.SOFA;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Tests for timezone lookup from coordinates, verifying that GeoTimeZone
/// resolves correctly for various sites including ocean locations.
/// </summary>
[Collection("Astrometry")]
public class TimeZoneLookupTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Vienna", 48.2, 16.3, "Europe/Vienna")]
    [InlineData("Denver", 39.7, -105.0, "America/Denver")]
    [InlineData("Tenerife", 28.3, -16.5, "Atlantic/Canary")]
    [InlineData("Morocco", 35.0, -5.0, "Africa/Casablanca")]
    [InlineData("Sydney", -33.9, 151.2, "Australia/Sydney")]
    [InlineData("Tokyo", 35.7, 139.7, "Asia/Tokyo")]
    [InlineData("Hawaii", 19.9, -155.5, "Pacific/Honolulu")]
    public void GivenLandCoordinatesWhenLookupThenReturnsValidTimezone(string name, double lat, double lon, string expectedTzId)
    {
        var result = TimeZoneLookup.GetTimeZone(lat, lon);

        output.WriteLine($"{name} ({lat}, {lon}): tzId={result.Result}");
        result.Result.ShouldNotBeNullOrEmpty($"{name} should have a timezone");
        result.Result.ShouldContain("/");
        result.Result.ShouldBe(expectedTzId, $"{name} timezone mismatch");

        // Verify it resolves to a .NET TimeZoneInfo
        var tzInfo = TimeZoneInfo.FindSystemTimeZoneById(result.Result);
        tzInfo.ShouldNotBeNull();
        output.WriteLine($"  → {tzInfo.DisplayName}, current offset: {tzInfo.GetUtcOffset(DateTime.UtcNow)}");
    }

    [Theory]
    [InlineData("Mid-Atlantic (32,-25)", 32.0, -25.0)]
    [InlineData("South Pacific", -30.0, -130.0)]
    [InlineData("Indian Ocean", -20.0, 70.0)]
    public void GivenOceanCoordinatesWhenLookupThenReturnsResultOrEmpty(string name, double lat, double lon)
    {
        var result = TimeZoneLookup.GetTimeZone(lat, lon);
        output.WriteLine($"{name} ({lat}, {lon}): tzId='{result.Result}'");

        // Ocean locations may or may not return a timezone — document the behavior
        if (result.Result is { Length: > 0 } tzId && tzId.Contains('/'))
        {
            output.WriteLine($"  → Resolved to: {tzId}");
            var tzInfo = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            output.WriteLine($"  → {tzInfo.DisplayName}, offset: {tzInfo.GetUtcOffset(DateTime.UtcNow)}");
        }
        else
        {
            output.WriteLine($"  → No timezone found (ocean location)");
        }
    }

    [Theory]
    [InlineData("Vienna", 48.2, 16.3)]
    [InlineData("Denver", 39.7, -105.0)]
    [InlineData("Tenerife", 28.3, -16.5)]
    public void GivenSiteCoordinatesWhenTransformCreatedThenSiteTimeZoneIsCorrect(string name, double lat, double lon)
    {
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = lat,
            SiteLongitude = lon,
            SiteElevation = 0,
            SiteTemperature = 15,
            DateTimeOffset = DateTimeOffset.UtcNow
        };

        transform.TryGetSiteTimeZone(out var offset, out _).ShouldBeTrue($"{name} should resolve timezone");
        output.WriteLine($"{name}: SiteTimeZone = UTC{(offset >= TimeSpan.Zero ? "+" : "")}{offset}");

        // Sanity check: offset should be roughly lon/15 hours (±2h for DST/political)
        var expectedHours = lon / 15.0;
        var actualHours = offset.TotalHours;
        Math.Abs(actualHours - expectedHours).ShouldBeLessThan(3.0,
            $"{name}: offset {actualHours:F1}h vs expected ~{expectedHours:F1}h (lon/15)");
    }

    [Fact]
    public void GivenOceanCoordinatesWhenTransformCreatedThenFallbackBehaviorDocumented()
    {
        var transform = new Transform(SystemTimeProvider.Instance)
        {
            SiteLatitude = 32,
            SiteLongitude = -25,
            SiteElevation = 0,
            SiteTemperature = 15,
            DateTimeOffset = DateTimeOffset.UtcNow
        };

        var hasTimezone = transform.TryGetSiteTimeZone(out var offset, out _);
        output.WriteLine($"Mid-Atlantic (32, -25): hasTimezone={hasTimezone}, offset={offset}");

        // Document: if this fails, the planner falls back to stale timezone
        if (!hasTimezone)
        {
            output.WriteLine("  → FAILS for ocean locations — planner will use stale/default timezone");
            output.WriteLine("  → User should pick a land-based site for correct timezone computation");
        }
    }
}
