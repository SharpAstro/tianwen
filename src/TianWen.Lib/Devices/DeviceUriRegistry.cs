using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Devices;

internal class DeviceUriRegistry(IServiceProvider serviceProvider) : IDeviceUriRegistry
{
    public bool TryGetDeviceFromUri(Uri uri, [NotNullWhen(true)] out DeviceBase? device)
    {
        // Create device from URI factory — preserves all query params (API keys, settings, etc.)
        var func = serviceProvider.GetKeyedService<Func<Uri, DeviceBase>>(uri.Host.ToLowerInvariant());

        if (func is not null)
        {
            device = func(uri);
            return true;
        }

        device = default;
        return false;
    }
}