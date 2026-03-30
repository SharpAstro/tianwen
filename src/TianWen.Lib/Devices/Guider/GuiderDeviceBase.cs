using System;
using System.Text;

namespace TianWen.Lib.Devices.Guider;

public abstract record class GuiderDeviceBase(Uri DeviceUri) : DeviceBase(DeviceUri)
{
    public abstract string? ProfileName { get; }

    /// <summary>
    /// Configuration capabilities this guider device supports.
    /// The equipment tab uses this to decide which settings to render.
    /// </summary>
    public virtual GuiderCapabilities Capabilities => GuiderCapabilities.None;
}