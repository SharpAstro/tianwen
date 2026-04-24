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
    double SiteLatitude = double.NaN,
    double SiteLongitude = double.NaN,
    bool MeasureBacklashIfUnknown = true,
    int AutoFocusRange = 200,
    int AutoFocusStepCount = 9,
    float FocusDriftThreshold = 1.07f,
    TimeSpan? MaxWaitForRisingTarget = null,
    bool AlwaysRefocusOnNewTarget = false,
    int BaselineHfdFrameCount = 3,
    TimeSpan? DefaultSubExposure = null,
    FocusFilterStrategy FocusFilterStrategy = FocusFilterStrategy.Auto,
    double MosaicOverlap = 0.2,
    double MosaicMargin = 0.1,
    float ConditionDeteriorationThreshold = 0.5f,
    TimeSpan? ConditionRecoveryTimeout = null,
    bool WarmCamerasOnSessionEnd = true,
    /// <summary>
    /// Per-device reconnect-attempt count that trips escalation. When any one
    /// driver's fault counter reaches this threshold, the observation loop
    /// returns <c>ImageLoopNextAction.DeviceUnrecoverable</c> and the session
    /// finalises cleanly. The counter decays by one for every
    /// <c>DeviceFaultDecayFrames</c> successful frames so a bad hour on one
    /// night doesn't poison the next.
    /// </summary>
    int DeviceFaultEscalationThreshold = 5,
    /// <summary>
    /// Number of successful frames before the per-device fault counter decays
    /// by one. Set to <c>0</c> to disable decay (counter only resets on
    /// explicit recovery).
    /// </summary>
    int DeviceFaultDecayFrames = 10
);
