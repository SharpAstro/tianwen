using System;

namespace Astap.Lib.Devices.Ascom;

public abstract class AscomDeviceDriverBase : DynamicComObject, IDeviceDriver
{
    private readonly AscomDevice _device;

    public AscomDeviceDriverBase(AscomDevice device) : base(device.DeviceId) => _device = device;

    public string Name => _comObject?.Name as string ?? _device.DisplayName;

    public string? Description => _comObject?.Description as string;

    public string? DriverInfo => _comObject?.DriverInfo as string;

    public string? DriverVersion => _comObject?.DriverVersion as string;

    public string DriverType => _device.DeviceType;

    public void SetupDialog() => _comObject?.SetupDialog();

    private bool? _connectedCache;
    public bool Connected
    {
        get => _connectedCache ??= _comObject?.Connected is bool connected && connected;
        set
        {
            if (_comObject is { } obj)
            {
                obj.Connected = value;
                if (obj.Connected is bool actualConnected)
                {
                    _connectedCache = actualConnected;

                    DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(actualConnected));
                }
                else
                {
                    _connectedCache = null;
                }
            }
            else
            {
                _connectedCache = null;
            }
        }
    }

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;
}