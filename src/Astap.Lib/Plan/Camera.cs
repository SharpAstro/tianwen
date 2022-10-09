using Astap.Lib.Devices;

namespace Astap.Lib.Plan;

public class Camera : ControllableDeviceBase<ICameraDriver>
{
    public Camera(DeviceBase device) : base(device) { }

    public bool? HasCooler => Driver.CanGetCoolerPower;

    public double? PixelSizeX => Driver.PixelSizeX;

    public double? PixelSizeY => Driver.PixelSizeY;

    protected override void Driver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
    {
        // nothing
    }
}
