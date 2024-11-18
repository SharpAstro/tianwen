namespace TianWen.Lib.Devices.Ascom;

using AscomSwitch = ASCOM.Com.DriverAccess.Switch;

internal class AscomSwitchDriver(AscomDevice device, IExternal external)
    : AscomDeviceDriverBase<AscomSwitch>(device, external, (progId, logger) => new AscomSwitch(progId, new AscomLoggerWrapper(logger))), ISwitchDriver
{
}