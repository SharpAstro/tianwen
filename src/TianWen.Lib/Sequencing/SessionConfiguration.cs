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
    int GuidingTries,
    bool MeasureBacklashIfUnknown = true,
    int AutoFocusRange = 200,
    int AutoFocusStepCount = 9,
    float FocusDriftThreshold = 1.07f,
    TimeSpan? MaxWaitForRisingTarget = null,
    bool AlwaysRefocusOnNewTarget = false,
    int BaselineHfdFrameCount = 3,
    TimeSpan? DefaultSubExposure = null,
    FocusFilterStrategy FocusFilterStrategy = FocusFilterStrategy.Auto
);
