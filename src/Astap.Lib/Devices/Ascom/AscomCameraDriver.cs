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

    public int? MaxADU => _comObject?.MaxADU is int maxADU ? maxADU : null;

    public double? FullWellCapacity => _comObject?.FullWellCapacity is double fullWellCapacity ? fullWellCapacity : null;

    public int? BitDepth
    {
        get
        {
            if (MaxADU is not int maxADU || FullWellCapacity is not double fwc)
            {
                return null;
            }

            if (maxADU == byte.MaxValue && maxADU < fwc && Name.Contains("QHYCCD", StringComparison.OrdinalIgnoreCase))
            {
                maxADU = (int)fwc;
            }

            int log2 = (int)MathF.Ceiling(MathF.Log(maxADU) / MathF.Log(2.0f));
            var bytesPerPixel = (log2 + 7) / 8;
            int bitDepth = bytesPerPixel * 8;

            return bitDepth;
        }
    }

    public void StartExposure(TimeSpan duration, bool light) => _comObject?.StartExposure(duration.TotalSeconds, light);
}