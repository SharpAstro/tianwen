using System;

namespace Astap.Lib.Devices.Builtin;

public record class NoneDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public NoneDevice() : this(new Uri($"none://{typeof(NoneDevice).Name}/None"))
    {

    }

    protected override object? NewImplementationFromDevice() => null;
}