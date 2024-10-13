using Astap.Lib.Astrometry.PlateSolve;
using Astap.Lib.Devices;
using Astap.Lib.Devices.Guider;
using Astap.Lib.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Astap.Lib.Sequencing;

public record Guider(DeviceBase Device) : ControllableDeviceBase<IGuider>(Device)
{

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
