using System;

namespace TianWen.Lib.Imaging;

public record struct ImageMeta(
    string Instrument,
    DateTimeOffset ExposureStartTime,
    TimeSpan ExposureDuration,
    FrameType FrameType,
    string Telescope,
    float PixelSizeX,
    float PixelSizeY,
    int FocalLength,
    int FocusPos,
    Filter Filter,
    int BinX,
    int BinY,
    float CCDTemperature,
    SensorType SensorType,
    int BayerOffsetX,
    int BayerOffsetY,
    RowOrder RowOrder,
    float Latitude,
    float Longitude,
    string ObjectName = ""
)
{
    /// <summary>
    /// Pixel scale in arcsec/pixel, derived from pixel size and focal length.
    /// Returns NaN if either value is unavailable.
    /// </summary>
    public readonly double DerivedPixelScale =>
        FocalLength > 0 && PixelSizeX > 0
            ? PixelSizeX / FocalLength * 206.265
            : double.NaN;
}
