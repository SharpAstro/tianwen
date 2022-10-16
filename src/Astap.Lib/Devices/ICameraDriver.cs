using Astap.Lib.Imaging;
using System;

namespace Astap.Lib.Devices;

public interface ICameraDriver : IDeviceDriver
{
    bool? CanGetCoolerPower { get; }

    double? PixelSizeX { get; }

    double? PixelSizeY { get; }

    int? StartX { get; }

    int? StartY { get; }

    int[,]? ImageData { get; }

    bool? ImageReady { get; }

    void StartExposure(TimeSpan duration, bool light);

    Image? Image => ImageReady == true && ImageData is int[,] data ? null : null; // TODO convert data + get fixed bit depth
}
