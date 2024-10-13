using Astap.Lib.Devices;
using System;

namespace Astap.Lib.Sequencing;

public record struct SessionConfiguration(
    SetpointTemp SetpointCCDTemperature,
    TimeSpan CooldownRampInterval,
    TimeSpan CoolupRampInterval,
    byte MinHeightAboveHorizon,
    double DitherPixel,
    double SettlePixel,
    int DitherEveryNthFrame,
    TimeSpan SettleTime,
    int GuidingTries
);
