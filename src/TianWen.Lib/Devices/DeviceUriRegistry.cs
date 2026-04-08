using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Devices;

internal class DeviceUriRegistry(IServiceProvider serviceProvider, ICombinedDeviceManager deviceManager) : IDeviceUriRegistry
{
    public bool TryGetDeviceFromUri(Uri uri, [NotNullWhen(true)] out DeviceBase? device)
    {
        // Prefer discovered devices — they carry runtime state (e.g. CameraFactory)
        var deviceId = string.Concat(uri.Segments[1..]);
        if (deviceManager.TryFindByDeviceId(deviceId, out device))
        {
            return true;
        }

        // Fall back to creating a new device from the URI factory
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