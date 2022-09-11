using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using System;

namespace Astap.Lib.Plan.Ascom;

public class AscomCamera : CameraBase<AscomDevice, AscomCameraDriver>
{
    public AscomCamera(AscomDevice device)
        : base(device.DeviceType == DeviceBase.CameraType ? device : throw new ArgumentException("Device is not an ASCOM camera", nameof(device)))
    {
        // calls base
    }

    public override bool? HasCooler => Driver.CanGetCoolerPower;

    public override double? PixelSizeX => Driver.PixelSizeX;

    public override double? PixelSizeY => Driver.PixelSizeY;
}
