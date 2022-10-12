using Astap.Lib.Imaging;
using System;

namespace Astap.Lib.Devices.Ascom;

public class AscomCameraDriver : AscomDeviceDriverBase, ICameraDriver
{
    public AscomCameraDriver(AscomDevice device) : base(device)
    {

    }

    public bool? CanGetCoolerPower => Connected && _comObject?.CanGetCoolerPower is bool canSetCoolerPower ? canSetCoolerPower : null;

    public double? PixelSizeX => Connected && _comObject?.PixelSizeX is double pixelSizeX ? pixelSizeX : null;

    public double? PixelSizeY => Connected && _comObject?.PixelSizeY is double pixelSizeY ? pixelSizeY : null;

    public int? StartX => Connected && _comObject?.StartX is int startX ? startX : null;

    public int? StartY => Connected && _comObject?.StartY is int startY ? startY : null;

    public int[,]? ImageData => _comObject?.ImageArray is int[,] intArray ? intArray : null;

    public bool? ImageReady => _comObject?.ImageReady is bool imageReady ? imageReady : null;

    public void StartExposure(TimeSpan duration, bool light) => _comObject?.StartExposure(duration.TotalSeconds, light);
}