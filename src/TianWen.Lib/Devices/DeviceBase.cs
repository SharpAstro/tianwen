using System;
using System.Collections.Immutable;
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

    /// <summary>
    /// Configurable settings for this device, described as URI query parameter descriptors.
    /// The equipment tab iterates these to render a generic settings pane.
    /// Override in subclasses to declare device-specific settings.
    /// </summary>
    [JsonIgnore]
    public virtual ImmutableArray<DeviceSettingDescriptor> Settings => [];

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

    public virtual bool TryInstantiateDriver<TDeviceDriver>(IServiceProvider sp, [NotNullWhen(true)] out TDeviceDriver? driver)
        where TDeviceDriver : IDeviceDriver
    {
        if (NewInstanceFromDevice(sp) is TDeviceDriver asT)
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

    protected virtual IDeviceDriver? NewInstanceFromDevice(IServiceProvider sp) => null;

    public virtual ISerialConnection? ConnectSerialDevice(IExternal external, int baud = 9600, Encoding? encoding = null)
    {
        if (Query.QueryValue(DeviceQueryKey.Port) is { Length: > 0 } port)
        {
            var selectedBaud = int.TryParse(Query.QueryValue(DeviceQueryKey.Baud), CultureInfo.InvariantCulture, out var customBaud) ? customBaud : baud;

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

    /// <summary>
    /// Compares two device URIs by identity (scheme + authority + path), ignoring
    /// query parameters and fragment. Query params carry runtime config (e.g. site
    /// coordinates on a mount URI) that should not affect device identity.
    /// </summary>
    public static bool SameDevice(Uri? a, Uri? b) =>
        a is not null && b is not null
        && a.GetLeftPart(UriPartial.Path) == b.GetLeftPart(UriPartial.Path);

    internal static bool IsValidHost(string host) => Uri.CheckHostName(host) switch
    {
        UriHostNameType.Dns or
        UriHostNameType.IPv4 or
        UriHostNameType.IPv6 => true,
        _ => false
    };
}