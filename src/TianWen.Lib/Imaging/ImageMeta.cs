using TianWen.Lib.Devices;
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
    float Longitude
);
