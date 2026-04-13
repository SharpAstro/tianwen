using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Skywatcher;

/// <summary>
/// Discovers Skywatcher mounts via serial port enumeration (115200/9600 baud probe)
/// and WiFi UDP broadcast (:e1\r to 255.255.255.255:11880).
/// </summary>
internal class SkywatcherDeviceSource(IExternal external, ILogger<SkywatcherDeviceSource> logger, ITimeProvider timeProvider) : IDeviceSource<SkywatcherDevice>
{
    private Dictionary<DeviceType, IReadOnlyList<SkywatcherDevice>> _cachedDevices = new();

    public IEnumerable<DeviceType> RegisteredDeviceTypes => [DeviceType.Mount];

    public ValueTask<bool> CheckSupportAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public IEnumerable<SkywatcherDevice> RegisteredDevices(DeviceType deviceType) =>
        _cachedDevices.TryGetValue(deviceType, out var devices) ? devices : [];

    public async ValueTask DiscoverAsync(CancellationToken cancellationToken)
    {
        var mounts = new List<SkywatcherDevice>();

        // Serial port discovery
        try
        {
            using var resourceLock = await external.WaitForSerialPortEnumerationAsync(cancellationToken);
            foreach (var portName in external.EnumerateAvailableSerialPorts(resourceLock))
            {
                var device = await QuerySerialPortAsync(portName, cancellationToken);
                if (device is not null)
                {
                    mounts.Add(device);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enumerate serial ports for Skywatcher discovery");
        }

        // WiFi discovery via UDP broadcast
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

    private async Task<SkywatcherDevice?> QuerySerialPortAsync(string portName, CancellationToken cancellationToken)
    {
        // Try 115200 baud first (USB-integrated mounts like EQ6-R), then 9600 (legacy serial adapter)
        foreach (var baud in new[] { SkywatcherProtocol.DEFAULT_USB_BAUD, SkywatcherProtocol.DEFAULT_LEGACY_BAUD })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMilliseconds(300));

                using var port = external.OpenSerialDevice(portName, baud, Encoding.ASCII);
                if (port is not { IsOpen: true })
                {
                    continue;
                }

                using var @lock = await port.WaitAsync(cts.Token);

                // Send firmware query for RA axis
                var command = SkywatcherProtocol.BuildCommand('e', '1');
                if (!await port.TryWriteAsync(command, cts.Token))
                {
                    continue;
                }

                var response = await port.TryReadTerminatedAsync("\r"u8.ToArray(), cts.Token);
                if (response is null || !SkywatcherProtocol.TryParseResponse(response, out var data))
                {
                    continue;
                }

                if (!SkywatcherProtocol.TryParseFirmwareResponse(data, out var firmware))
                {
                    continue;
                }

                var modelName = firmware.MountModel.DisplayName;
                var deviceId = $"Skywatcher_{modelName.Replace(' ', '_')}_{firmware.VersionString}_{ISerialConnection.CleanupPortName(portName)}";
                var displayName = $"Skywatcher {modelName} (FW {firmware.VersionString})";

                port.TryClose();
                return new SkywatcherDevice(DeviceType.Mount, deviceId, displayName, portName, baud);
            }
            catch (OperationCanceledException)
            {
                // Timeout — try next baud rate
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skywatcher probe failed on {Port} at {Baud} baud", portName, baud);
            }
        }

        return null;
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
