namespace TianWen.Lib.Devices.Alpaca;

internal class AlpacaSwitchDriver(AlpacaDevice device, IExternal external)
    : AlpacaDeviceDriverBase(device, external), ISwitchDriver
{
}
