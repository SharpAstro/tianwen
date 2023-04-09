using System;

namespace Astap.Lib.Sequencing;

public record SessionConfiguration(
    SetpointTemp SetpointCCDTemperature,
    TimeSpan CooldownRampInterval,
    TimeSpan CoolupRampInterval,
    byte MinHeightAboveHorizon,
    double DitherPixel,
    double SettlePixel,
    int DitherEveryNFrame,
    TimeSpan SettleTime,
    int GuidingTries
);
