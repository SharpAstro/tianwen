using System;

namespace Astap.Lib.Devices;

public class DeviceConnectedEventArgs(bool connected) : EventArgs
{
    public bool Connected { get; } = connected;
}
