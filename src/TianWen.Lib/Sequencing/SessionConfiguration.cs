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
    int DeviceFaultDecayFrames = 10,
    /// <summary>
    /// Length of each scout exposure taken by <c>ScoutAndProbeAsync</c> after centering and
    /// before committing to the guider/imaging loop. Long enough to detect ~10 stars with
    /// the wide-field setup (15 s at 200 mm/f/4) but short enough that a wasted scout costs
    /// less than a wasted full-length exposure.
    /// </summary>
    TimeSpan? ScoutExposure = null,
    /// <summary>
    /// Star-count ratio (post-scout / previous-target baseline, exposure-scaled) at or above
    /// which the FOV is considered healthy and imaging proceeds without a nudge test.
    /// </summary>
    float ObstructionStarCountRatioHealthy = 0.7f,
    /// <summary>
    /// Star-count ratio below which the FOV is considered severely degraded. Between this
    /// and <see cref="ObstructionStarCountRatioHealthy"/> the rig runs the altitude-nudge
    /// disambiguation test before deciding obstruction vs. transparency.
    /// </summary>
    float ObstructionStarCountRatioSevere = 0.3f,
    /// <summary>
    /// Multiplier on the half-FOV of the widest OTA used to size the upward altitude nudge
    /// during the obstruction probe. Default <c>1.0</c> = one full half-FOV up.
    /// </summary>
    float ObstructionNudgeRadii = 1.0f,
    /// <summary>
    /// If the trajectory-projected obstruction clear time is within this fraction of the
    /// observation's remaining allocation, the session sleeps until clear instead of
    /// advancing. Default <c>0.2</c> = wait if it'll clear in &lt;= 20% of remaining time.
    /// </summary>
    float ObstructionClearFractionOfRemaining = 0.2f,
    /// <summary>
    /// When <c>true</c>, scout frames are written to disk for debugging. Default <c>false</c>:
    /// scout frames are taken, analysed for star count, then released without a FITS write.
    /// </summary>
    bool SaveScoutFrames = false
);
