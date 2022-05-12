using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Web;

namespace Astap.Lib.Devices;

public abstract record class DeviceBase(Uri DeviceUri)
{
    public string DeviceType => HttpUtility.HtmlDecode(DeviceUri.Fragment.TrimStart('#'));

    public string DeviceId => string.Join('/', DeviceUri.Segments[1..]);

    public string DisplayName => HttpUtility.ParseQueryString(DeviceUri.Query)["displayName"] ?? "";

    public string DeviceClass => DeviceUri.Host;

    public static bool TryFromUri(Uri deviceUri, [NotNullWhen(true)] out DeviceBase? device)
    {
        if (deviceUri.Scheme == "device")
        {
            foreach (var assembly in new[] { typeof(DeviceBase).Assembly, Assembly.GetCallingAssembly(), Assembly.GetEntryAssembly() })
            {
                if (assembly is null)
                {
                    continue;
                }
                foreach (var exported in assembly.GetExportedTypes())
                {
                    if (string.Equals(exported.Name, deviceUri.Host, StringComparison.OrdinalIgnoreCase)
                        && exported.IsSubclassOf(typeof(DeviceBase))
                        && exported.GetConstructor(new[] { typeof(Uri) }) is ConstructorInfo uriConstructor
                        && uriConstructor.Invoke(new[] { deviceUri }) is DeviceBase newDevice)
                    {
                        device = newDevice;
                        return true;
                    }
                }
            }
        }

        device = null;
        return false;
    }
}