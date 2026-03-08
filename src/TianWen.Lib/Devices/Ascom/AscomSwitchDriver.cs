namespace TianWen.Lib.Devices.Ascom;

using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal class AscomSwitchDriver(AscomDevice device, IExternal external)
    : AscomDeviceDriverBase(device, external), ISwitchDriver
{
}
