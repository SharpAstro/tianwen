using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The site rule every host applies to a mount (#798): <see cref="SiteReconcileDecision.Decide"/>, and
/// <c>ReconcileSiteAsync</c>, which reads the mount and applies the mount-side half. The rule lived, untested,
/// in the GUI's <c>EquipmentActions</c>; a run's use of it is pinned in <c>SessionLifecycleTests</c>.
/// </summary>
public class MountSiteReconcileTests(ITestOutputHelper output)
{
    private static readonly SiteCoordinates Vienna = new(48.2, 16.3, 200);
    private static readonly SiteCoordinates Melbourne = new(-37.8136, 144.9631, 31);

    [Theory]
    [InlineData(SiteTieBreaker.Mount)]
    [InlineData(SiteTieBreaker.Profile)]
    public void WithNoSiteOnEitherSideThereIsNone(SiteTieBreaker tieBreaker)
    {
        SiteReconcileDecision.Decide(null, null, tieBreaker)
            .ShouldBe(new SiteReconcileDecision(null, SiteSource.None, PushToMount: false, AdoptIntoProfile: false));
    }

    [Theory]
    [InlineData(SiteTieBreaker.Mount)]
    [InlineData(SiteTieBreaker.Profile)]
    public void AMountSiteTheProfileLacksIsAdoptedWhateverTheTieBreaker(SiteTieBreaker tieBreaker)
    {
        SiteReconcileDecision.Decide(Vienna, null, tieBreaker)
            .ShouldBe(new SiteReconcileDecision(Vienna, SiteSource.Mount, PushToMount: false, AdoptIntoProfile: true));
    }

    [Theory]
    [InlineData(SiteTieBreaker.Mount)]
    [InlineData(SiteTieBreaker.Profile)]
    public void AProfileSiteTheMountLacksIsPushedWhateverTheTieBreaker(SiteTieBreaker tieBreaker)
    {
        SiteReconcileDecision.Decide(null, Melbourne, tieBreaker)
            .ShouldBe(new SiteReconcileDecision(Melbourne, SiteSource.Profile, PushToMount: true, AdoptIntoProfile: false));
    }

    [Fact]
    public void WhenBothDifferTheMountWinsByDefaultAndTheProfileAdoptsIt()
    {
        SiteReconcileDecision.Decide(Vienna, Melbourne, SiteTieBreaker.Mount)
            .ShouldBe(new SiteReconcileDecision(Vienna, SiteSource.Mount, PushToMount: false, AdoptIntoProfile: true));
    }

    [Fact]
    public void WhenBothDifferAndTheProfileWinsTheMountIsGivenIt()
    {
        SiteReconcileDecision.Decide(Vienna, Melbourne, SiteTieBreaker.Profile)
            .ShouldBe(new SiteReconcileDecision(Melbourne, SiteSource.Profile, PushToMount: true, AdoptIntoProfile: false));
    }

    [Theory]
    [InlineData(SiteTieBreaker.Mount)]
    [InlineData(SiteTieBreaker.Profile)]
    public void WhenBothAgreeNothingIsWritten(SiteTieBreaker tieBreaker)
    {
        var decision = SiteReconcileDecision.Decide(Vienna, Vienna with { }, tieBreaker);

        decision.Site.ShouldBe(Vienna);
        decision.PushToMount.ShouldBeFalse();
        decision.AdoptIntoProfile.ShouldBeFalse();
    }

    [Fact]
    public void UnderTheProfileAnElevationDisagreesOnlyWhenTheMountReportsOne()
    {
        SiteReconcileDecision.Decide(Vienna, Vienna with { Elevation = 250 }, SiteTieBreaker.Profile).PushToMount
            .ShouldBeTrue("both report one and they differ");
        SiteReconcileDecision.Decide(Vienna with { Elevation = null }, Vienna with { Elevation = 250 }, SiteTieBreaker.Profile).PushToMount
            .ShouldBeFalse("a mount that keeps no elevation has none to correct");
    }

    /// <summary>
    /// The mount's site is taken whole, so an elevation only the profile had is dropped: the GUI's rule when the
    /// mount wins, kept as it was when the rule moved here.
    /// </summary>
    [Fact]
    public void UnderTheMountTheProfileMirrorsItsSiteWholeElevationIncluded()
    {
        var mountWithoutElevation = Vienna with { Elevation = null };

        SiteReconcileDecision.Decide(mountWithoutElevation, Vienna, SiteTieBreaker.Mount)
            .ShouldBe(new SiteReconcileDecision(mountWithoutElevation, SiteSource.Mount, PushToMount: false, AdoptIntoProfile: true));
    }

    [Theory]
    [InlineData(double.NaN, double.NaN)] // what a mount never given a site reports (SkyWatcher, iOptron)
    [InlineData(0.0, 0.0)]               // what an ASCOM driver typically reports instead
    public async Task AMountReportingNaNOrZeroZeroHasNoSite(double latitude, double longitude)
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = await ConnectedFakeMountAsync();
        await mount.SetSiteLatitudeAsync(latitude, ct);
        await mount.SetSiteLongitudeAsync(longitude, ct);

        (await mount.GetSiteAsync(ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ReconcilingAMountWithNoSiteGivesItTheProfilesElevationIncluded()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = await ConnectedFakeMountAsync();
        await mount.SetSiteLatitudeAsync(double.NaN, ct);
        await mount.SetSiteLongitudeAsync(double.NaN, ct);

        var decision = await mount.ReconcileSiteAsync(Melbourne, SiteTieBreaker.Mount, logger: null, ct);

        decision.ShouldBe(new SiteReconcileDecision(Melbourne, SiteSource.Profile, PushToMount: true, AdoptIntoProfile: false));
        (await mount.GetSiteAsync(ct)).ShouldBe(Melbourne);
    }

    [Fact]
    public async Task ReconcilingLeavesTheProfilesHalfToItsWriterAndTheMountAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = await ConnectedFakeMountAsync();
        (await mount.GetSiteAsync(ct)).ShouldBe(Vienna, "premise: the fake mount's own site");

        var decision = await mount.ReconcileSiteAsync(profileSite: null, SiteTieBreaker.Profile, logger: null, ct);

        decision.AdoptIntoProfile.ShouldBeTrue();
        (await mount.GetSiteAsync(ct)).ShouldBe(Vienna);
    }

    /// <summary>
    /// No site, no transform. The default built one on a NaN site, which threw "Site longitude has not been
    /// set" the first time anything asked it for sidereal time: here, the fake mount re-homing as a run gave a
    /// mount with no site the profile's, latitude first.
    /// </summary>
    [Fact]
    public async Task AMountWithNoSiteHasNoTransform()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = await ConnectedFakeMountAsync();
        (await mount.TryGetTransformAsync(ct)).ShouldNotBeNull("premise: a transform on the mount's own site");

        await mount.SetSiteLongitudeAsync(double.NaN, ct);

        (await mount.TryGetTransformAsync(ct)).ShouldBeNull();
    }

    [Fact]
    public async Task TheGuiAppliesTheProfilesHalfWhenTheMountsSiteWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = await ConnectedFakeMountAsync();

        var result = await EquipmentActions.ReconcileSiteOnMountConnectAsync(ProfileWithSite(null), mount, logger: null, ct);

        result.ProfileChanged.ShouldBeTrue();
        result.MountPushed.ShouldBeFalse();
        result.WinnerSource.ShouldBe("mount");
        result.Data.Site.ShouldBe(Vienna);
    }

    [Fact]
    public async Task TheGuiLeavesTheProfileAloneWhenTheMountIsGivenItsSite()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = await ConnectedFakeMountAsync();
        await mount.SetSiteLatitudeAsync(double.NaN, ct);
        await mount.SetSiteLongitudeAsync(double.NaN, ct);
        var profile = ProfileWithSite(Melbourne);

        var result = await EquipmentActions.ReconcileSiteOnMountConnectAsync(profile, mount, logger: null, ct);

        result.ProfileChanged.ShouldBeFalse();
        result.MountPushed.ShouldBeTrue();
        result.WinnerSource.ShouldBe("profile");
        result.Data.ShouldBe(profile);
        (await mount.GetSiteAsync(ct)).ShouldBe(Melbourne);
    }

    private static ProfileData ProfileWithSite(SiteCoordinates? site) => new ProfileData(
        Mount: new FakeDevice(DeviceType.Mount, 1).DeviceUri,
        Guider: new FakeDevice(DeviceType.Guider, 1).DeviceUri,
        OTAs: [],
        SiteLatitude: site?.Latitude,
        SiteLongitude: site?.Longitude,
        SiteElevation: site?.Elevation);

    private async Task<IMountDriver> ConnectedFakeMountAsync()
    {
        var services = new FakeExternal(output).BuildServiceProvider();
        new FakeDevice(DeviceType.Mount, 1).TryInstantiateDriver<IMountDriver>(services, out var mount).ShouldBeTrue();
        await mount.ConnectAsync(TestContext.Current.CancellationToken);
        return mount;
    }
}
