using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Weather;

namespace TianWen.Lib.Sequencing;

public record Weather(DeviceBase Device, IExternal External) : ControllableDeviceBase<IWeatherDriver>(Device, External)
{
    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
