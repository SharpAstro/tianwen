using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class FakeSkywatcherMountDriverTests(ITestOutputHelper output)
{
    private (FakeSkywatcherMountDriver mount, FakeExternal external) CreateMount(
        double latitude = 48.2,
        double longitude = 16.3,
        DateTimeOffset? now = null)
    {
        var external = new FakeExternal(output, now: now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "port", "SkyWatcher" },
            { "latitude", latitude.ToString() },
            { "longitude", longitude.ToString() }
        });
        var mount = new FakeSkywatcherMountDriver(device, external.BuildServiceProvider());
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
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

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
    public async Task GivenMountWithLatLongInUriThenSiteReturnsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount(latitude: 48.2, longitude: 16.3);
        await mount.ConnectAsync(ct);

        var lat = await mount.GetSiteLatitudeAsync(ct);
        var lon = await mount.GetSiteLongitudeAsync(ct);

        lat.ShouldBe(48.2, 0.01);
        lon.ShouldBe(16.3, 0.01);
    }

    #endregion
}
