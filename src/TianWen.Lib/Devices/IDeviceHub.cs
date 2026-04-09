using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Singleton service that owns device driver instances across the application lifetime.
/// Combines URI→DeviceBase factory resolution (previously <c>IDeviceUriRegistry</c>) with
/// connect/disconnect lifecycle management. Sessions borrow connected drivers from the hub
/// instead of creating their own.
/// </summary>
public interface IDeviceHub : IAsyncDisposable
{
    // ── URI → DeviceBase factory (absorbed from IDeviceUriRegistry) ──

    /// <summary>
    /// Resolves a persisted device URI into a <see cref="DeviceBase"/> instance
    /// using registered keyed factories.
    /// </summary>
    bool TryGetDeviceFromUri(Uri uri, [NotNullWhen(true)] out DeviceBase? device);

    // ── Driver lifecycle ──

    /// <summary>
    /// Creates a driver for <paramref name="device"/>, connects it, and stores
    /// it in the hub. Returns the connected driver.
    /// </summary>
    ValueTask<IDeviceDriver> ConnectAsync(DeviceBase device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects and disposes the driver for the given device URI,
    /// removing it from the hub.
    /// </summary>
    ValueTask DisconnectAsync(Uri deviceUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tries to retrieve a connected driver of type <typeparamref name="T"/>
    /// for the given device URI.
    /// </summary>
    bool TryGetConnectedDriver<T>(Uri deviceUri, [NotNullWhen(true)] out T? driver) where T : class, IDeviceDriver;

    /// <summary>
    /// Returns a snapshot of all currently connected devices and their drivers.
    /// </summary>
    IReadOnlyList<(Uri DeviceUri, IDeviceDriver Driver)> ConnectedDevices { get; }

    /// <summary>
    /// Whether a device with the given URI is currently connected in the hub.
    /// </summary>
    bool IsConnected(Uri deviceUri);

    /// <summary>
    /// Whether a connected camera at the given URI has its cooler actively running
    /// (cooler on and CCD temperature significantly below ambient).
    /// </summary>
    ValueTask<bool> IsCoolingAsync(Uri deviceUri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fired when a device is connected or disconnected via the hub.
    /// </summary>
    event EventHandler<DeviceConnectedEventArgs>? DeviceStateChanged;
}
