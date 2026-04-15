using Shouldly;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceDiscoveryExtensions.ReconcileUri"/>. Covers the
/// "plug the mount into a different USB hub and COM5 becomes COM6" scenario (and the
/// DHCP-reassigned-WiFi-IP analogue): stored URI and discovered URI share the same
/// identity (scheme+authority+path) but the query drifted; reconciliation must adopt
/// the live discovered URI.
/// </summary>
public class DeviceDiscoveryExtensionsTests
{
    private static readonly Uri OnStepSerialCom5 =
        new Uri("Mount://OnStepDevice/abce318ef?port=COM5#Teesek 11");

    private static readonly Uri OnStepSerialCom6 =
        new Uri("Mount://OnStepDevice/abce318ef?port=COM6#Teesek 11");

    private static readonly Uri OnStepWifi =
        new Uri("Mount://OnStepDevice/abce318ef?host=192.168.1.42&tcp=9999#Teesek 11");

    [Fact]
    public void GivenSameDeviceIdWithDifferentPortReturnsDiscoveredUri()
    {
        var discovery = new StubDiscovery
        {
            MountDevices = [new FakeDeviceRecord(OnStepSerialCom6)]
        };

        var resolved = discovery.ReconcileUri(OnStepSerialCom5);

        resolved.ShouldBe(OnStepSerialCom6, "discovered URI is fresher — COM port was reassigned by the OS");
    }

    [Fact]
    public void GivenSameDeviceIdWithIdenticalUriReturnsStoredUri()
    {
        var discovery = new StubDiscovery
        {
            MountDevices = [new FakeDeviceRecord(OnStepSerialCom5)]
        };

        var resolved = discovery.ReconcileUri(OnStepSerialCom5);

        resolved.ShouldBeSameAs(OnStepSerialCom5, "no drift — must return the input unchanged to avoid pointless log spam");
    }

    [Fact]
    public void GivenNoMatchingDeviceIdReturnsStoredUri()
    {
        var discovery = new StubDiscovery
        {
            MountDevices = [new FakeDeviceRecord(new Uri("Mount://OnStepDevice/different-id?port=COM7#Other"))]
        };

        var resolved = discovery.ReconcileUri(OnStepSerialCom5);

        resolved.ShouldBeSameAs(OnStepSerialCom5, "different deviceId — no reconciliation applies");
    }

    [Fact]
    public void GivenSameDeviceIdSwitchingFromSerialToWifiReturnsWifiUri()
    {
        // User plugs the mount into WiFi while the profile still references the old serial URI.
        // Same deviceId (UUID in site-slot 4) → adopt the live WiFi URI.
        var discovery = new StubDiscovery
        {
            MountDevices = [new FakeDeviceRecord(OnStepWifi)]
        };

        var resolved = discovery.ReconcileUri(OnStepSerialCom5);

        resolved.ShouldBe(OnStepWifi, "transport may flip entirely — query drift is unconstrained");
    }

    [Fact]
    public void GivenStoredHasUserSetSiteAndDiscoveredHasDefaultSitePreservesUserSite()
    {
        // Reproduces the real-world "my latitude keeps getting reset to 48.2" bug:
        // user edited their site to high-precision 48.21 / 16.37, but the FakeMount
        // discovery advertises the source-default 48.2 / 16.3. Pre-fix, reconcile
        // clobbered the user's values; post-fix, user config is preserved while
        // transport (port) still refreshes from discovery.
        var storedUserSite = new Uri("Mount://FakeDevice/FakeMount_SkyWatcher?latitude=48.21&longitude=16.37&port=SkyWatcher#Fake Mount (SkyWatcher)");
        var discoveredDefaults = new Uri("Mount://FakeDevice/FakeMount_SkyWatcher?latitude=48.2&longitude=16.3&port=SkyWatcher#Fake Mount (SkyWatcher)");

        var discovery = new StubDiscovery
        {
            MountDevices = [new FakeDeviceRecord(discoveredDefaults)]
        };

        var resolved = discovery.ReconcileUri(storedUserSite);

        resolved.QueryValue(DeviceQueryKey.Latitude).ShouldBe("48.21", "user's precise latitude must survive reconcile");
        resolved.QueryValue(DeviceQueryKey.Longitude).ShouldBe("16.37", "user's precise longitude must survive reconcile");
        resolved.QueryValue(DeviceQueryKey.Port).ShouldBe("SkyWatcher", "transport (port) still reflected from discovery");
    }

    [Fact]
    public void GivenStoredHasFilterSlotAndDiscoveredOmitsItPreservesFilterSlot()
    {
        // Filter slot keys (filter1..filterN) are user-configured; they must survive
        // reconcile even when the device source doesn't advertise them.
        var storedWithFilters = new Uri("FilterWheel://QHYDevice/cfw123?filter1=Luminance&filter2=Red&port=COM7");
        var discoveredNoFilters = new Uri("FilterWheel://QHYDevice/cfw123?port=COM8");

        var discovery = new StubDiscovery
        {
            FilterWheels = [new FakeDeviceRecord(discoveredNoFilters)]
        };

        var resolved = discovery.ReconcileUri(storedWithFilters);

        resolved.ToString().ShouldContain("filter1=Luminance");
        resolved.ToString().ShouldContain("filter2=Red");
        resolved.QueryValue(DeviceQueryKey.Port).ShouldBe("COM8");
    }

    [Fact]
    public void GivenUnknownDeviceTypeSchemeReturnsStoredUri()
    {
        var discovery = new StubDiscovery();

        var junk = new Uri("not-a-device://foo/bar");
        var resolved = discovery.ReconcileUri(junk);

        resolved.ShouldBeSameAs(junk, "unknown scheme — skip reconciliation rather than throw");
    }

    [Fact]
    public void GivenProfileDataWithNoDriftReturnsChangedFalse()
    {
        var discovery = new StubDiscovery
        {
            Mounts = [new FakeDeviceRecord(OnStepSerialCom5)]
        };

        var data = MakeProfileData(mount: OnStepSerialCom5);

        var (reconciled, changed) = discovery.ReconcileProfileData(data);

        changed.ShouldBe(false, "profile URIs already match discovery — no rewrite");
        reconciled.ShouldBe(data); // value equality on record struct
    }

    [Fact]
    public void GivenProfileDataWithMountDriftReconcilesMountAndFlagsChange()
    {
        var discovery = new StubDiscovery
        {
            Mounts = [new FakeDeviceRecord(OnStepSerialCom6)] // freshly discovered on COM6
        };

        var data = MakeProfileData(mount: OnStepSerialCom5); // stored with COM5

        var (reconciled, changed) = discovery.ReconcileProfileData(data);

        changed.ShouldBe(true);
        reconciled.Mount.ShouldBe(OnStepSerialCom6);
    }

    [Fact]
    public void GivenProfileDataWithOtaCameraDriftReconcilesThatOtaOnly()
    {
        var camA = new Uri("Camera://ZwoDevice/cam123?gain=100#Main");
        var camAReconciled = new Uri("Camera://ZwoDevice/cam123?gain=100&offset=15#Main"); // query differs
        var camB = new Uri("Camera://ZwoDevice/guide456?gain=50#Guide"); // unrelated — no drift

        var discovery = new StubDiscovery
        {
            Cameras = [new FakeDeviceRecord(camAReconciled), new FakeDeviceRecord(camB)]
        };

        var ota1 = new OTAData("OTA1", 1000, camA, null, null, null, null, null);
        var ota2 = new OTAData("OTA2", 2000, camB, null, null, null, null, null);
        var data = MakeProfileData(mount: OnStepSerialCom5, otas: [ota1, ota2]);
        // Pre-seed mount in discovery too so only the OTA1 camera drifts.
        discovery.Mounts = [new FakeDeviceRecord(OnStepSerialCom5)];

        var (reconciled, changed) = discovery.ReconcileProfileData(data);

        changed.ShouldBe(true);
        reconciled.OTAs.Length.ShouldBe(2);
        reconciled.OTAs[0].Camera.ShouldBe(camAReconciled, "first OTA's camera query drifted — adopt discovered");
        reconciled.OTAs[1].Camera.ShouldBe(camB, "second OTA's camera matches exactly — unchanged");
    }

    private static ProfileData MakeProfileData(Uri mount, ImmutableArray<OTAData>? otas = null)
        => new ProfileData(
            Mount: mount,
            Guider: new Uri("Guider://NoneDevice/none"),
            OTAs: otas ?? []);

    /// <summary>
    /// Minimal <see cref="IDeviceDiscovery"/> stub. Returns per-type fixed lists so tests
    /// can seed discovery results for mount, camera, etc. independently.
    /// </summary>
    private sealed class StubDiscovery : IDeviceDiscovery
    {
        public IReadOnlyList<DeviceBase> Mounts { get; set; } = [];
        public IReadOnlyList<DeviceBase> Cameras { get; set; } = [];
        public IReadOnlyList<DeviceBase> FilterWheels { get; set; } = [];

        // Back-compat alias for existing tests.
        public IReadOnlyList<DeviceBase> MountDevices { set => Mounts = value; }

        public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount, DeviceType.Camera, DeviceType.FilterWheel];

        public IEnumerable<DeviceBase> RegisteredDevices(DeviceType deviceType) => deviceType switch
        {
            DeviceType.Mount => Mounts,
            DeviceType.Camera => Cameras,
            DeviceType.FilterWheel => FilterWheels,
            _ => []
        };

        public ValueTask DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DiscoverOnlyDeviceType(DeviceType type, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    /// <summary>
    /// Minimal <see cref="DeviceBase"/> that exposes only its URI — enough for
    /// reconciliation which only reads <c>DeviceUri</c>.
    /// </summary>
    private sealed record FakeDeviceRecord(Uri DeviceUri) : DeviceBase(DeviceUri);
}
