using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Represents a source of devices that can be discovered and registered.
/// </summary>
/// <typeparam name="TDevice">The type of device.</typeparam>
public interface IDeviceSource<out TDevice> : IAsyncSupportedCheck where TDevice : DeviceBase
{
    /// <summary>
    /// Discovers devices asynchronously, meaningful only after <see cref="IAsyncSupportedCheck.CheckSupportAsync(CancellationToken)"/> has been called.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous discovery operation.</returns>
    ValueTask DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the types of devices that are registered, meaningful only after <see cref="IAsyncSupportedCheck.CheckSupportAsync(CancellationToken)"/> has been called.
    /// </summary>
    IEnumerable<DeviceType> RegisteredDeviceTypes { get; }

    /// <summary>
    /// Gets the registered devices of a specified type. Should only be called after <see cref="DiscoverAsync(CancellationToken)"/> has been called.
    /// </summary>
    /// <param name="deviceType">The type of device.</param>
    /// <returns>A collection of registered devices of the specified type.</returns>
    IEnumerable<TDevice> RegisteredDevices(DeviceType deviceType);
}
