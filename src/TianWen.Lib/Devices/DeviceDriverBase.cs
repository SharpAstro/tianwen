using Microsoft.Extensions.Logging;
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

    const int STATE_UNKNOWN = 0;
    const int CONNECTED = 1;
    const int CONNECTING = 2;
    const int DISCONNECTING = 9;
    const int DISCONNECTED = 10;
    const int CONNECTION_FAILURE = 99;

    private int _connectionState = STATE_UNKNOWN;

    public bool Connected
    {
        get
        {
            var state = Volatile.Read(ref _connectionState);
            return state switch
            {
                CONNECTED => true,
                _ => false,
            };
        }

        set
        {
            var desiredState = value ? CONNECTED : DISCONNECTED;

            try
            {
                if (!SetConnectionState(desiredState))
                {
                    External.AppLogger.LogError("Failed to {DesiredState} to device {DeviceId} ({DeviceDisplayName}), current state is {CurrentState}",
                        desiredState switch { CONNECTED => "connect", DISCONNECTED => "disconnect", _ => $"reach unknown state {desiredState}" },
                        _device.DeviceId,
                        _device.DisplayName,
                        StateToString(Volatile.Read(ref _connectionState))
                    );
                }
            }
            catch (Exception ex)
            {
                External.AppLogger.LogError(ex, "Failed to {DesiredState} to device {DeviceId} ({DeviceDisplayName}), current state is {CurrentState}",
                    desiredState switch { CONNECTED => "connect", DISCONNECTED => "disconnect", _ => $"reach unknown state {desiredState}" },
                    _device.DeviceId,
                    _device.DisplayName,
                    StateToString(Volatile.Read(ref _connectionState))
                );
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
        }
    }

    private bool SetConnectionState(int desiredState)
    {
        var wantsConnect = desiredState is CONNECTED;
        var intermediateState = wantsConnect ? CONNECTING : DISCONNECTING;
        var oppositeState = wantsConnect ? DISCONNECTED : CONNECTED;
        var prevState = Interlocked.CompareExchange(ref _connectionState, intermediateState, oppositeState);

        if (prevState != desiredState)
        {
            if (desiredState == CONNECTED)
            {
                if (ConnectDevice(out var connectionId, out _deviceInfo))
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
                if (DisconnectDevice(Volatile.Read(ref _connectionId)))
                {
                    // only trigger disconnect event once
                    if (Interlocked.CompareExchange(ref _connectionState, desiredState, intermediateState) == intermediateState)
                    {
                        Volatile.Write(ref _connectionId, CONNECTION_ID_UNKNOWN);
                        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(wantsConnect));

                        return false;
                    }
                }
                else if (Interlocked.CompareExchange(ref _connectionState, CONNECTION_FAILURE, intermediateState) == intermediateState)
                {
                    throw new InvalidOperationException($"Could not disconnect device {_device.DeviceId}");
                }
            }

            return Volatile.Read(ref _connectionState) != desiredState;
        }

        return true;
    }

    protected abstract bool ConnectDevice(out int connectionId, out TDeviceInfo connectedDeviceInfo);

    protected abstract bool DisconnectDevice(int connectionId);

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                DeviceConnectedEvent = null;
                Connected = false;
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
