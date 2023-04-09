using System;
using System.Threading;

namespace Astap.Lib.Devices.ZWO;

public abstract class ZWODeviceDriverBase<TDeviceInfo> : IDeviceDriver
    where TDeviceInfo : struct
{
    public delegate void ProcessDeviceInfoDelegate(in TDeviceInfo deviceInfo);

    protected readonly ZWODevice _device;
    private TDeviceInfo _deviceInfo;

    public ZWODeviceDriverBase(ZWODevice device) => _device = device;

    ~ZWODeviceDriverBase()
    {
        Dispose(false);
    }

    private bool disposedValue;

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;

    protected void ProcessDeviceInfo(ProcessDeviceInfoDelegate processor) => processor(_deviceInfo);

    public virtual string Name => _device.DisplayName;

    public virtual string? DriverInfo => $"ZWO Driver v{DriverVersion}";

    public abstract string? Description { get; }

    public abstract string? DriverVersion { get; }

    public virtual string DriverType => _device.DeviceType;

    private int _connectionId;

    protected int ConnectionId => _connectionId;

    const int STATE_UNKNOWN = 0;
    const int CONNECTED = 1;
    const int DISCONNECTED = 2;
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
            var prevState = Volatile.Read(ref _connectionState);

            if (prevState != desiredState)
            {
                if (desiredState == CONNECTED)
                {
                    if (ConnectDevice(out var connectionId, out _deviceInfo))
                    {
                        Volatile.Write(ref _connectionId, connectionId);
                        Volatile.Write(ref _connectionState, CONNECTED);

                        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(value));
                    }
                }
                else if (desiredState == DISCONNECTED)
                {
                    DisconnectDevice(Volatile.Read(ref _connectionId));
                    Volatile.Write(ref _connectionState, DISCONNECTED);

                    DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(value));
                }
            }
        }
    }

    protected abstract bool ConnectDevice(out int connectionId, out TDeviceInfo deviceInfo);

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

    protected abstract void DisposeNative();

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
