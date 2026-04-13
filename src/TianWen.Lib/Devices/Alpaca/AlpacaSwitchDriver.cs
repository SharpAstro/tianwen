using System;

namespace TianWen.Lib.Devices.Alpaca;

internal class AlpacaSwitchDriver(AlpacaDevice device, IServiceProvider serviceProvider)
    : AlpacaDeviceDriverBase(device, serviceProvider), ISwitchDriver
{
}
