using Shouldly;
using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using TianWen.DAL;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

public class FakeSgpMountDriverTests(ITestOutputHelper output)
{
    private (FakeSgpMountDriver mount, FakeExternal external) CreateMount(
        double latitude = 48.2,
        double longitude = 16.3,
        DateTimeOffset? now = null)
    {
        var external = new FakeExternal(output, now: now ?? new DateTimeOffset(2025, 6, 15, 22, 0, 0, TimeSpan.Zero));
        var device = new FakeDevice(DeviceType.Mount, 1, new NameValueCollection
        {
            { "port", "SGP" },
            { "latitude", latitude.ToString() },
            { "longitude", longitude.ToString() }
        });
        var mount = new FakeSgpMountDriver(device, external.BuildServiceProvider());
        return (mount, external);
    }

    #region Connect / Disconnect

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenConnectedThenIsConnectedAndFirmwareAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();

        await mount.ConnectAsync(ct);

        mount.Connected.ShouldBeTrue();
        var driverInfo = mount.DriverInfo;
        driverInfo.ShouldNotBeNull();
        driverInfo.ShouldContain("iOptron SkyGuider Pro");
        driverInfo.ShouldContain("FW");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedSgpMountWhenDisconnectedThenIsNotConnected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();

        await mount.ConnectAsync(ct);
        mount.Connected.ShouldBeTrue();

        await mount.DisconnectAsync(ct);
        mount.Connected.ShouldBeFalse();
    }

    #endregion

    #region Tracking Rates

    [Theory(Timeout = 60_000)]
    [InlineData(TrackingSpeed.Sidereal)]
    [InlineData(TrackingSpeed.Lunar)]
    [InlineData(TrackingSpeed.Solar)]
    public async Task GivenConnectedSgpMountWhenTrackingSpeedSetThenItIsReported(TrackingSpeed speed)
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetTrackingSpeedAsync(speed, ct);

        var reported = await mount.GetTrackingSpeedAsync(ct);
        reported.ShouldBe(speed);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedSgpMountWhenTrackingEnabledThenIsTrackingReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetTrackingAsync(true, ct);

        (await mount.IsTrackingAsync(ct)).ShouldBeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenConnectedSgpMountThenTrackingSpeedsContainExpectedValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.TrackingSpeeds.ShouldContain(TrackingSpeed.Sidereal);
        mount.TrackingSpeeds.ShouldContain(TrackingSpeed.Lunar);
        mount.TrackingSpeeds.ShouldContain(TrackingSpeed.Solar);
    }

    #endregion

    #region Hemisphere

    [Fact(Timeout = 60_000)]
    public async Task GivenNorthernHemisphereSgpMountThenDecIs90()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount(latitude: 48.2);
        await mount.ConnectAsync(ct);

        await mount.SetHemisphereAsync(true, ct);

        var dec = await mount.GetDeclinationAsync(ct);
        dec.ShouldBe(90.0);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSouthernHemisphereSgpMountThenDecIsMinus90()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount(latitude: -33.9);
        await mount.ConnectAsync(ct);

        await mount.SetHemisphereAsync(false, ct);

        var dec = await mount.GetDeclinationAsync(ct);
        dec.ShouldBe(-90.0);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenHemisphereSwitchedThenDecUpdates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SetHemisphereAsync(true, ct);
        (await mount.GetDeclinationAsync(ct)).ShouldBe(90.0);

        await mount.SetHemisphereAsync(false, ct);
        (await mount.GetDeclinationAsync(ct)).ShouldBe(-90.0);
    }

    #endregion

    #region RA MoveAxis

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenMoveAxisPrimaryThenIsSlewing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        // Start moving at speed-1 rate west (positive)
        var rate = mount.AxisRates(TelescopeAxis.Primary)[0].Maximum;
        await mount.MoveAxisAsync(TelescopeAxis.Primary, rate, ct);

        (await mount.IsSlewingAsync(ct)).ShouldBeTrue();

        // Stop
        await mount.MoveAxisAsync(TelescopeAxis.Primary, 0.0, ct);
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenMoveAxisSecondaryThenThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await Should.ThrowAsync<InvalidOperationException>(
            () => mount.MoveAxisAsync(TelescopeAxis.Seconary, 1.0, ct).AsTask());
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenMoveAxisTertiaryThenThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await Should.ThrowAsync<InvalidOperationException>(
            () => mount.MoveAxisAsync(TelescopeAxis.Tertiary, 1.0, ct).AsTask());
    }

    [Theory(Timeout = 60_000)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task GivenSgpMountWhenMoveAxisAtVariousSpeedsThenAccepted(int speedIndex)
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        var rates = mount.AxisRates(TelescopeAxis.Primary);
        rates.Count.ShouldBe(7);

        var rate = rates[speedIndex - 1].Maximum;
        await mount.MoveAxisAsync(TelescopeAxis.Primary, rate, ct);
        (await mount.IsSlewingAsync(ct)).ShouldBeTrue();

        await mount.MoveAxisAsync(TelescopeAxis.Primary, 0.0, ct);
        (await mount.IsSlewingAsync(ct)).ShouldBeFalse();
    }

    #endregion

    #region Pulse Guide (not supported — requires ST-4)

    [Theory(Timeout = 60_000)]
    [InlineData(GuideDirection.East)]
    [InlineData(GuideDirection.West)]
    [InlineData(GuideDirection.North)]
    [InlineData(GuideDirection.South)]
    public async Task GivenSgpMountWhenPulseGuideThenThrows(GuideDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await Should.ThrowAsync<InvalidOperationException>(
            () => mount.PulseGuideAsync(direction, TimeSpan.FromMilliseconds(500), ct).AsTask());
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountThenIsPulseGuidingAlwaysFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        (await mount.IsPulseGuidingAsync(ct)).ShouldBeFalse();
    }

    #endregion

    #region Camera Snap

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenCameraSnapTriggeredThenSettingsReadBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        var settings = new CameraSnapSettings(
            ShutterTime: TimeSpan.FromSeconds(60),
            Interval: TimeSpan.FromSeconds(10),
            ShotCount: 5);

        await mount.CameraSnapAsync(settings, ct);

        var readBack = await mount.GetCameraSnapSettingsAsync(ct);
        readBack.ShouldNotBeNull();
        readBack.Value.ShutterTime.ShouldBe(TimeSpan.FromSeconds(60));
        readBack.Value.Interval.ShouldBe(TimeSpan.FromSeconds(10));
        readBack.Value.ShotCount.ShouldBe(5);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenCameraSnapSettingsQueriedBeforeSnapThenDefaultsReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        // Default values from FakeSgpSerialDevice: shutter=30, interval=5, shots=2
        var readBack = await mount.GetCameraSnapSettingsAsync(ct);
        readBack.ShouldNotBeNull();
        readBack.Value.ShutterTime.ShouldBe(TimeSpan.FromSeconds(30));
        readBack.Value.Interval.ShouldBe(TimeSpan.FromSeconds(5));
        readBack.Value.ShotCount.ShouldBe(2);
    }

    #endregion

    #region Sync

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenSyncedThenRaDecUpdated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SyncRaDecAsync(12.5, 45.0, ct);

        var ra = await mount.GetRightAscensionAsync(ct);
        var dec = await mount.GetDeclinationAsync(ct);

        ra.ShouldBe(12.5, 0.001);
        dec.ShouldBe(45.0, 0.001);
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenSyncedMultipleTimesThenLatestValueUsed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await mount.SyncRaDecAsync(6.0, 30.0, ct);
        await mount.SyncRaDecAsync(18.0, -15.0, ct);

        (await mount.GetRightAscensionAsync(ct)).ShouldBe(18.0, 0.001);
        (await mount.GetDeclinationAsync(ct)).ShouldBe(-15.0, 0.001);
    }

    #endregion

    #region Capabilities

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountThenCapabilitiesReflectRaOnlyMount()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        mount.CanPulseGuide.ShouldBeFalse(); // requires ST-4 guide port
        mount.CanSetTracking.ShouldBeFalse(); // SGP always tracks, no stop command
        mount.CanSync.ShouldBeTrue();
        mount.CanCameraSnap.ShouldBeTrue();
        mount.CanSetGuideRates.ShouldBeTrue();

        mount.CanSlew.ShouldBeFalse();
        mount.CanSlewAsync.ShouldBeFalse();
        mount.CanPark.ShouldBeFalse();
        mount.CanSetSideOfPier.ShouldBeFalse();
        mount.CanSetRightAscensionRate.ShouldBeFalse();
        mount.CanSetDeclinationRate.ShouldBeFalse();

        mount.CanMoveAxis(TelescopeAxis.Primary).ShouldBeTrue();
        mount.CanMoveAxis(TelescopeAxis.Seconary).ShouldBeFalse();
        mount.CanMoveAxis(TelescopeAxis.Tertiary).ShouldBeFalse();
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenSlewAttemptedThenThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await Should.ThrowAsync<InvalidOperationException>(
            () => mount.BeginSlewRaDecAsync(12.0, 45.0, ct).AsTask());
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenSetDecGuideRateThenThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        await Should.ThrowAsync<InvalidOperationException>(
            () => mount.SetGuideRateDeclinationAsync(0.5, ct).AsTask());
    }

    #endregion

    #region Guide Rate

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWhenGuideRateSetThenReadBackMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var (mount, _) = CreateMount();
        await mount.ConnectAsync(ct);

        // Set guide rate to 0.75x sidereal
        var siderealRateDegPerSec = 15.0417 / 3600.0;
        var desiredRate = 0.75 * siderealRateDegPerSec;
        await mount.SetGuideRateRightAscensionAsync(desiredRate, ct);

        var readBack = await mount.GetGuideRateRightAscensionAsync(ct);
        // Rounding to integer percentage: 0.75 → 75%
        readBack.ShouldBe(0.75 * siderealRateDegPerSec, siderealRateDegPerSec * 0.02);
    }

    #endregion

    #region Site

    [Fact(Timeout = 60_000)]
    public async Task GivenSgpMountWithLatLongInUriThenSiteReturnsThem()
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
