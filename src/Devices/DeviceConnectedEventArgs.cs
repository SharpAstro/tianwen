using System;

namespace Astap.Lib.Devices;

public class DeviceConnectedEventArgs : EventArgs
{
    public DeviceConnectedEventArgs(bool connected)
    {
        Connected = connected;
    }

    public bool Connected { get; }
}
