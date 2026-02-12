namespace TianWen.Lib.Devices.Ascom;

using System.Runtime.Versioning;
using AscomSwitch = ASCOM.Com.DriverAccess.Switch;

[SupportedOSPlatform("windows")]
internal class AscomSwitchDriver(AscomDevice device, IExternal external)
    : AscomDeviceDriverBase<AscomSwitch>(device, external, (progId, logger) => new AscomSwitch(progId, new AscomLoggerWrapper(logger))), ISwitchDriver
{
}