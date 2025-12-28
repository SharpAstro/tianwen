using System;
using System.Threading;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

public interface IDeviceDriver : IAsyncDisposable, IDisposable
{
    internal const int MAX_FAILSAFE = 1_000;

    string Name { get; }

    string? Description { get; }

    string? DriverInfo { get; }

    string? DriverVersion { get; }

    DeviceType DriverType { get; }

    IExternal External { get; }

    bool Connected { get; }

    /// <summary>
    /// Connects to the device asynchronously.
    /// Will throw if <see cref="CanAsyncConnect"/> is <see langword="false"/>.
    /// Should return immidiately if already <see cref="Connected"/>.
    /// </summary>
    /// <returns>Awaitable task, if completed, device is connected</returns>
    /// <exception cref="InvalidOperationException">Thrown if async connection is not supported</exception>
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);


    /// <summary>
    /// Disconnects to the device asynchronously.
    /// </summary>
    /// <returns>Awaitable task, if completed, device is dis-connected</returns>
    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;
}
