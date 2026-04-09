using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

internal class DeviceHub(IServiceProvider serviceProvider, ILogger<DeviceHub> logger) : IDeviceHub
{
    private readonly ConcurrentDictionary<string, (DeviceBase Device, IDeviceDriver Driver)> _connected = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<DeviceConnectedEventArgs>? DeviceStateChanged;

    // ── URI → DeviceBase factory (absorbed from DeviceUriRegistry) ──

    public bool TryGetDeviceFromUri(Uri uri, [NotNullWhen(true)] out DeviceBase? device)
    {
        var func = serviceProvider.GetKeyedService<Func<Uri, DeviceBase>>(uri.Host.ToLowerInvariant());

        if (func is not null)
        {
            device = func(uri);
            return true;
        }

        device = default;
        return false;
    }

    // ── Driver lifecycle ──

    public async ValueTask<IDeviceDriver> ConnectAsync(DeviceBase device, CancellationToken cancellationToken = default)
    {
        var key = DeviceKey(device.DeviceUri);

        if (_connected.TryGetValue(key, out var existing) && existing.Driver.Connected)
        {
            return existing.Driver;
        }

        if (!device.TryInstantiateDriver<IDeviceDriver>(serviceProvider, out var driver))
        {
            throw new InvalidOperationException($"Could not instantiate driver for device {device.DisplayName} ({device.DeviceType})");
        }

        try
        {
            await driver.ConnectAsync(cancellationToken);
        }
        catch
        {
            await driver.DisposeAsync();
            throw;
        }

        _connected[key] = (device, driver);

        logger.LogInformation("DeviceHub: connected {DeviceType} {DisplayName}", device.DeviceType, device.DisplayName);
        DeviceStateChanged?.Invoke(this, new DeviceConnectedEventArgs(connected: true));

        return driver;
    }

    public async ValueTask DisconnectAsync(Uri deviceUri, CancellationToken cancellationToken = default)
    {
        var key = DeviceKey(deviceUri);

        if (!_connected.TryRemove(key, out var entry))
        {
            return;
        }

        try
        {
            if (entry.Driver.Connected)
            {
                await entry.Driver.DisconnectAsync(cancellationToken);
            }
        }
        finally
        {
            await entry.Driver.DisposeAsync();
        }

        logger.LogInformation("DeviceHub: disconnected {DeviceType} {DisplayName}", entry.Device.DeviceType, entry.Device.DisplayName);
        DeviceStateChanged?.Invoke(this, new DeviceConnectedEventArgs(connected: false));
    }

    public bool TryGetConnectedDriver<T>(Uri deviceUri, [NotNullWhen(true)] out T? driver) where T : class, IDeviceDriver
    {
        var key = DeviceKey(deviceUri);

        if (_connected.TryGetValue(key, out var entry) && entry.Driver is T typed && entry.Driver.Connected)
        {
            driver = typed;
            return true;
        }

        driver = default;
        return false;
    }

    public IReadOnlyList<(Uri DeviceUri, IDeviceDriver Driver)> ConnectedDevices =>
        _connected.Values.Select(e => (e.Device.DeviceUri, e.Driver)).ToList();

    public bool IsConnected(Uri deviceUri) =>
        _connected.TryGetValue(DeviceKey(deviceUri), out var entry) && entry.Driver.Connected;

    public async ValueTask<bool> IsCoolingAsync(Uri deviceUri, CancellationToken cancellationToken = default)
    {
        if (!_connected.TryGetValue(DeviceKey(deviceUri), out var entry))
        {
            return false;
        }

        if (entry.Driver is not ICameraDriver camera || !camera.CanGetCoolerOn)
        {
            return false;
        }

        return await camera.GetCoolerOnAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, (device, _)) in _connected.ToArray())
        {
            try
            {
                await DisconnectAsync(device.DeviceUri, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DeviceHub: error during shutdown disconnect for {Device}", device.DisplayName);
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Device identity key — scheme + authority + path, ignoring query/fragment.
    /// Matches <see cref="DeviceBase.SameDevice"/>.
    /// </summary>
    private static string DeviceKey(Uri uri) => uri.GetLeftPart(UriPartial.Path);
}
