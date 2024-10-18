using System;

namespace Astap.Lib.Devices;

public interface IDeviceDriver : IDisposable
{
    internal const int MAX_FAILSAFE = 1_000;

    string Name { get; }

    string? Description { get; }

    string? DriverInfo { get; }

    string? DriverVersion { get; }

    DeviceType DriverType { get; }

    bool Connected { get; set; }

    IExternal External { get; }

    event EventHandler<DeviceConnectedEventArgs>? DeviceConnectedEvent;
}
