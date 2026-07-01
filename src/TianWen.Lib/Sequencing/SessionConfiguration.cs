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
    bool SaveScoutFrames = false,
    /// <summary>
    /// Minutes <em>before</em> meridian crossing where the OTA / cables / focuser physically
    /// risk hitting the pier. Tracking is paused on entry and resumed only after the obstruction
    /// has cleared (HA &gt;= <see cref="MeridianFlipEarliestMinutesAfter"/>). Default <c>0</c>
    /// = no obstruction zone (refractor-on-tripod, side-by-side rigs with clean clearance).
    /// Typical SCT-on-pier values: 5-10 min.
    /// </summary>
    double MeridianFlipObstructionZoneMinutesBefore = 0,
    /// <summary>
    /// Earliest acceptable hour angle (in minutes past meridian) at which a flip may be commanded.
    /// Below this, the loop continues imaging on the east side. Default <c>5</c> min.
    /// </summary>
    double MeridianFlipEarliestMinutesAfter = 5,
    /// <summary>
    /// Latest hour angle (in minutes past meridian) at which a flip is still allowed before the
    /// rig is considered past its tracking limit. Equal to
    /// <see cref="MeridianFlipEarliestMinutesAfter"/> = single fixed flip point; larger = an
    /// opportunistic window in which the flip can happen between exposures. Default <c>10</c> min.
    /// </summary>
    double MeridianFlipLatestMinutesAfter = 10,
    /// <summary>
    /// Backstop for how long the imaging loop defers to the guider recovering a lock in place
    /// (states "Calibrating"/"Settling") before forcing a clean restart. The built-in guider
    /// bounds its own recovery and goes to "Stopped" (raising a guiding error) when it truly
    /// gives up -- at which point the session restarts immediately, without waiting this out.
    /// This grace only covers a pathological never-completing "Settling": normal bounded
    /// recovery resolves to Guiding or Stopped well before it. <c>null</c> = use
    /// <see cref="SessionConfiguration.DefaultGuiderRecoveryGrace"/>. Slow guide cameras
    /// (long guide exposures) legitimately need a longer grace.
    /// </summary>
    TimeSpan? GuiderRecoveryGrace = null,
    /// <summary>
    /// Lead time subtracted from <see cref="ScheduledObservation.Start"/> before the observation
    /// loop begins slewing, so the slew + centering + guider-settle overhead completes close to the
    /// scheduled start and the first light frame lands near <c>Start</c>. <c>null</c> = use
    /// <see cref="SessionConfiguration.DefaultScheduledStartLeadTime"/> (3 min).
    /// Schedules whose observations all share the same (or a past) <c>Start</c> -- the hosted API
    /// and legacy callers -- short-circuit the wait entirely, so this only takes effect for true
    /// future-start schedules produced by the planner.
    /// </summary>
    TimeSpan? ScheduledStartLeadTime = null,
    /// <summary>
    /// Master switch for the first-scout zenith-anchored obstruction oracle + cloud gate. When the
    /// first observation of the night has no same-night baseline, the rough-focus zenith frame is used
    /// to calibrate an expected star count for the target; a large shortfall routes into the nudge test.
    /// </summary>
    bool FirstScoutOracleEnabled = true,
    /// <summary>
    /// Fraction of the (zenith-calibrated, else catalog-floor) expected star count below which the
    /// first scout is considered suspicious and routed into the obstruction nudge test. Deliberately
    /// looser than the same-night <see cref="ObstructionStarCountRatioHealthy"/> because an absolute
    /// expectation is a weaker reference than a same-night baseline.
    /// </summary>
    float OracleFactor = 0.4f,
    /// <summary>
    /// Minimum catalog-predicted star count for the first-scout oracle / zenith gauge to be trusted.
    /// Below this (e.g. a tiny FOV at high galactic latitude) the oracle is skipped and the first
    /// observation is waved through as before -- a handful of expected stars cannot support the test.
    /// </summary>
    int MinOracleStarCount = 10,
    /// <summary>
    /// Zenith detection efficiency (detected / catalog-predicted at the unobstructed zenith) below
    /// which the whole sky is treated as clouded over: the session holds and re-gauges (up to
    /// <see cref="ConditionRecoveryTimeout"/>) instead of imaging into cloud. Zenith can't be blocked,
    /// so a crushed ratio there is transparency, never obstruction.
    /// </summary>
    float CloudGateEfficiencyFloor = 0.15f,
    /// <summary>
    /// When <c>true</c>, an automated panel/calibrator flat block runs at the end of a normally
    /// completed session (after the observation loop, before <c>Finalise</c> warms the cameras, so the
    /// flats are taken at the imaging setpoint temperature). Per OTA: close the cover, turn the
    /// calibrator on, and for each installed filter auto-expose to <see cref="FlatTargetAduFraction"/>
    /// then write <see cref="Imaging.FrameType.Flat"/> frames the stacker's <c>MasterFrameBuilder</c>
    /// consumes automatically. Default OFF; the same routine is exposed as an on-demand method.
    /// Skipped on abort/failure (a user who stopped the session wants a quick shutdown).
    /// </summary>
    bool TakeFlatsOnSessionEnd = false,
    /// <summary>Target flat level as a fraction of the sensor ceiling (median ADU / max ADU). Default 0.5 = half full well.</summary>
    double FlatTargetAduFraction = 0.5,
    /// <summary>Half-width of the acceptance band around <see cref="FlatTargetAduFraction"/>. Default 0.05.</summary>
    double FlatAduTolerance = 0.05,
    /// <summary>Maximum metering exposures the auto-exposure routine takes per filter before giving up. Default 6.</summary>
    int FlatMaxBrackets = 6,
    /// <summary>Number of flat frames written per filter once the exposure has converged. Default 15.</summary>
    int FlatsPerFilter = 15,
    /// <summary>First metering exposure. <c>null</c> = <see cref="DefaultFlatInitialExposure"/> (1 s).</summary>
    TimeSpan? FlatInitialExposure = null,
    /// <summary>Shortest flat exposure the solver will use. <c>null</c> = <see cref="DefaultFlatMinExposure"/> (0.1 s).</summary>
    TimeSpan? FlatMinExposure = null,
    /// <summary>Longest flat exposure before the solver fails ("panel too dim"). <c>null</c> = <see cref="DefaultFlatMaxExposure"/> (30 s).</summary>
    TimeSpan? FlatMaxExposure = null,
    /// <summary>
    /// Calibrator panel brightness as a percentage of the driver's <c>MaxBrightness</c> (a coarse
    /// pre-set; exposure does the fine convergence). When <c>MaxBrightness</c> is unknown (-1) the
    /// value is passed through as an absolute brightness. Default 50.
    /// </summary>
    int FlatCalibratorBrightnessPercent = 50,
    /// <summary>
    /// Illumination source for the end-of-session flat block (gated by <see cref="TakeFlatsOnSessionEnd"/>):
    /// <see cref="FlatIlluminationSource.Calibrator"/> (default) runs a controllable panel/flip-flat; with
    /// <see cref="FlatIlluminationSource.TwilightSky"/> the end-of-session block instead shoots
    /// <em>dawn</em> twilight sky-flats (see <see cref="TakeSkyFlatsAtDusk"/> for the evening counterpart).
    /// </summary>
    FlatIlluminationSource FlatSource = FlatIlluminationSource.Calibrator,
    /// <summary>
    /// When <c>true</c>, a <em>dusk</em> (evening) twilight sky-flat run executes at session start -- after
    /// cooling to the imaging setpoint but before the wait-for-dark, while the sky is still in twilight.
    /// Independent of <see cref="TakeFlatsOnSessionEnd"/> so both dusk and dawn flats can be captured in one
    /// session (insurance against a clouded dawn). Always uses the twilight-sky strategy (a dumb panel is
    /// time-independent, so there is no "dusk panel" mode). Default OFF.
    /// </summary>
    bool TakeSkyFlatsAtDusk = false,
    /// <summary>
    /// Hour-angle offset from the meridian toward the anti-solar sky for sky-flat pointing (applied west of
    /// the meridian at dawn, east at dusk, both at Dec = site latitude) to minimise the twilight gradient
    /// across the frame. <c>null</c> = <see cref="DefaultFlatSkyMeridianTilt"/> (1 h ~ 15 deg).
    /// </summary>
    TimeSpan? FlatSkyMeridianTilt = null,
    /// <summary>
    /// Maximum wall-clock duration of a single sky-flat run; bounds the total time spent waiting for the
    /// twilight sky to enter the usable band. <c>null</c> = <see cref="DefaultFlatSkyMaxDuration"/> (25 min).
    /// </summary>
    TimeSpan? FlatSkyMaxDuration = null,
    /// <summary>
    /// Sleep between re-meters while waiting for the twilight sky to ramp into the usable band (a
    /// <see cref="Imaging.Calibration.SkyFlatAction.Wait"/> verdict). <c>null</c> =
    /// <see cref="DefaultFlatSkySettleInterval"/> (20 s).
    /// </summary>
    TimeSpan? FlatSkySettleInterval = null,
    /// <summary>
    /// Solar altitude (degrees) above which the twilight sky is too bright for flats (the bright edge of the
    /// usable window). Used only for the coarse start gate that skips a run whose window has clearly already
    /// passed; the per-frame exposure solver does the fine convergence. Default -3.
    /// </summary>
    double FlatSkySunAltitudeBrightDeg = -3,
    /// <summary>
    /// Solar altitude (degrees) below which the twilight sky is too dark for flats (the dark edge of the
    /// usable window). Used only for the coarse start gate (see <see cref="FlatSkySunAltitudeBrightDeg"/>).
    /// Default -14.
    /// </summary>
    double FlatSkySunAltitudeDarkDeg = -14
)
{
    /// <summary>Effective default for <see cref="GuiderRecoveryGrace"/> when unset.</summary>
    public static readonly TimeSpan DefaultGuiderRecoveryGrace = TimeSpan.FromMinutes(3);

    /// <summary>Effective default for <see cref="ScheduledStartLeadTime"/> when unset.</summary>
    public static readonly TimeSpan DefaultScheduledStartLeadTime = TimeSpan.FromMinutes(3);

    /// <summary>Effective default for <see cref="FlatInitialExposure"/> when unset.</summary>
    public static readonly TimeSpan DefaultFlatInitialExposure = TimeSpan.FromSeconds(1);

    /// <summary>Effective default for <see cref="FlatMinExposure"/> when unset.</summary>
    public static readonly TimeSpan DefaultFlatMinExposure = TimeSpan.FromSeconds(0.1);

    /// <summary>Effective default for <see cref="FlatMaxExposure"/> when unset.</summary>
    public static readonly TimeSpan DefaultFlatMaxExposure = TimeSpan.FromSeconds(30);

    /// <summary>Effective default for <see cref="FlatSkyMeridianTilt"/> when unset (1 h ~ 15 deg of anti-solar tilt).</summary>
    public static readonly TimeSpan DefaultFlatSkyMeridianTilt = TimeSpan.FromHours(1);

    /// <summary>Effective default for <see cref="FlatSkyMaxDuration"/> when unset.</summary>
    public static readonly TimeSpan DefaultFlatSkyMaxDuration = TimeSpan.FromMinutes(25);

    /// <summary>Effective default for <see cref="FlatSkySettleInterval"/> when unset.</summary>
    public static readonly TimeSpan DefaultFlatSkySettleInterval = TimeSpan.FromSeconds(20);
}
