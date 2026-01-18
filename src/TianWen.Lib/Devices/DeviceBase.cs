using System;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Web;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices;

public abstract record class DeviceBase(Uri DeviceUri)
{
    private DeviceType? _deviceType;

    [JsonIgnore]
    public DeviceType DeviceType => _deviceType ??= TryParseDeviceType();

    private DeviceType TryParseDeviceType() => DeviceTypeHelper.TryParseDeviceType(DeviceUri.Scheme);

    private string? _deviceId;

    [JsonIgnore]
    public string DeviceId => _deviceId ??= string.Concat(DeviceUri.Segments[1..]);

    private NameValueCollection? _query;
    [JsonIgnore]
    public NameValueCollection Query => _query ??= HttpUtility.ParseQueryString(DeviceUri.Query);

    [JsonIgnore]
    public string DisplayName => HttpUtility.UrlDecode(DeviceUri.Fragment.TrimStart('#'));

    [JsonIgnore]
    public string DeviceClass => DeviceUri.Host;

    protected virtual string? CustomToString() => null;

    public sealed override string ToString()
    {
        if (CustomToString() is { } custom)
        {
            return custom;
        }

        var stringBuilder = new StringBuilder();
        var deviceTypeName = DeviceType.PascalCaseStringToName();

        stringBuilder.AppendFormat("[{0} {1}]", 
            GetType().Name.Replace(deviceTypeName, "").PascalCaseStringToName().Replace(" Device", ""),
            deviceTypeName
        );

        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            stringBuilder.AppendFormat(" {0}", DisplayName);
        }

        stringBuilder.AppendFormat(" ({0})", DeviceId);

        return stringBuilder.ToString();
    }

    public virtual bool TryInstantiateDriver<TDeviceDriver>(IExternal external, [NotNullWhen(true)] out TDeviceDriver? driver)
        where TDeviceDriver : IDeviceDriver
    {
        if (NewInstanceFromDevice(external) is TDeviceDriver asT)
        {
            driver = asT;
            return true;
        }
        else
        {
            driver = default;
            return false;
        }
    }

    protected virtual IDeviceDriver? NewInstanceFromDevice(IExternal external) => null;

    public virtual ISerialConnection? ConnectSerialDevice(IExternal external, int baud = 9600, Encoding? encoding = null)
    {
        if (Query["port"] is { Length: > 0 } port)
        {
            var selectedBaud = int.TryParse(Query["baud"], CultureInfo.InvariantCulture, out var customBaud) ? customBaud : baud;

            if (port.StartsWith(ISerialConnection.SerialProto, StringComparison.Ordinal)
                || port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || port.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[^1].StartsWith("tty", StringComparison.Ordinal)
            )
            {
                return external.OpenSerialDevice(port, selectedBaud, encoding ?? Encoding.ASCII);
            }
        }

        return null;
    }

    internal static bool IsValidHost(string host) => Uri.CheckHostName(host) switch
    {
        UriHostNameType.Dns or
        UriHostNameType.IPv4 or
        UriHostNameType.IPv6 => true,
        _ => false
    };
}