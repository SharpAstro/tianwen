using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices.Alpaca;

/// <summary>
/// Discovers Alpaca servers via UDP broadcast on port 32227, then queries
/// each server's management API for configured devices.
/// </summary>
internal class AlpacaDeviceSource(AlpacaClient alpacaClient, ILogger<AlpacaDeviceSource> logger) : IDeviceSource<AlpacaDevice>
{
    private const int AlpacaDiscoveryPort = 32227;
    private static readonly byte[] DiscoveryMessage = Encoding.ASCII.GetBytes("alpacadiscovery1");

    private static readonly HashSet<DeviceType> SupportedDeviceTypes =
    [
        DeviceType.Camera,
        DeviceType.CoverCalibrator,
        DeviceType.FilterWheel,
        DeviceType.Focuser,
        DeviceType.Switch,
        DeviceType.Telescope
    ];

    private Dictionary<DeviceType, List<AlpacaDevice>> _devices = [];

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken = default)
    {
        // Alpaca is HTTP-based and works on all platforms
        return ValueTask.FromResult(true);
    }

    public IEnumerable<DeviceType> RegisteredDeviceTypes => SupportedDeviceTypes;

    public IEnumerable<AlpacaDevice> RegisteredDevices(DeviceType deviceType) =>
        _devices.TryGetValue(deviceType, out var devices) ? devices : [];

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var servers = await DiscoverServersAsync(cancellationToken);
        var devices = new Dictionary<DeviceType, List<AlpacaDevice>>();

        foreach (var (host, port) in servers)
        {
            try
            {
                var baseUrl = $"http://{host}:{port}";
                var configuredDevices = await alpacaClient.GetConfiguredDevicesAsync(baseUrl, cancellationToken);

                if (configuredDevices is null)
                {
                    continue;
                }

                foreach (var configured in configuredDevices)
                {
                    var deviceType = DeviceTypeHelper.TryParseDeviceType(configured.DeviceType);
                    if (deviceType is DeviceType.Unknown || !SupportedDeviceTypes.Contains(deviceType))
                    {
                        continue;
                    }

                    if (!devices.TryGetValue(deviceType, out var list))
                    {
                        list = [];
                        devices[deviceType] = list;
                    }

                    list.Add(new AlpacaDevice(deviceType, configured.UniqueID, host, port, configured.DeviceNumber, configured.DeviceName)
                    {
                        Client = alpacaClient
                    });
                }
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Failed to query Alpaca server at {Host}:{Port}: {ErrorMessage}", host, port, e.Message);
            }
        }

        Interlocked.Exchange(ref _devices, devices);
    }

    /// <summary>
    /// Sends a UDP broadcast discovery message and collects server responses.
    /// Resolves IP addresses to hostnames via reverse DNS for stable identification.
    /// </summary>
    private static async Task<List<(string Host, int Port)>> DiscoverServersAsync(CancellationToken cancellationToken)
    {
        var servers = new List<(string Host, int Port)>();

        try
        {
            using var udpClient = new UdpClient();
            udpClient.EnableBroadcast = true;

            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, AlpacaDiscoveryPort);
            await udpClient.SendAsync(DiscoveryMessage, broadcastEndpoint, cancellationToken);

            // Wait for responses with a timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            while (!timeoutCts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync(timeoutCts.Token);
                    var json = Encoding.UTF8.GetString(result.Buffer);
                    var discovery = JsonSerializer.Deserialize(json, AlpacaJsonSerializerContext.Default.AlpacaDiscoveryResponse);

                    if (discovery is { AlpacaPort: > 0 })
                    {
                        var host = await ResolveHostNameAsync(result.RemoteEndPoint.Address);
                        servers.Add((host, discovery.AlpacaPort));
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (SocketException)
        {
            // UDP broadcast not available (e.g. no network interface)
        }

        return servers;
    }

    /// <summary>
    /// Resolves an IP address to its FQDN via reverse DNS lookup.
    /// Falls back to the IP address string if resolution fails.
    /// </summary>
    private static async Task<string> ResolveHostNameAsync(IPAddress address)
    {
        try
        {
            var hostEntry = await Dns.GetHostEntryAsync(address);
            if (!string.IsNullOrEmpty(hostEntry.HostName))
            {
                return hostEntry.HostName;
            }
        }
        catch (SocketException)
        {
            // Reverse DNS lookup not available — fall through to IP string
        }

        return address.ToString();
    }
}
