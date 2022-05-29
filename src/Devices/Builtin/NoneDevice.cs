using System;

namespace Astap.Lib.Devices.Builtin;

public record class NoneDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public NoneDevice() : this(new Uri($"device://{typeof(NoneDevice).Name}/None#None"))
    {

    }
}