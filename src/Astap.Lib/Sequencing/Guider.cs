using Astap.Lib.Devices;
using Astap.Lib.Devices.Guider;

namespace Astap.Lib.Sequencing;

public record Guider(DeviceBase Device, IExternal External) : ControllableDeviceBase<IGuider>(Device, External)
{

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
