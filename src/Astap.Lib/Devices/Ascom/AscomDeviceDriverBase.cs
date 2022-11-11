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

    public bool Connected
    {
        get => _comObject?.Connected is bool connected && connected;
        set
        {
            var obj = _comObject;
            if (obj is not null)
            {
                obj.Connected = value;

                DeviceConnectedEvent?.Invoke(this, new DeviceConnectedEventArgs(Connected));
            }
        }
    }

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;
}