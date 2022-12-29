using System;

namespace Astap.Lib.Sequencing;

public record SessionConfiguration(
    int SetpointCCDTemperature,
    TimeSpan CooldownRampInterval,
    TimeSpan CoolupRampInterval,
    byte MinHeightAboveHorizon,
    double DitherPixel,
    double SettlePixel,
    TimeSpan SettleTime
);
