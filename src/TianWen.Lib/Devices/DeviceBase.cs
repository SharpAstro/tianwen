using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
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

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, stackalloc char[64], $"{DeviceType} {(string.IsNullOrWhiteSpace(DisplayName) ? DeviceId : DisplayName)}");

    public static bool TryFromUri(Uri deviceUri, [NotNullWhen(true)] out DeviceBase? device)
    {
        device = EnumerateDeviceBase(deviceUri, typeof(DeviceBase).Assembly, Assembly.GetCallingAssembly(), Assembly.GetEntryAssembly()).FirstOrDefault();
        return device is not null;
    }

    /// <summary>
    /// TODO <see cref="Type.GetConstructor(Type[])"/> considered harmful.
    /// </summary>
    /// <param name="deviceUri"></param>
    /// <param name="assemblies"></param>
    /// <returns></returns>
    internal static IEnumerable<DeviceBase> EnumerateDeviceBase(Uri deviceUri, params Assembly?[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var exported in assembly?.GetExportedTypes() ?? [])
            {
                if (exported.Name.Equals(deviceUri.Host, StringComparison.OrdinalIgnoreCase) && exported.IsSubclassOf(typeof(DeviceBase)))
                {
                    var constructor = exported.GetConstructor([typeof(Uri)]);
                    var obj = constructor?.Invoke(new[] { deviceUri }) as DeviceBase;
                    if (obj is not null)
                    {
                        yield return obj;
                    }
                }
            }
        }
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

    public virtual ISerialConnection? ConnectSerialDevice(IExternal external, int baud = 9600, Encoding? encoding = null, TimeSpan? ioTimeout = null)
    {
        if (Query["port"] is { Length: > 0 } port)
        {
            var selectedBaud = int.TryParse(Query["baud"], CultureInfo.InvariantCulture, out var customBaud) ? customBaud : baud;

            if (port.StartsWith(ISerialConnection.SerialProto, StringComparison.Ordinal)
                || port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || port.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[^1].StartsWith("tty", StringComparison.Ordinal)
            )
            {
                return external.OpenSerialDevice(port, selectedBaud, encoding ?? Encoding.ASCII, ioTimeout);
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