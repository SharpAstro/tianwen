using TianWen.Lib.Devices;
using System;

namespace TianWen.Lib.Sequencing;

public record struct SessionConfiguration(
    SetpointTemp SetpointCCDTemperature,
    TimeSpan CooldownRampInterval,
    TimeSpan WarmupRampInterval,
    byte MinHeightAboveHorizon,
    double DitherPixel,
    double SettlePixel,
    int DitherEveryNthFrame,
    TimeSpan SettleTime,
    int GuidingTries
);
