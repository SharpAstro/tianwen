using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

/// <summary>
/// Aggregates all <see cref="IDeviceSource{TDevice}"/> implementations and provides
/// a unified discovery and enumeration surface for all device backends.
/// </summary>
public interface IDeviceDiscovery : IAsyncSupportedCheck
{
    /// <summary>
    /// Discovers all devices from all supported backends.
    /// </summary>
    ValueTask DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers only devices of a specified type <paramref name="type"/> asynchronously.
    /// </summary>
    ValueTask DiscoverOnlyDeviceType(DeviceType type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the types of devices that are registered across all supported backends.
    /// </summary>
    IEnumerable<DeviceType> RegisteredDeviceTypes { get; }

    /// <summary>
    /// Gets all registered devices of a specified type across all supported backends.
    /// </summary>
    IEnumerable<DeviceBase> RegisteredDevices(DeviceType deviceType);
}
