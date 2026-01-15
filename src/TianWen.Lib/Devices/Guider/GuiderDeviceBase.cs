using System;
using System.Text;

namespace TianWen.Lib.Devices.Guider;

public abstract record class GuiderDeviceBase(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public abstract string? ProfileName { get; }

    /// <summary>
    /// Assume that implementors will display profile in one way or the other
    /// </summary>
    /// <param name="stringBuilder"></param>
    /// <returns></returns>
    protected override bool PrintMembers(StringBuilder stringBuilder) => base.PrintMembers(stringBuilder);
}