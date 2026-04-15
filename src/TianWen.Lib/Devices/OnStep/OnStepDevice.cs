using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices.OnStep;

/// <summary>
/// OnStep / OnStepX mount device addressed by URI. Two transports:
/// <list type="bullet">
///   <item><description><c>?port=COMx</c> — RS-232 / USB-to-serial @ 9600 baud</description></item>
///   <item><description><c>?host=192.168.1.42&amp;tcp=9999</c> — WiFi / Ethernet (ESP32 SmartHand Controller default port 9999)</description></item>
/// </list>
/// </summary>
public record OnStepDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    /// <summary>Default OnStep WiFi/Ethernet TCP port (ESP32 SmartHand Controller standard).</summary>
    public const int DefaultTcpPort = 9999;

    /// <summary>Constructor for serial-port OnStep mounts.</summary>
    public OnStepDevice(DeviceType deviceType, string deviceId, string displayName, string port)
        : this(new Uri($"{deviceType}://{typeof(OnStepDevice).Name}/{deviceId}?{new NameValueCollection { ["port"] = port }.ToQueryString()}#{displayName}"))
    {
    }

    /// <summary>Constructor for WiFi/Ethernet OnStep mounts (TCP transport).</summary>
    public OnStepDevice(DeviceType deviceType, string deviceId, string displayName, string host, int tcpPort)
        : this(new Uri($"{deviceType}://{typeof(OnStepDevice).Name}/{deviceId}?{new NameValueCollection { ["host"] = host, ["tcp"] = tcpPort.ToString(CultureInfo.InvariantCulture) }.ToQueryString()}#{displayName}"))
    {
    }

    /// <summary>
    /// Equipment-tab settings surface: lets the user flip between serial and WiFi in place.
    /// Setting <c>Host</c> switches transport to TCP on next connect (see <see cref="ConnectSerialDevice"/>);
    /// clearing <c>Host</c> reverts to the serial <c>Port</c>. The irrelevant mode's fields hide
    /// automatically via <c>isVisible</c> so the user only sees one transport at a time.
    /// </summary>
    public override ImmutableArray<DeviceSettingDescriptor> Settings { get; } =
    [
        // Serial transport: shown only when no WiFi host is configured.
        DeviceSettingHelper.StringSetting(
            DeviceQueryKey.Port.Key, "Serial Port",
            placeholder: "e.g. COM3 or /dev/ttyUSB0",
            isVisible: uri => string.IsNullOrEmpty(uri.QueryValue(DeviceQueryKey.Host))),

        // WiFi Host — always visible; populating it is how the user flips to TCP transport.
        DeviceSettingHelper.StringSetting(
            DeviceQueryKey.Host.Key, "WiFi Host / IP",
            placeholder: "e.g. 192.168.1.42 (switches to WiFi)"),

        // TCP port — only meaningful when Host is set; defaults to 9999 on connect if blank.
        DeviceSettingHelper.StringSetting(
            "tcp", "WiFi TCP Port",
            defaultValue: DefaultTcpPort.ToString(CultureInfo.InvariantCulture),
            placeholder: DefaultTcpPort.ToString(CultureInfo.InvariantCulture),
            isVisible: uri => !string.IsNullOrEmpty(uri.QueryValue(DeviceQueryKey.Host))),
    ];

    protected override IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => DeviceType switch
    {
        DeviceType.Mount => new OnStepMountDriver<OnStepDevice>(this, sp),
        _ => null
    };

    /// <summary>
    /// Picks the transport based on URI shape: <c>host</c> ⇒ TCP, otherwise serial.
    /// Fully async — no thread pool thread blocks during the TCP 3-way handshake
    /// or the serial port open.
    /// </summary>
    public override async ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(IExternal external, ILogger logger, ITimeProvider timeProvider, int baud = 9600, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        var host = Query.QueryValue(DeviceQueryKey.Host);
        if (host is { Length: > 0 })
        {
            var port = int.TryParse(Query["tcp"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : DefaultTcpPort;
            return await TcpSerialConnection.CreateAsync(host, port, encoding ?? Encoding.Latin1, logger, cancellationToken);
        }

        // Serial path: defer to the base, which reads ?port= and ?baud= from the URI.
        return await base.ConnectSerialDeviceAsync(external, logger, timeProvider, baud, encoding, cancellationToken);
    }
}
