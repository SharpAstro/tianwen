using System;

namespace Astap.Lib.Devices;

/// <summary>
/// Build-in device type: <see cref="DeviceType.None"/>.
/// </summary>
/// <param name="DeviceUri"></param>
public record class NoneDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public NoneDevice() : this(new Uri($"none://{typeof(NoneDevice).Name}/None"))
    {

    }

    protected override object? NewImplementationFromDevice(IExternal external) => null;
}