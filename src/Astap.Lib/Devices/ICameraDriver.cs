namespace Astap.Lib.Devices;

public interface ICameraDriver : IDeviceDriver
{
    bool? CanGetCoolerPower { get; }

    double? PixelSizeX { get; }

    double? PixelSizeY { get; }

    int? StartX { get; }

    int? StartY { get; }
}
