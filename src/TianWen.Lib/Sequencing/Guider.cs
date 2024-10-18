using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;

namespace TianWen.Lib.Sequencing;

public record Guider(DeviceBase Device, IExternal External) : ControllableDeviceBase<IGuider>(Device, External)
{

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
