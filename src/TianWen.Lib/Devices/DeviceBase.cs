using Microsoft.Extensions.Logging;
using System;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TianWen.Lib.Connections;

namespace TianWen.Lib.Devices;

public abstract record class DeviceBase(Uri DeviceUri)
{
    private DeviceType? _deviceType;

    [JsonIgnore]
    public DeviceType DeviceType => _deviceType ??= DeviceTypeOf(DeviceUri);

    private string? _deviceId;

    [JsonIgnore]
    public string DeviceId => _deviceId ??= DeviceIdOf(DeviceUri);

    private NameValueCollection? _query;
    [JsonIgnore]
    public NameValueCollection Query => _query ??= HttpUtility.ParseQueryString(DeviceUri.Query);

    [JsonIgnore]
    public string DisplayName => DisplayNameOf(DeviceUri);

    [JsonIgnore]
    public string DeviceClass => DeviceClassOf(DeviceUri);

    // The ONE reading of a device URI. The instance properties above call these, and so does
    // anything that has a URI but no device (Profile.Detailed describing an undiscovered device),
    // so the two can never disagree about what a URI says.

    /// <summary>The device type is the URI scheme.</summary>
    internal static DeviceType DeviceTypeOf(Uri deviceUri) => DeviceTypeHelper.TryParseDeviceType(deviceUri.Scheme);

    /// <summary>The device id is the path segments after the leading slash, joined.</summary>
    internal static string DeviceIdOf(Uri deviceUri) => string.Concat(deviceUri.Segments[1..]);

    /// <summary>The display name is the URL-decoded fragment.</summary>
    internal static string DisplayNameOf(Uri deviceUri) => HttpUtility.UrlDecode(deviceUri.Fragment.TrimStart('#'));

    /// <summary>The device class is the URI host.</summary>
    internal static string DeviceClassOf(Uri deviceUri) => deviceUri.Host;

    /// <summary>
    /// Short vendor / transport moniker shown in the equipment device list
    /// (e.g. "ASCOM", "ZWO", "Canon", "OnStep"). Default strips the conventional
    /// "Device" suffix from the type name; override in subclasses where case
    /// or aliasing matters ("Ascom" -> "ASCOM", "IOptron" -> "iOptron", etc.).
    /// </summary>
    [JsonIgnore]
    public virtual string Source
    {
        get
        {
            var typeName = GetType().Name;
            return typeName switch
            {
                "AscomDevice" => "ASCOM",
                "IOptronDevice" => "iOptron",
                "OpenPHD2GuiderDevice" => "PHD2",
                "BuiltInGuiderDevice" => "Built-in",
                "ManualFilterWheelDevice" => "Manual",
                "ManualCoverDevice" => "Manual",
                "OpenMeteoDevice" => "Open-Meteo",
                "OpenWeatherMapDevice" => "OpenWeather",
                "NoneDevice" => "",
                _ => typeName.EndsWith("Device", StringComparison.Ordinal)
                    ? typeName[..^"Device".Length]
                    : typeName,
            };
        }
    }

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

    public virtual async ValueTask<ISerialConnection?> ConnectSerialDeviceAsync(IExternal external, ILogger logger, ITimeProvider timeProvider, int baud = 9600, Encoding? encoding = null, CancellationToken cancellationToken = default)
    {
        if (Query.QueryValue(DeviceQueryKey.Port) is { Length: > 0 } port)
        {
            var selectedBaud = int.TryParse(Query.QueryValue(DeviceQueryKey.Baud), CultureInfo.InvariantCulture, out var customBaud) ? customBaud : baud;

            if (port.StartsWith(ISerialConnection.SerialProto, StringComparison.Ordinal)
                || port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || port.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[^1].StartsWith("tty", StringComparison.Ordinal)
            )
            {
                return await external.OpenSerialDeviceAsync(port, selectedBaud, encoding ?? Encoding.ASCII, cancellationToken: cancellationToken);
            }
        }

        return null;
    }

    /// <summary>
    /// Compares two device URIs by identity (scheme + authority + path), ignoring
    /// query parameters and fragment. Query params carry runtime config (e.g. site
    /// coordinates on a mount URI) that should not affect device identity.
    /// </summary>
    /// <remarks>
    /// Both <see cref="NotNullWhenAttribute"/>s state what the body decides: a true answer means
    /// neither side was null, which is what lets a caller read the URI it just compared without a
    /// null-forgiving <c>!</c>.
    /// </remarks>
    public static bool SameDevice([NotNullWhen(true)] Uri? a, [NotNullWhen(true)] Uri? b) =>
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