using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins <see cref="MountLimitWatcher"/> -- P3 of <c>docs/plans/mount-safety-limits.md</c>, the half
/// that protects a mount with no session running. <see cref="SessionMountLimitTests"/> pins the
/// session-scoped half (P2); this is deliberately a separate, session-free suite, since the whole
/// point of this class is that it works when there is no <see cref="Session"/> to drive.
/// </summary>
public class MountLimitWatcherTests
{
    private static readonly Uri MountUri = new Uri("Mount://FakeDevice/FakeMount1");

    /// <summary>Warn at 5 min past the meridian, act at 10 by stopping tracking.</summary>
    private static MountLimitConfiguration StopTrackingPastTheMeridian => new MountLimitConfiguration(
        Enabled: true,
        MeridianWarnMinutes: 5.0,
        MeridianActionExtraMinutes: 5.0,
        MeridianResponse: MountLimitResponse.StopTracking);

    private static IMountDriver MountAt(
        double hourAngleHours, bool isTracking = true, bool canPark = false,
        PointingState pointingState = PointingState.ThroughThePole)
    {
        var mount = Substitute.For<IMountDriver>();
        mount.Name.Returns("FakeMount1");
        mount.Connected.Returns(true);
        mount.Logger.Returns(NullLogger.Instance);
        mount.GetHourAngleAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(hourAngleHours));
        // Configured explicitly, always. An unconfigured NSubstitute ValueTask<PointingState> returns
        // default, and default(PointingState) is Normal -- the FLIPPED state, in which the meridian limit
        // is silent -- so left to the default every "it stops the mount" case here would go green with
        // enforcement deleted. Through-the-pole (counterweight down, looking east, not yet flipped) is
        // the state in which tracking west carries the tube toward the pier.
        mount.GetSideOfPierAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(pointingState));
        mount.GetDeclinationAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(45.0));
        mount.IsTrackingAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(isTracking));
        mount.SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        mount.CanPark.Returns(canPark);
        mount.ParkAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        return mount;
    }

    private static (IDeviceHub Hub, IDeviceDiscovery Discovery) HubAndDiscoveryFor(
        IMountDriver mount, MountLimitConfiguration? limits, bool leased = false, double? siteLatitude = 45.0)
    {
        var hub = Substitute.For<IDeviceHub>();
        hub.ConnectedDevices.Returns(new List<(Uri DeviceUri, IDeviceDriver Driver)> { (MountUri, mount) });
        hub.TryGetLease(MountUri, out Arg.Any<DeviceLease>()).Returns(leased);

        var discovery = Substitute.For<IDeviceDiscovery>();
        var profileData = new ProfileData(
            Mount: MountUri,
            Guider: NoneDevice.Instance.DeviceUri,
            OTAs: [],
            SiteLatitude: siteLatitude,
            MountLimits: limits);
        var profile = new Profile(Guid.NewGuid(), "Test profile", profileData);
        discovery.RegisteredDevices(DeviceType.Profile).Returns(new DeviceBase[] { profile });

        return (hub, discovery);
    }

    private static MountLimitWatcher WatcherFor(IDeviceHub hub, IDeviceDiscovery discovery)
        => new MountLimitWatcher(hub, discovery, Substitute.For<ITimeProvider>(), NullLogger<MountLimitWatcher>.Instance);

    [Fact]
    public async Task AMountWithinItsLimitsIsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        // 1 min east of the meridian: approaching, but not even at the warn threshold.
        var mount = MountAt(hourAngleHours: -1.0 / 60.0);
        var (hub, discovery) = HubAndDiscoveryFor(mount, StopTrackingPastTheMeridian);

        await WatcherFor(hub, discovery).TickAsync(ct);

        await mount.DidNotReceive().SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AMountThatHasFlippedIsLeftAloneHoweverFarPastTheMeridianItTracks()
    {
        var ct = TestContext.Current.CancellationToken;
        // Normal = counterweight down while looking west = AFTER the flip. Tracking west from there moves
        // the tube away from the pier; the hour-angle-only first cut stopped this rig 30 min post-flip.
        var mount = MountAt(hourAngleHours: 1.0, pointingState: PointingState.Normal);
        var (hub, discovery) = HubAndDiscoveryFor(mount, StopTrackingPastTheMeridian);

        await WatcherFor(hub, discovery).TickAsync(ct);

        await mount.DidNotReceive().SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PastTheWarnThresholdItWarnsAndDoesNotTouchTheMount()
    {
        var ct = TestContext.Current.CancellationToken;
        // 7 min past the meridian: over the 5 min warn threshold, under the 10 min action one.
        var mount = MountAt(hourAngleHours: 7.0 / 60.0);
        var (hub, discovery) = HubAndDiscoveryFor(mount, StopTrackingPastTheMeridian);

        await WatcherFor(hub, discovery).TickAsync(ct);

        await mount.DidNotReceive().SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PastTheActionThresholdTheMountIsStopped()
    {
        var ct = TestContext.Current.CancellationToken;
        // 15 min past the meridian, comfortably beyond the 10 min action threshold.
        var mount = MountAt(hourAngleHours: 1.0);
        var (hub, discovery) = HubAndDiscoveryFor(mount, StopTrackingPastTheMeridian);

        await WatcherFor(hub, discovery).TickAsync(ct);

        await mount.Received(1).SetTrackingAsync(false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheActionFiresOnceAndThenLatchesUntilClear()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = MountAt(hourAngleHours: 1.0);
        var (hub, discovery) = HubAndDiscoveryFor(mount, StopTrackingPastTheMeridian);
        var watcher = WatcherFor(hub, discovery);

        await watcher.TickAsync(ct);
        await mount.Received(1).SetTrackingAsync(false, Arg.Any<CancellationToken>());

        // The mount is still past the limit (tracking is a fake stub, not a real state machine), and
        // the poll runs continuously. Without the latch this re-issues the stop (and, for Park,
        // re-commands the park slew) every tick forever. Exactly one call must have happened by now.
        await watcher.TickAsync(ct);
        await mount.Received(1).SetTrackingAsync(false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LimitsThatAreNotConfiguredDoNothingAtAll()
    {
        var ct = TestContext.Current.CancellationToken;
        // A profile that never configured limits deserialises to null. Well past where the limit
        // above would have acted, and nothing happens -- opt-in is the point.
        var mount = MountAt(hourAngleHours: 1.0);
        var (hub, discovery) = HubAndDiscoveryFor(mount, limits: null);

        await WatcherFor(hub, discovery).TickAsync(ct);

        await mount.DidNotReceive().SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADisabledConfigurationIsAlsoInert()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = MountAt(hourAngleHours: 1.0);
        var (hub, discovery) = HubAndDiscoveryFor(mount, StopTrackingPastTheMeridian with { Enabled = false });

        await WatcherFor(hub, discovery).TickAsync(ct);

        await mount.DidNotReceive().SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ALeasedMountIsNeverActedOnEvenPastTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        // A session owns this mount and is already enforcing the same limit on its own poll; the
        // watcher acting too would be two actors racing the same axis. Deep past the action
        // threshold, and still nothing happens here.
        var mount = MountAt(hourAngleHours: 1.0);
        var (hub, discovery) = HubAndDiscoveryFor(mount, StopTrackingPastTheMeridian, leased: true);

        await WatcherFor(hub, discovery).TickAsync(ct);

        await mount.DidNotReceive().SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AMountWithNoMatchingProfileIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var mount = MountAt(hourAngleHours: 1.0);
        var (hub, discovery) = HubAndDiscoveryFor(mount, StopTrackingPastTheMeridian);
        // Rewire the hub to a connected mount whose URI matches no profile at all.
        var otherUri = new Uri("Mount://FakeDevice/FakeMount2");
        hub.ConnectedDevices.Returns(new List<(Uri DeviceUri, IDeviceDriver Driver)> { (otherUri, mount) });
        hub.TryGetLease(otherUri, out Arg.Any<DeviceLease>()).Returns(false);

        await WatcherFor(hub, discovery).TickAsync(ct);

        await mount.DidNotReceive().SetTrackingAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ParkResponseParksAMountThatCanAndStopsOneThatCannot()
    {
        var ct = TestContext.Current.CancellationToken;
        var parkConfig = StopTrackingPastTheMeridian with { MeridianResponse = MountLimitResponse.Park };

        var parkableMount = MountAt(hourAngleHours: 1.0, canPark: true);
        var (parkHub, parkDiscovery) = HubAndDiscoveryFor(parkableMount, parkConfig);
        await WatcherFor(parkHub, parkDiscovery).TickAsync(ct);
        await parkableMount.Received(1).SetTrackingAsync(false, Arg.Any<CancellationToken>());
        await parkableMount.Received(1).ParkAsync(Arg.Any<CancellationToken>());

        var unparkableMount = MountAt(hourAngleHours: 1.0, canPark: false);
        var (noParkHub, noParkDiscovery) = HubAndDiscoveryFor(unparkableMount, parkConfig);
        await WatcherFor(noParkHub, noParkDiscovery).TickAsync(ct);
        await unparkableMount.Received(1).SetTrackingAsync(false, Arg.Any<CancellationToken>());
        await unparkableMount.DidNotReceive().ParkAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANonMountDriverConnectedToTheHubIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = Substitute.For<IDeviceDriver>();
        camera.DriverType.Returns(DeviceType.Camera);

        var hub = Substitute.For<IDeviceHub>();
        hub.ConnectedDevices.Returns(new List<(Uri DeviceUri, IDeviceDriver Driver)> { (MountUri, camera) });
        var discovery = Substitute.For<IDeviceDiscovery>();
        discovery.RegisteredDevices(DeviceType.Profile).Returns(Array.Empty<DeviceBase>());

        // Must not throw casting the camera to IMountDriver, and must not call DiscoverOnlyDeviceType
        // pointlessly for every non-mount driver (it's only called once per tick regardless).
        await WatcherFor(hub, discovery).TickAsync(ct);

        await discovery.Received(1).DiscoverOnlyDeviceType(DeviceType.Profile, Arg.Any<CancellationToken>());
    }
}
