using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Discovery;
using TianWen.Lib.Devices.OnStep;
using TianWen.Lib.Devices.Skywatcher;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// A discovery probe of a host that never answers ends at its own deadline on the injected clock. Each deadline is set
/// to an hour, and the fake clock is advanced by that hour ONCE, as soon as the probe is waiting on the host (for OnStep,
/// once the host has its first request, so the TCP connect's own timer is gone): the probe's deadline fires, and the
/// timers the probe makes after the advance never do. On the wall clock the probe would hold for the hour.
/// </summary>
public class DiscoveryDeadlineTests
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);

    [Fact(Timeout = 30_000)]
    public async Task AnOnStepWifiHostThatNeverAnswersEndsItsProbeAtTheDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clock = new FakeTimeProviderWrapper();
        var source = new OnStepDeviceSource(NullLogger<OnStepDeviceSource>.Instance, new NoSerialMatches(), LocalOnlyMode.Off, clock)
        {
            TcpProbeTimeout = Long
        };
        var devices = new Dictionary<DeviceType, List<OnStepDevice>>();

        var accepted = listener.AcceptTcpClientAsync(ct);
        var probe = source.QueryWifiHostAsync(devices, "silent", "127.0.0.1", port, ct);
        using var host = await accepted;
        var request = new byte[5];
        await host.GetStream().ReadExactlyAsync(request, ct);
        clock.Advance(Long);
        await probe;

        devices.ShouldBeEmpty("nothing answered");
    }

    [Fact(Timeout = 30_000)]
    public async Task ASkywatcherWifiBroadcastNobodyAnswersEndsAtItsWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var target = (IPEndPoint)silent.Client.LocalEndPoint!;
        var clock = new FakeTimeProviderWrapper();
        var source = new SkywatcherDeviceSource(new NoSerialMatches(), NullLogger<SkywatcherDeviceSource>.Instance, LocalOnlyMode.Off, clock)
        {
            WifiAnswerWindow = Long
        };

        var found = CollectAsync(source.DiscoverWiFiAsync(target, ct));
        clock.Advance(Long);

        (await found).ShouldBeEmpty("nothing answered");
    }

    private static async Task<List<SkywatcherDevice>> CollectAsync(IAsyncEnumerable<SkywatcherDevice> devices)
    {
        var list = new List<SkywatcherDevice>();
        await foreach (var device in devices)
        {
            list.Add(device);
        }
        return list;
    }

    private sealed class NoSerialMatches : ISerialProbeService
    {
        public ValueTask ProbeAllAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public IReadOnlyList<SerialProbeMatch> ResultsFor(string probeName) => [];
    }
}
