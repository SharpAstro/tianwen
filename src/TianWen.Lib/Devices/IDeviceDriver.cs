using System;
using System.Threading.Tasks;

namespace TianWen.Lib.Devices;

public interface IDeviceDriver : IDisposable
{
    internal const int MAX_FAILSAFE = 1_000;

    string Name { get; }

    string? Description { get; }

    string? DriverInfo { get; }

    string? DriverVersion { get; }

    DeviceType DriverType { get; }

    bool Connected { get; }

    bool CanAsyncConnect => false;

    void Connect();

    void Disconnect();

    /// <summary>
    /// Connects to the device asynchronously.
    /// Will throw if <see cref="CanAsyncConnect"/> is <see langword="false"/>.
    /// </summary>
    /// <returns>Awaitable task, if completed, device is connected</returns>
    /// <exception cref="InvalidOperationException">Thrown if async connection is not supported</exception>
    ValueTask ConnectAsync() => throw new InvalidOperationException("Async connect is not supported by this device");


    /// <summary>
    /// Disconnects to the device asynchronously.
    /// Will throw if <see cref="CanAsyncConnect"/> is <see langword="false"/>.
    /// </summary>
    /// <returns>Awaitable task, if completed, device is dis-connected</returns>
    /// <exception cref="InvalidOperationException">Thrown if async connection is not supported</exception>
    ValueTask DisconnectAsync() => throw new InvalidOperationException("Async dis-connect is not supported by this device");

    IExternal External { get; }

    event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;
}
