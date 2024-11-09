using System;
using System.Threading;

namespace TianWen.Lib.Devices;

internal abstract class DeviceDriverBase<TDevice, TDeviceInfo>(TDevice device, IExternal external) : IDeviceDriver
    where TDevice : DeviceBase
    where TDeviceInfo : struct
{
    ~DeviceDriverBase()
    {
        Dispose(false);
    }

    public delegate void ProcessDeviceInfoDelegate(in TDeviceInfo deviceInfo);

    protected readonly TDevice _device = device;
    protected TDeviceInfo _deviceInfo;

    private bool disposedValue;

    public virtual string Name => _device.DisplayName;

    public abstract string? DriverInfo { get; }

    public abstract string? Description { get; }

    public virtual string? DriverVersion { get; } = typeof(IDeviceDriver).Assembly.GetName().Version?.ToString() ?? "0.0.1";

    public virtual DeviceType DriverType => _device.DeviceType;

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    public IExternal External { get; } = external;

    private int _connectionId;

    protected int ConnectionId => _connectionId;

    protected void ProcessDeviceInfo(ProcessDeviceInfoDelegate processor) => processor(_deviceInfo);

    internal const int CONNECTION_ID_EXCLUSIVE = -100;
    internal const int CONNECTION_ID_UNKNOWN   = -200;

    const int CONNECTED = 1;
    const int CONNECTING = 2;
    const int DISCONNECTING = 9;
    const int DISCONNECTED = 10;
    const int CONNECTION_FAILURE = 99;

    private int _connectionState = DISCONNECTED;

    public bool Connected => Interlocked.CompareExchange(ref _connectionState, CONNECTED, CONNECTED) == CONNECTED;

    public void Connect() => SetConnectionState(CONNECTED);

    public void Disconnect() => SetConnectionState(DISCONNECTED);

    private void SetConnectionState(int desiredState)
    {
        if (!TrySetConnectionState(desiredState))
        {
            var desiredStateStr = desiredState switch { CONNECTED => "connect", DISCONNECTED => "disconnect", _ => $"reach unknown state {desiredState}" };

            throw new InvalidOperationException($"Failed to {desiredStateStr} to device {_device.DeviceId} ({_device.DisplayName}), current state is {StateToString(Volatile.Read(ref _connectionState))}");
        }
    }

    /// <summary>
    /// Tries to transition device into <paramref name="desiredState"/> (either <see cref="CONNECTED"/> or <see cref="DISCONNECTED"/>) via intermediate states (<see cref="CONNECTING"/>, <see cref="DISCONNECTING"/>).
    /// Will trigger <see cref="OnConnectDevice(out int, out TDeviceInfo)"/> and <see cref="OnDisconnectDevice(int)"/> <em>once</em> respectively.
    /// </summary>
    /// <param name="desiredState"></param>
    /// <returns><see langword="true"/> if desired state has been reached</returns>
    /// <exception cref="InvalidOperationException"></exception>
    private bool TrySetConnectionState(int desiredState)
    {
        var wantsConnect = desiredState is CONNECTED;
        var intermediateState = wantsConnect ? CONNECTING : DISCONNECTING;
        var oppositeState = wantsConnect ? DISCONNECTED : CONNECTED;
        var prevState = Interlocked.CompareExchange(ref _connectionState, intermediateState, oppositeState);

        if (prevState == desiredState)
        {
            return true;
        }

        if (desiredState == CONNECTED)
        {
            if (OnConnectDevice(out var connectionId, out _deviceInfo))
            {
                // only trigger connected event once
                if (Interlocked.CompareExchange(ref _connectionState, desiredState, intermediateState) == intermediateState)
                {
                    Volatile.Write(ref _connectionId, connectionId);
                    DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(wantsConnect));

                    return true;
                }
            }
            else if (Interlocked.CompareExchange(ref _connectionState, CONNECTION_FAILURE, intermediateState) == intermediateState)
            {
                throw new InvalidOperationException($"Could not connect to device {_device.DeviceId}");
            }
        }
        else if (desiredState == DISCONNECTED)
        {
            if (OnDisconnectDevice(Volatile.Read(ref _connectionId)))
            {
                // only trigger disconnect event once
                if (Interlocked.CompareExchange(ref _connectionState, desiredState, intermediateState) == intermediateState)
                {
                    Volatile.Write(ref _connectionId, CONNECTION_ID_UNKNOWN);
                    DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(wantsConnect));

                    return true;
                }
            }
            else if (Interlocked.CompareExchange(ref _connectionState, CONNECTION_FAILURE, intermediateState) == intermediateState)
            {
                throw new InvalidOperationException($"Could not disconnect device {_device.DeviceId}");
            }
        }

        return Interlocked.CompareExchange(ref _connectionState, desiredState, desiredState) != desiredState;
    }

    static string StateToString(int state) => state switch
    {
        CONNECTED => "connected",
        DISCONNECTED => "disconnected",
        CONNECTING => "connecting",
        DISCONNECTING => "disconnecting",
        CONNECTION_FAILURE => "failure",
        _ => $"unknown state {state}"
    };

    protected abstract bool OnConnectDevice(out int connectionId, out TDeviceInfo connectedDeviceInfo);

    protected abstract bool OnDisconnectDevice(int connectionId);

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                Disconnect();
                DeviceConnectedEvent = null;
            }

            DisposeNative();

            disposedValue = true;
        }
    }

    protected virtual void DisposeNative()
    {
        // default empty
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
