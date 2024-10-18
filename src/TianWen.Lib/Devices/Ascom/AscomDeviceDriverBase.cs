using System;
using System.Diagnostics;
using System.Threading;

namespace TianWen.Lib.Devices.Ascom;

public abstract class AscomDeviceDriverBase(AscomDevice device, IExternal external) : DynamicComObject(device.DeviceId), IDeviceDriver
{
    private readonly AscomDevice _device = device;

    public string Name => _comObject?.Name as string ?? _device.DisplayName;

    public string? Description => _comObject?.Description as string;

    public string? DriverInfo => _comObject?.DriverInfo as string;

    public string? DriverVersion => _comObject?.DriverVersion as string;

    public DeviceType DriverType => _device.DeviceType;

    public IExternal External { get; } = external;

    [DebuggerHidden]
    public void SetupDialog() => _comObject?.SetupDialog();

    const int STATE_UNKNOWN = 0;
    const int CONNECTED = 1;
    const int DISCONNECTED = 2;
    private int _connectionState = STATE_UNKNOWN;
    public bool Connected
    {
        get
        {
            var state = Volatile.Read(ref _connectionState);
            switch (state)
            {
                case STATE_UNKNOWN:
                    if (_comObject?.Connected is bool connected)
                    {
                        Volatile.Write(ref _connectionState, connected ? CONNECTED : DISCONNECTED);
                        return connected;
                    }
                    return false;

                case CONNECTED:
                    return true;

                case DISCONNECTED:
                default:
                    return false;
            }
        }

        set
        {
            if (_comObject is { } obj)
            {
                if (obj.Connected is bool currentConnected)
                {
                    var actualState = currentConnected ? CONNECTED : DISCONNECTED;
                    var desiredState = value ? CONNECTED : DISCONNECTED;
                    var prevState = Volatile.Read(ref _connectionState);
                    if (prevState != actualState || actualState != desiredState)
                    {
                        Volatile.Write(ref _connectionState, desiredState);

                        DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(obj.Connected = value));
                    }
                }
                else
                {
                    Volatile.Write(ref _connectionState, STATE_UNKNOWN);
                }
            }
            else
            {
                Volatile.Write(ref _connectionState, STATE_UNKNOWN);
            }
        }
    }

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;
}