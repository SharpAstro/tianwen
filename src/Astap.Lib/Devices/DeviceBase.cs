using System;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Web;

namespace Astap.Lib.Devices;

public abstract record class DeviceBase(Uri DeviceUri)
{
    protected const string UriScheme = "device";

    private DeviceType? _deviceType;
    public DeviceType DeviceType => _deviceType ??= TryParseDeviceType();

    private DeviceType TryParseDeviceType() => DeviceTypeHelper.TryParseDeviceType(HttpUtility.HtmlDecode(DeviceUri.Fragment.TrimStart('#')));

    private string? _deviceId;
    public string DeviceId => _deviceId ??= string.Concat(DeviceUri.Segments[1..]);

    private NameValueCollection? _query;
    protected NameValueCollection Query => _query ??= HttpUtility.ParseQueryString(DeviceUri.Query);

    public string DisplayName => Query["displayName"] ?? "";

    public string DeviceClass => DeviceUri.Host;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, stackalloc char[64], $"{DeviceType} {(string.IsNullOrWhiteSpace(DisplayName) ? DeviceId : DisplayName)}");

    public static bool TryFromUri(Uri deviceUri, [NotNullWhen(true)] out DeviceBase? device)
    {
        if (deviceUri.Scheme != UriScheme)
        {
            device = null;
            return false;
        }

        var findDeviceInSubclass =
            from assembly in new[] { typeof(DeviceBase).Assembly, Assembly.GetCallingAssembly(), Assembly.GetEntryAssembly() }
            where assembly is not null
            from exported in assembly.GetExportedTypes()
            where exported.Name.Equals(deviceUri.Host, StringComparison.OrdinalIgnoreCase) && exported.IsSubclassOf(typeof(DeviceBase))
            let constructor = exported.GetConstructor(new[] { typeof(Uri) })
            let obj = constructor?.Invoke(new[] { deviceUri }) as DeviceBase
            where obj is not null
            select obj;

        device = findDeviceInSubclass.FirstOrDefault();
        return device is not null;
    }

    public virtual bool TryInstantiateDriver<T>([NotNullWhen(true)] out T? driver) where T : IDeviceDriver => TryInstantiate(out driver);

    public virtual bool TryInstantiateDeviceSource<TDevice>([NotNullWhen(true)] out IDeviceSource<TDevice>? deviceSource) where TDevice : DeviceBase
        => TryInstantiate(out deviceSource);

    public virtual bool TryInstantiate<T>([NotNullWhen(true)] out T? driver)
    {
        if (NewImplementationFromDevice() is T asT)
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

    protected abstract object? NewImplementationFromDevice();
}