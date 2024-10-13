using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Imaging;

public record struct ImageMeta(
    string Instrument,
    DateTime ExposureStartTime,
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
    float Longitude
);
