using System;
using System.Text;

namespace TianWen.Lib.Devices;

/// <summary>
/// Build-in device type: <see cref="DeviceType.None"/>.
/// </summary>
/// <param name="DeviceUri"></param>
public sealed record class NoneDevice(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public NoneDevice() : this(new Uri($"none://{typeof(NoneDevice).Name}/None"))
    {
    }

    public static readonly NoneDevice Instance = new NoneDevice();

    protected override bool PrintMembers(StringBuilder stringBuilder)
    {
        return false;
    }
}