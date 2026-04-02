using System;
using System.Collections.Specialized;
using System.Text;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.Skywatcher;

public record SkywatcherDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public SkywatcherDevice(DeviceType deviceType, string deviceId, string displayName, string port, int baud = SkywatcherProtocol.DEFAULT_LEGACY_BAUD)
        : this(new Uri($"{deviceType}://{typeof(SkywatcherDevice).Name}/{deviceId}?{new NameValueCollection { ["port"] = port, ["baud"] = baud.ToString() }.ToQueryString()}#{displayName}"))
    {
        // calls primary constructor
    }

    protected override IDeviceDriver? NewInstanceFromDevice(IExternal external) => DeviceType switch
    {
        DeviceType.Mount => new SkywatcherMountDriver(this, external),
        _ => null
    };

    public override ISerialConnection? ConnectSerialDevice(IExternal external, int baud = SkywatcherProtocol.DEFAULT_LEGACY_BAUD, Encoding? encoding = null)
    {
        var port = Query.QueryValue(DeviceQueryKey.Port);
        if (port is null)
        {
            return null;
        }

        // WiFi transport: port starts with a host address (contains ':' or is an IP)
        if (port.Contains(':') || (port.Split('.') is { Length: 4 }))
        {
            return new SkywatcherUdpConnection(port, SkywatcherProtocol.WIFI_PORT, encoding ?? Encoding.ASCII, external.AppLogger);
        }

        // Parse baud from URI query, defaulting to the provided baud parameter
        if (int.TryParse(Query.QueryValue(DeviceQueryKey.Baud), out var uriBaud))
        {
            baud = uriBaud;
        }

        return external.OpenSerialDevice(port, baud, encoding ?? Encoding.ASCII);
    }
}
