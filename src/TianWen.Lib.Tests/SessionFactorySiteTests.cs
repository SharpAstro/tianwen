using System;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Which site a session started with no site of its own runs on (P0b item 10, #752): the profile's when the
/// profile is the site's authority, otherwise none, so the mount keeps its own. Never 0, 0, which the
/// zero-filled API configuration used to sync to every mount.
/// </summary>
public class SessionFactorySiteTests
{
    private static ProfileData Profile(SiteTieBreaker tieBreaker, double? latitude = -37.8136, double? longitude = 144.9631)
        => new ProfileData(
            Mount: new Uri("mount://fakedevice/FakeMount1"),
            Guider: new Uri("guider://fakedevice/FakeGuider1"),
            OTAs: [],
            SiteLatitude: latitude,
            SiteLongitude: longitude,
            SiteTieBreaker: tieBreaker);

    [Fact]
    public void WithTheProfileAsTheAuthorityAnUnsetSiteIsTheProfiles()
    {
        var config = SessionFactory.WithProfileSite(new SessionConfiguration(), Profile(SiteTieBreaker.Profile));

        config.SiteLatitude.ShouldBe(-37.8136);
        config.SiteLongitude.ShouldBe(144.9631);
    }

    [Fact]
    public void WithTheMountAsTheAuthorityAnUnsetSiteStaysUnsetSoTheMountKeepsItsOwn()
    {
        var config = SessionFactory.WithProfileSite(new SessionConfiguration(), Profile(SiteTieBreaker.Mount));

        double.IsNaN(config.SiteLatitude).ShouldBeTrue();
        double.IsNaN(config.SiteLongitude).ShouldBeTrue();
    }

    [Fact]
    public void ASiteTheRequestNamesIsNeverReplaced()
    {
        var asked = new SessionConfiguration() with { SiteLatitude = 51.5, SiteLongitude = -0.12 };

        SessionFactory.WithProfileSite(asked, Profile(SiteTieBreaker.Profile)).ShouldBe(asked);
    }

    [Fact]
    public void AProfileWithNoSiteLeavesItUnset()
    {
        var config = SessionFactory.WithProfileSite(new SessionConfiguration(), Profile(SiteTieBreaker.Profile, latitude: null, longitude: null));

        double.IsNaN(config.SiteLatitude).ShouldBeTrue();
    }
}
