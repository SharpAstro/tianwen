using Shouldly;
using System;
using System.Collections.Generic;
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
    public void GivenUnknownDeviceTypeSchemeReturnsStoredUri()
    {
        var discovery = new StubDiscovery();

        var junk = new Uri("not-a-device://foo/bar");
        var resolved = discovery.ReconcileUri(junk);

        resolved.ShouldBeSameAs(junk, "unknown scheme — skip reconciliation rather than throw");
    }

    /// <summary>
    /// Minimal <see cref="IDeviceDiscovery"/> stub that returns a fixed list of devices
    /// for the <see cref="DeviceType.Mount"/> type.
    /// </summary>
    private sealed class StubDiscovery : IDeviceDiscovery
    {
        public IReadOnlyList<DeviceBase> MountDevices { get; set; } = [];

        public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount];

        public IEnumerable<DeviceBase> RegisteredDevices(DeviceType deviceType)
            => deviceType is DeviceType.Mount ? MountDevices : [];

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
