using System;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Devices;

/// <summary>
/// Stores information specific to each exposure (for driver clients to query last exposure duration/start time, frame type)
/// </summary>
/// <param name="StartTime"></param>
/// <param name="IntendedDuration"></param>
/// <param name="FrameType"></param>
public readonly record struct ExposureData(DateTimeOffset StartTime, TimeSpan IntendedDuration, TimeSpan? ActualDuration, FrameType FrameType, int Gain, int Offset);
