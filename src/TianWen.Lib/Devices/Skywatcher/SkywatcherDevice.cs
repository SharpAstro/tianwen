using Microsoft.Extensions.Logging;
using System;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using TianWen.Lib;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Skywatcher;

/// <summary>
/// Skywatcher mount device addressed by URI. The <c>baud</c> query parameter selects the serial speed:
/// <c>9600</c> = legacy mounts via external serial adapter, <c>115200</c> = integrated USB (e.g. EQ6-R, AzEQ6).
/// Discovery probes 115200 first. WiFi mounts (port = IP address) ignore baud entirely.
/// </summary>
public record SkywatcherDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public SkywatcherDevice(DeviceType deviceType, string deviceId, string displayName, string port, int baud = SkywatcherProtocol.DEFAULT_LEGACY_BAUD)
        : this(new Uri($"{deviceType}://{typeof(SkywatcherDevice).Name}/{deviceId}?{new NameValueCollection { ["port"] = port, ["baud"] = baud.ToString() }.ToQueryString()}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Mount => new SkywatcherMountDriver(this, sp),
        _ => null
    };

    public override ISerialConnection? ConnectSerialDevice(IExternal external, int baud = SkywatcherProtocol.DEFAULT_LEGACY_BAUD, Encoding? encoding = null, ILogger? logger = null, ITimeProvider? timeProvider = null)
    {
        var port = Query.QueryValue(DeviceQueryKey.Port);
        if (port is null)
        {
            return null;
        }

        // WiFi transport: port is an IPv4 address
        if (IPAddress.TryParse(port, out _))
        {
            return new SkywatcherUdpConnection(port, SkywatcherProtocol.WIFI_PORT, encoding ?? Encoding.ASCII, logger);
        }

        // Parse baud from URI query, defaulting to the provided baud parameter
        if (int.TryParse(Query.QueryValue(DeviceQueryKey.Baud), out var uriBaud))
        {
            baud = uriBaud;
        }

        return external.OpenSerialDevice(port, baud, encoding ?? Encoding.ASCII);
    }
}
