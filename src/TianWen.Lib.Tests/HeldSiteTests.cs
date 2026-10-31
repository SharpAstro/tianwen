using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The site editor holds a site in the other hemisphere while the connected mount is away from home
/// (<see cref="EquipmentActions.SiteNeedsMountHome"/>), and asks to home it first; anything else saves at once.
/// </summary>
public class HeldSiteTests
{
    private static readonly SiteCoordinates Vienna = new SiteCoordinates(48.2, 16.3, 200);
    private static readonly SiteCoordinates Melbourne = new SiteCoordinates(-37.88, 145.18, 77);
    private static readonly SiteCoordinates Berlin = new SiteCoordinates(52.5, 13.4, 34);

    private static MountDeviceStateDto Mount(bool atHome, bool atPark = false) => new MountDeviceStateDto
    {
        PierSide = PointingState.Normal,
        IsSlewing = false,
        IsTracking = false,
        AtHome = atHome,
        AtPark = atPark,
    };

    [Theory]
    [InlineData(false, false, true)]  // the other hemisphere, away from home: held
    [InlineData(true, false, false)]  // at home (a Skywatcher parked at home reads at home too): saved
    [InlineData(false, true, true)]   // parked away from home: held, and homing unparks first
    public void AnOtherHemisphereSiteWaitsForTheMountToBeHome(bool atHome, bool atPark, bool held)
    {
        var data = ProfileData.Empty.WithSite(Vienna);

        EquipmentActions.SiteNeedsMountHome(data, Melbourne, Mount(atHome, atPark)).ShouldBe(held);
    }

    [Fact]
    public void ASiteInTheSameHemisphereNeverWaits()
        => EquipmentActions.SiteNeedsMountHome(ProfileData.Empty.WithSite(Vienna), Berlin, Mount(atHome: false)).ShouldBeFalse();

    [Fact]
    public void NoConnectedMountOrNoPreviousSiteNeverWaits()
    {
        EquipmentActions.SiteNeedsMountHome(ProfileData.Empty.WithSite(Vienna), Melbourne, mount: null).ShouldBeFalse();
        EquipmentActions.SiteNeedsMountHome(ProfileData.Empty, Melbourne, Mount(atHome: false)).ShouldBeFalse();
    }

    [Fact]
    public void AtHomeAndAtParkCrossTheWire()
    {
        var state = new MountState(5, 30, 1, PointingState.Normal, false, false, AtHome: true, AtPark: true);

        var back = MountDeviceStateDto.FromState(state, verdict: null).ToState();

        (back.AtHome, back.AtPark).ShouldBe((true, true));
    }
}
