using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Astrometry;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class FakeSkywatcherMountDriverTests(ITestOutputHelper output)
{
    private (FakeSkywatcherMountDriver mount, FakeExternal external) CreateMount(
        double latitude = 48.2,
        double longitude = 16.3,
        double azMisalignmentArcmin = 0.0,
        double altMisalignmentArcmin = 0.0,
        DateTimeOffset? now = null)
    {
        var external = new FakeExternal(output, now: now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "port", "SkyWatcher" },
            { "latitude", latitude.ToString() },
            { "longitude", longitude.ToString() },
            { "polarMisalignmentAzArcmin", azMisalignmentArcmin.ToString() },
            { "polarMisalignmentAltArcmin", altMisalignmentArcmin.ToString() }
        });
        var mount = new FakeSkywatcherMountDriver(device, external.BuildServiceProvider());
        return (mount, external);
    }

    private async Task<(FakeSkywatcherMountDriver mount, FakeExternal external)> CreateConnectedMountAsync(
        CancellationToken ct,
        double latitude = 48.2,
        double longitude = 16.3,
        double azMisalignmentArcmin = 0.0,
        double altMisalignmentArcmin = 0.0,
        DateTimeOffset? now = null)
    {
        var (mount, external) = CreateMount(latitude, longitude, azMisalignmentArcmin, altMisalignmentArcmin, now);
        await mount.ConnectAsync(ct);
        await mount.SetSiteLatitudeAsync(latitude, ct);
        await mount.SetSiteLongitudeAsync(longitude, ct);
        return (mount, external);
    }

    #region Connect / Disconnect

    [Fact(Timeout = 60_000)]
    public async Task GivenSkywatcherMountWhenConnectedThenIsConnectedAndFirmwareAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();

        await mount.ConnectAsync(ct);

        mount.Connected.ShouldBeTrue();
        var driverInfo = mount.DriverInfo;
        driverInfo.ShouldNotBeNull();
        driverInfo.ShouldContain("Skywatcher");
        driverInfo.ShouldContain("EQ6");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedSkywatcherMountWhenDisconnectedThenIsNotConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();

        await mount.ConnectAsync(ct);
        mount.Connected.ShouldBeTrue();

        await mount.DisconnectAsync(ct);
        mount.Connected.ShouldBeFalse();
    }

    #endregion

    #region Tracking

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenTrackingEnabledThenIsTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetTrackingAsync(true, ct);

        (await mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenTrackingMountWhenTrackingDisabledThenNotTracking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetTrackingAsync(true, ct);
        (await mount.IsTrackingAsync(ct)).ShouldBeTrue();

        await mount.SetTrackingAsync(false, ct);
        (await mount.IsTrackingAsync(ct)).ShouldBeFalse();
    }

    #endregion

    #region Slew

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenSlewStartedThenIsSlewing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.BeginSlewRaDecAsync(12.0, 45.0, ct);

        (await mount.IsSlewingAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSlewingMountWhenAbortedThenNotSlewing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.BeginSlewRaDecAsync(12.0, 45.0, ct);
        (await mount.IsSlewingAsync(ct)).ShouldBeTrue();

        await mount.AbortSlewAsync(ct);
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse();
    }

    #endregion

    #region Sync

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenSyncedThenPositionUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(ct);

        await mount.SyncRaDecAsync(12.5, 45.0, ct);

        var ra = await mount.GetRightAscensionAsync(ct);
        var dec = await mount.GetDeclinationAsync(ct);

        ra.ShouldBe(12.5, 0.1);
        dec.ShouldBe(45.0, 0.1);
    }

    #endregion

    #region Pulse Guide

    [Theory(Timeout = 60_000)]
    [InlineData(GuideDirection.East)]
    [InlineData(GuideDirection.West)]
    [InlineData(GuideDirection.North)]
    [InlineData(GuideDirection.South)]
    public async Task GivenConnectedMountWhenPulseGuideThenCompletes(GuideDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        // Should complete without throwing
        await mount.PulseGuideAsync(direction, TimeSpan.FromMilliseconds(100), ct);
    }

    #endregion

    #region Camera Snap

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenCameraSnapThenCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        var settings = new CameraSnapSettings(
            ShutterTime: TimeSpan.FromMilliseconds(100),
            Interval: TimeSpan.FromSeconds(1),
            ShotCount: 1);

        await mount.CameraSnapAsync(settings, ct);
    }

    #endregion

    #region Axis Position

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountThenAxisPositionReturnsValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        var raPos = await mount.GetAxisPositionAsync(TelescopeAxis.Primary, ct);
        var decPos = await mount.GetAxisPositionAsync(TelescopeAxis.Seconary, ct);

        raPos.ShouldNotBeNull();
        decPos.ShouldNotBeNull();
    }

    #endregion

    #region Park

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedMountWhenParkedThenAtPark()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.ParkAsync(ct);
        (await mount.AtParkAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenParkedMountWhenUnparkedThenNotAtPark()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.ParkAsync(ct);
        (await mount.AtParkAsync(ct)).ShouldBeTrue();

        await mount.UnparkAsync(ct);
        (await mount.AtParkAsync(ct)).ShouldBeFalse();
    }

    #endregion

    #region Capabilities

    [Fact(Timeout = 60_000)]
    public async Task GivenSkywatcherMountThenCapabilitiesReflectGEM()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.CanPulseGuide.ShouldBeTrue();
        mount.CanSetTracking.ShouldBeTrue();
        mount.CanSync.ShouldBeTrue();
        mount.CanCameraSnap.ShouldBeTrue();
        mount.CanSlew.ShouldBeTrue();
        mount.CanSlewAsync.ShouldBeTrue();
        mount.CanPark.ShouldBeTrue();
        mount.CanSetGuideRates.ShouldBeTrue();

        mount.CanMoveAxis(TelescopeAxis.Primary).ShouldBeTrue();
        mount.CanMoveAxis(TelescopeAxis.Seconary).ShouldBeTrue();
        mount.CanMoveAxis(TelescopeAxis.Tertiary).ShouldBeFalse();
    }

    #endregion

    #region Site

    [Fact(Timeout = 60_000)]
    public async Task GivenSkywatcherMountWhenSiteIsPushedThenGetSiteReturnsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetSiteLatitudeAsync(48.2, ct);
        await mount.SetSiteLongitudeAsync(16.3, ct);

        (await mount.GetSiteLatitudeAsync(ct)).ShouldBe(48.2, 0.01);
        (await mount.GetSiteLongitudeAsync(ct)).ShouldBe(16.3, 0.01);
    }

    #endregion

    #region Polar Misalignment — imaging regime

    // ~30'/-10' misalignment used across the imaging-regime tests; magnitude
    // ~0.5deg, so the pointing offset stays well inside a degree.
    private const double TestAzMisalignArcmin = 30.0;
    private const double TestAltMisalignArcmin = -10.0;

    /// <summary>
    /// Advance fake time in small steps until the mount reports it has stopped
    /// slewing (or a cap is hit). The fake Skywatcher integrates its goto at
    /// 3deg/s on a 50ms simulation timer, so the slew only progresses when fake
    /// time advances.
    /// </summary>
    private static async Task PumpUntilNotSlewingAsync(FakeSkywatcherMountDriver mount, FakeExternal external, CancellationToken ct, TimeSpan max)
    {
        var step = TimeSpan.FromMilliseconds(200);
        var pumped = TimeSpan.Zero;
        while (pumped < max)
        {
            if (!await mount.IsSlewingAsync(ct))
            {
                break;
            }
            external.TimeProvider.Advance(step);
            pumped += step;
        }
    }

    [Fact]
    public void GivenMisalignedAxisAwayFromPoleWhenApplyAxisTiltThenNearCommandedNotPole()
    {
        // Misaligned RA axis tilted ~0.5deg off the +Z (north) celestial pole.
        var tiltRad = 0.5 * Math.PI / 180.0;
        var axis = new Vec3(Math.Sin(tiltRad), 0.0, Math.Cos(tiltRad));

        // Believed (commanded/encoder) pointing well away from the pole.
        const double believedRa = 6.0;   // hours
        const double believedDec = 45.0; // degrees

        var (ra, dec) = FakeSkywatcherMountDriver.ApplyAxisTiltToPointing(
            axis, Hemisphere.North, believedRa, believedDec);

        // True pointing must track the commanded Dec to within the misalignment
        // magnitude -- emphatically NOT snapped to the pole (the original bug).
        dec.ShouldBe(believedDec, 1.0);
        dec.ShouldBeLessThan(80.0);

        // ...but it must NOT be identical either: the misalignment carries on so
        // plate-solve centering has a real offset to detect and sync away.
        var offsetDeg = Math.Abs(dec - believedDec) + Math.Abs(ra - believedRa) * 15.0;
        offsetDeg.ShouldBeGreaterThan(0.01);
    }

    [Fact]
    public void GivenAlignedAxisWhenApplyAxisTiltThenReturnsBelievedUnchanged()
    {
        // Axis exactly on the pole == no misalignment -> identity transform.
        var pole = new Vec3(0.0, 0.0, 1.0);

        var (ra, dec) = FakeSkywatcherMountDriver.ApplyAxisTiltToPointing(
            pole, Hemisphere.North, 6.0, 45.0);

        ra.ShouldBe(6.0, 1e-9);
        dec.ShouldBe(45.0, 1e-9);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedMountWhenSlewedAwayFromPoleThenReportsNearTargetNotPole()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        // Slew Dec from the home pole down to +45; keep RA at the current LST so
        // the RA delta is ~0 and only the Dec axis has to travel (bounds pump time).
        var lst = await mount.GetSiderealTimeAsync(ct);
        await mount.BeginSlewRaDecAsync(lst, 45.0, ct);
        await PumpUntilNotSlewingAsync(mount, external, ct, TimeSpan.FromSeconds(40));

        var dec = await mount.GetDeclinationAsync(ct);

        // Reports ~commanded (within the misalignment magnitude), NOT the pole.
        dec.ShouldBe(45.0, 1.5);
        dec.ShouldBeLessThan(80.0);
        // No plate-solve sync yet -> the misalignment is still "carried on".
        mount.IsAlignmentCorrected.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedMountWhenPlateSolveSyncAwayFromPoleThenLearnsAlignmentAndReportsBelieved()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        mount.IsAlignmentCorrected.ShouldBeFalse();

        // A plate-solve-driven sync to a real target (away from the pole) models
        // the mount learning its true orientation.
        await mount.SyncRaDecAsync(6.0, 45.0, ct);

        mount.IsAlignmentCorrected.ShouldBeTrue();
        // After correction the reported pointing is the believed (encoder)
        // position verbatim -- no residual offset.
        (await mount.GetRightAscensionAsync(ct)).ShouldBe(6.0, 0.01);
        (await mount.GetDeclinationAsync(ct)).ShouldBe(45.0, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedMountWhenSyncToPoleThenAlignmentNotLearned()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        // A sync to the pole (startup / park convention) must NOT learn alignment,
        // or the polar-align simulation would be zeroed before it can run.
        var lst = await mount.GetSiderealTimeAsync(ct);
        await mount.SyncRaDecAsync(lst, 90.0, ct);

        mount.IsAlignmentCorrected.ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSouthernMisalignedMountWhenSyncAwayFromPoleThenLearnsAlignment()
    {
        var ct = TestContext.Current.CancellationToken;
        // Southern site -> the home pole is -90; hemisphere is driven by site lat.
        var (mount, _) = await CreateConnectedMountAsync(
            ct, latitude: -33.9, longitude: 18.4,
            azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        mount.IsAlignmentCorrected.ShouldBeFalse();

        // Sync to a southern target well away from the -90 pole.
        await mount.SyncRaDecAsync(6.0, -45.0, ct);

        mount.IsAlignmentCorrected.ShouldBeTrue();
        (await mount.GetDeclinationAsync(ct)).ShouldBe(-45.0, 0.01);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenMisalignedMountWhenTrackingAfterSyncThenFieldDriftsSlowlyAndAccumulates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, external) = await CreateConnectedMountAsync(
            ct, azMisalignmentArcmin: TestAzMisalignArcmin, altMisalignmentArcmin: TestAltMisalignArcmin);

        // Plate-solve sync away from the pole: the static offset is learned, and
        // the drift reference is set to this freshly-centred position.
        await mount.SyncRaDecAsync(6.0, 45.0, ct);
        mount.IsAlignmentCorrected.ShouldBeTrue();

        // At the moment of sync the field sits exactly on target (zero residual) --
        // this is what lets the centering loop converge.
        var ra0 = await mount.GetRightAscensionAsync(ct);
        var dec0 = await mount.GetDeclinationAsync(ct);
        ra0.ShouldBe(6.0, 0.02);
        dec0.ShouldBe(45.0, 0.02);

        // Track for 5 sidereal minutes -- the misaligned axis leaks a slow drift.
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        var ra5 = await mount.GetRightAscensionAsync(ct);
        var dec5 = await mount.GetDeclinationAsync(ct);

        var drift5Arcsec = GreatCircleArcsec(ra0, dec0, ra5, dec5);
        var decDrift5Arcsec = Math.Abs(dec5 - dec0) * 3600.0;
        output.WriteLine($"5min: total drift={drift5Arcsec:F1}\"  dec drift={decDrift5Arcsec:F1}\"");

        // Realistic polar-misalignment drift for ~30': tens of arcsec over 5 min --
        // NOT zero (the old "learned alignment zeroes everything" bug) and NOT
        // degrees (a runaway). Rule of thumb ~0.25"/min per arcmin -> ~40"/5min.
        drift5Arcsec.ShouldBeGreaterThan(3.0, "a misaligned mount must drift while tracking");
        drift5Arcsec.ShouldBeLessThan(900.0, "drift must stay realistic, not run away");

        // Drift accumulates with elapsed tracking time (track 5 more minutes).
        external.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        var ra10 = await mount.GetRightAscensionAsync(ct);
        var dec10 = await mount.GetDeclinationAsync(ct);
        var drift10Arcsec = GreatCircleArcsec(ra0, dec0, ra10, dec10);
        output.WriteLine($"10min: total drift={drift10Arcsec:F1}\"");
        drift10Arcsec.ShouldBeGreaterThan(drift5Arcsec, "drift grows with tracking time");
    }

    /// <summary>Great-circle separation between two (RA hours, Dec deg) points, in arcsec.</summary>
    private static double GreatCircleArcsec(double ra1Hours, double dec1Deg, double ra2Hours, double dec2Deg)
    {
        var ra1 = ra1Hours * 15.0 * Math.PI / 180.0;
        var ra2 = ra2Hours * 15.0 * Math.PI / 180.0;
        var dec1 = dec1Deg * Math.PI / 180.0;
        var dec2 = dec2Deg * Math.PI / 180.0;
        var cosSep = Math.Sin(dec1) * Math.Sin(dec2)
            + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
        var sepRad = Math.Acos(Math.Clamp(cosSep, -1.0, 1.0));
        return sepRad * 180.0 / Math.PI * 3600.0;
    }

    #endregion
}
