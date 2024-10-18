namespace Astap.Lib.Devices.Ascom;

public class AscomSwitchDriver(AscomDevice device, IExternal external) : AscomDeviceDriverBase(device, external), ISwitchDriver
{
}
