using System;

namespace TianWen.Lib.Devices.Ascom;

using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal class AscomSwitchDriver(AscomDevice device, IServiceProvider sp)
    : AscomDeviceDriverBase(device, sp), ISwitchDriver
{
}
