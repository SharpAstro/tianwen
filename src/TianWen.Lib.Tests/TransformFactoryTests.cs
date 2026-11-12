using Shouldly;
using System;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>What <see cref="TransformFactory.FromProfile"/> asks of a profile: a site, and nothing else.</summary>
public class TransformFactoryTests
{
    private static readonly FakeTimeProviderWrapper Clock = new(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void AProfileWithASiteAndNoMountHasATransform()
    {
        var data = ProfileData.Empty with { SiteLatitude = -37.0, SiteLongitude = 145.0, SiteElevation = 72.0 };
        data.Mount.ShouldBe(NoneDevice.Instance.DeviceUri, "the empty profile has no mount");

        var transform = TransformFactory.FromProfile(new Profile(Guid.NewGuid(), "Test", data), Clock, out var error);

        error.ShouldBeNull();
        transform.ShouldNotBeNull();
        transform.SiteLatitude.ShouldBe(-37.0);
        transform.SiteLongitude.ShouldBe(145.0);
        transform.SiteElevation.ShouldBe(72.0);
    }

    [Fact]
    public void AProfileWithAMountAndNoSiteSaysTheSiteIsMissing()
    {
        var data = ProfileData.Empty with { Mount = new Uri("ascom://GSServer") };

        var transform = TransformFactory.FromProfile(new Profile(Guid.NewGuid(), "Test", data), Clock, out var error);

        transform.ShouldBeNull();
        error.ShouldNotBeNull().ShouldContain("site coordinates");
    }
}
