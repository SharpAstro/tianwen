using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Web;

namespace TianWen.Lib.Devices;

public abstract record class DeviceBase(Uri DeviceUri)
{
    private DeviceType? _deviceType;
    public DeviceType DeviceType => _deviceType ??= TryParseDeviceType();

    private DeviceType TryParseDeviceType() => DeviceTypeHelper.TryParseDeviceType(DeviceUri.Scheme);

    private string? _deviceId;
    public string DeviceId => _deviceId ??= string.Concat(DeviceUri.Segments[1..]);

    private NameValueCollection? _query;
    [JsonIgnore]
    public NameValueCollection Query => _query ??= HttpUtility.ParseQueryString(DeviceUri.Query);

    public string DisplayName => HttpUtility.UrlDecode(DeviceUri.Fragment.TrimStart('#'));

    public string DeviceClass => DeviceUri.Host;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, stackalloc char[64], $"{DeviceType} {(string.IsNullOrWhiteSpace(DisplayName) ? DeviceId : DisplayName)}");

    public static bool TryFromUri(Uri deviceUri, [NotNullWhen(true)] out DeviceBase? device)
    {
        device = EnumerateDeviceBase(deviceUri, typeof(DeviceBase).Assembly, Assembly.GetCallingAssembly(), Assembly.GetEntryAssembly()).FirstOrDefault();
        return device is not null;
    }

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
}