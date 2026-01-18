using System;
using System.Text;

namespace TianWen.Lib.Devices.Guider;

public abstract record class GuiderDeviceBase(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public abstract string? ProfileName { get; }
}