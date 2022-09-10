using System;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Web;

namespace Astap.Lib.Devices;

public abstract record class DeviceBase(Uri DeviceUri)
{
    protected const string UriScheme = "device";

    public string DeviceType => HttpUtility.HtmlDecode(DeviceUri.Fragment.TrimStart('#'));

    public string DeviceId => string.Concat(DeviceUri.Segments[1..]);

    private NameValueCollection? _query;
    protected NameValueCollection Query => _query ??= HttpUtility.ParseQueryString(DeviceUri.Query);

    public string DisplayName => Query["displayName"] ?? "";

    public string DeviceClass => DeviceUri.Host;

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
            where constructor is not null
            let obj = constructor.Invoke(new[] { deviceUri }) as DeviceBase
            where obj is not null
            select obj;

        device = findDeviceInSubclass.FirstOrDefault();
        return device is not null;
    }
}