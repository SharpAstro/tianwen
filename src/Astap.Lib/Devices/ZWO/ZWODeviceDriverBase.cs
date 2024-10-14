using System;
using System.Threading;
using ZWOptical.SDK;

namespace Astap.Lib.Devices.ZWO;

public abstract class ZWODeviceDriverBase<TDeviceInfo>(ZWODevice device, IExternal external) : IDeviceDriver
    where TDeviceInfo : struct, IZWODeviceInfo
{
    public delegate void ProcessDeviceInfoDelegate(in TDeviceInfo deviceInfo);

    protected readonly ZWODevice _device = device;
    private TDeviceInfo _deviceInfo;

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

    public virtual DeviceType DriverType => _device.DeviceType;

    public IExternal External { get; } = external;

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

    protected virtual bool ConnectDevice(out int connectionId, out TDeviceInfo connectedDeviceInfo)
    {
        var deviceIterator = new DeviceIterator<TDeviceInfo>();
        var searchId = _device.DeviceId;

        foreach (var (deviceId, deviceInfo) in deviceIterator)
        {
            bool hasOpened = false; 
            try
            {
                hasOpened = deviceInfo.Open();
                if (hasOpened && (IsSameSerialNumber(deviceInfo) || IsSameCustomId(deviceInfo) || IsSameName(deviceInfo)))
                {
                    connectionId = deviceId;
                    connectedDeviceInfo = deviceInfo;

                    return true;
                }
            }
            finally
            {
                if (hasOpened)
                {
                    deviceInfo.Close();
                }
            }
        }

        connectionId = int.MinValue;
        connectedDeviceInfo = default;

        return false;

        bool IsSameSerialNumber(in TDeviceInfo deviceInfo) => deviceInfo.SerialNumber?.ToString() is { Length: > 0 } serialNumber && serialNumber == searchId;

        bool IsSameCustomId(in TDeviceInfo deviceInfo) => deviceInfo.IsUSB3Device && deviceInfo.CustomId is { Length: > 0 } customId && customId == searchId;

        bool IsSameName(in TDeviceInfo deviceInfo) => deviceInfo.Name is { Length: > 0 } name && name == searchId;
    }

    protected virtual bool DisconnectDevice(int connectionId) => _deviceInfo.Close();

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
