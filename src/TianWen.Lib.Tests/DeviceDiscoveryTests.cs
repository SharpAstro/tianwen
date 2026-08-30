using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// <see cref="DeviceDiscovery.DiscoverOnlyDeviceType"/> used to run the centralised serial probe pass
/// unconditionally, so a PROFILE scan (a handful of JSON files) opened every COM port and ran all nine
/// protocol probes: at GUI start-up on the main thread, where a Bluetooth port with no peer parked the
/// process for good, and from <c>MountLimitWatcher</c> on every 5 s tick. The pass belongs to the types
/// whose sources consume it, and to nothing else.
/// </summary>
public class DeviceDiscoveryTests
{
    [Fact]
    public async Task GivenNoSourceForTheTypeConsumesTheSerialProbeWhenDiscoveringOnlyThatTypeThenNoPortIsProbed()
    {
        var ct = TestContext.Current.CancellationToken;
        var probes = Substitute.For<ISerialProbeService>();
        var profiles = SourceFor(DeviceType.Profile, consumesSerialProbe: false);
        var mounts = SourceFor(DeviceType.Mount, consumesSerialProbe: true);
        var discovery = new DeviceDiscovery(NullLogger<DeviceDiscovery>.Instance, [profiles, mounts], probes);

        await discovery.DiscoverOnlyDeviceType(DeviceType.Profile, ct);

        await probes.DidNotReceive().ProbeAllAsync(Arg.Any<CancellationToken>());
        await profiles.Received(1).DiscoverAsync(Arg.Any<CancellationToken>());
        await mounts.DidNotReceive().DiscoverAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GivenASourceForTheTypeConsumesTheSerialProbeWhenDiscoveringOnlyThatTypeThenThePortsAreProbed()
    {
        var ct = TestContext.Current.CancellationToken;
        var probes = Substitute.For<ISerialProbeService>();
        var profiles = SourceFor(DeviceType.Profile, consumesSerialProbe: false);
        var mounts = SourceFor(DeviceType.Mount, consumesSerialProbe: true);
        var discovery = new DeviceDiscovery(NullLogger<DeviceDiscovery>.Instance, [profiles, mounts], probes);

        await discovery.DiscoverOnlyDeviceType(DeviceType.Mount, ct);

        await probes.Received(1).ProbeAllAsync(Arg.Any<CancellationToken>());
        await mounts.Received(1).DiscoverAsync(Arg.Any<CancellationToken>());
        await profiles.DidNotReceive().DiscoverAsync(Arg.Any<CancellationToken>());
    }

    private static IDeviceSource<DeviceBase> SourceFor(DeviceType type, bool consumesSerialProbe)
    {
        var source = Substitute.For<IDeviceSource<DeviceBase>>();
        source.CheckSupportAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        source.RegisteredDeviceTypes.Returns(new[] { type });
        source.ConsumesSerialProbe.Returns(consumesSerialProbe);
        source.DiscoverAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        return source;
    }
}
