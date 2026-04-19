using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices.Discovery;

namespace TianWen.Lib.Devices.Skywatcher;

/// <summary>
/// Discovers Skywatcher mounts. Serial probing lives in <see cref="SkywatcherSerialProbeBase"/>
/// and runs inside <see cref="ISerialProbeService"/>; this source just reads matches the
/// service has already published. WiFi discovery (UDP broadcast of <c>:e1\r</c> to
/// 255.255.255.255:11880) stays here because it uses a different transport.
/// </summary>
internal class SkywatcherDeviceSource(ISerialProbeService probeService, ILogger<SkywatcherDeviceSource> logger) : IDeviceSource<SkywatcherDevice>
{
    private Dictionary<DeviceType, IReadOnlyList<SkywatcherDevice>> _cachedDevices = new();

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount];

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public IEnumerable<SkywatcherDevice> RegisteredDevices(DeviceType deviceType) =>
        _cachedDevices.TryGetValue(deviceType, out var devices) ? devices : [];

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        var mounts = new List<SkywatcherDevice>();

        // Serial matches come from the central probe service (populated before DiscoverAsync
        // runs). Each match's URI already has deviceId + port + baud baked in by the probe.
        foreach (var match in probeService.ResultsFor("Skywatcher"))
        {
            mounts.Add(new SkywatcherDevice(match.DeviceUri));
        }

        // WiFi discovery via UDP broadcast — unchanged, uses a different transport.
        try
        {
            await foreach (var device in DiscoverWiFiAsync(cancellationToken))
            {
                mounts.Add(device);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Skywatcher WiFi discovery failed");
        }

        Interlocked.Exchange(ref _cachedDevices, new Dictionary<DeviceType, IReadOnlyList<SkywatcherDevice>>
        {
            [DeviceType.Mount] = mounts
        });
    }

    private async IAsyncEnumerable<SkywatcherDevice> DiscoverWiFiAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        System.Net.Sockets.UdpClient? udpClient = null;
        try
        {
            udpClient = new System.Net.Sockets.UdpClient();
            udpClient.EnableBroadcast = true;
            udpClient.Client.ReceiveTimeout = 2000;

            var command = SkywatcherProtocol.BuildCommand('e', '1');
            var broadcastEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, SkywatcherProtocol.WIFI_PORT);
            await udpClient.SendAsync(command, broadcastEndPoint, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            while (!cts.Token.IsCancellationRequested)
            {
                System.Net.Sockets.UdpReceiveResult result;
                try
                {
                    result = await udpClient.ReceiveAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    yield break;
                }

                var responseStr = Encoding.ASCII.GetString(result.Buffer);
                // Strip \r terminator if present
                if (responseStr.EndsWith('\r'))
                {
                    responseStr = responseStr[..^1];
                }

                if (SkywatcherProtocol.TryParseResponse(responseStr, out var data) &&
                    SkywatcherProtocol.TryParseFirmwareResponse(data, out var firmware))
                {
                    var host = result.RemoteEndPoint.Address.ToString();
                    var modelName = firmware.MountModel.DisplayName;
                    var deviceId = $"Skywatcher_{modelName.Replace(' ', '_')}_{firmware.VersionString}_{host}";
                    var displayName = $"Skywatcher {modelName} WiFi (FW {firmware.VersionString})";

                    yield return new SkywatcherDevice(DeviceType.Mount, deviceId, displayName, host);
                }
            }
        }
        finally
        {
            udpClient?.Dispose();
        }
    }
}
