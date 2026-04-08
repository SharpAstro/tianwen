using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;

namespace TianWen.Lib.Devices;

internal class DeviceUriRegistry(IServiceProvider serviceProvider) : IDeviceUriRegistry
{
    public bool TryGetDeviceFromUri(Uri uri, [NotNullWhen(true)] out DeviceBase? device)
    {
        // Prefer discovered devices — they carry runtime state (e.g. CanonCameraFactory via DI)
        try
        {
            if (serviceProvider.GetService<ICombinedDeviceManager>() is { } deviceManager)
            {
                var deviceId = string.Concat(uri.Segments[1..]);
                if (deviceManager.TryFindByDeviceId(deviceId, out device))
                {
                    return true;
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            serviceProvider.GetService<IExternal>()?.AppLogger.LogDebug(ex, "Could not resolve ICombinedDeviceManager for device lookup");
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