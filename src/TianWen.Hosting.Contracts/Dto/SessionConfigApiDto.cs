using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

/// <summary>
/// The session configuration on the wire: EVERY field of <see cref="SessionConfiguration"/>, each optional.
/// An unset field takes the declared default (<see cref="SessionConfiguration()"/>), so an empty body is
/// the defaults and a client sends only what it changes. Durations are seconds unless the name says
/// minutes; enums cross as their numbers, like everything on <c>HostingJsonContext</c>.
/// </summary>
/// <remarks>
/// It used to carry 16 of the 60 fields, so the site, the flip windows, the unattended prompt answer and
/// 40 more could not be set over the API at all (P0b item 10 of docs/plans/hardware-in-the-server.md,
/// #752). <b>A field added to <see cref="SessionConfiguration"/> is added here and to
/// <c>SessionConfigApiDtoTests</c>, whose round trip sets every field to a non-default value</b>;
/// nothing else would notice it missing.
/// </remarks>
public sealed class SessionConfigApiDto
{
    public double? SetpointTemperature { get; init; }
    public SetpointTempKind? SetpointKind { get; init; }
    public double? CooldownRampSeconds { get; init; }
    public double? WarmupRampSeconds { get; init; }
    public byte? MinHeightAboveHorizon { get; init; }
    public double? DitherPixel { get; init; }
    public double? SettlePixel { get; init; }
    public int? DitherEveryNthFrame { get; init; }
    public double? SettleTimeSeconds { get; init; }
    public int? GuidingTries { get; init; }
    public double? SiteLatitude { get; init; }
    public double? SiteLongitude { get; init; }
    public int? AutoFocusRange { get; init; }
    public int? AutoFocusStepCount { get; init; }
    public float? FocusDriftThreshold { get; init; }
    public double? MaxWaitForRisingTargetSeconds { get; init; }
    public bool? AlwaysRefocusOnNewTarget { get; init; }
    public int? BaselineHfdFrameCount { get; init; }
    public double? DefaultSubExposureSeconds { get; init; }
    public FocusFilterStrategy? FocusFilterStrategy { get; init; }
    public double? MosaicOverlap { get; init; }
    public double? MosaicMargin { get; init; }
    public float? ConditionDeteriorationThreshold { get; init; }
    public double? ConditionRecoveryTimeoutMinutes { get; init; }
    public bool? WarmCamerasOnSessionEnd { get; init; }
    public int? DeviceFaultEscalationThreshold { get; init; }
    public int? DeviceFaultDecayFrames { get; init; }
    public double? ScoutExposureSeconds { get; init; }
    public float? ObstructionStarCountRatioHealthy { get; init; }
    public float? ObstructionStarCountRatioSevere { get; init; }
    public float? ObstructionNudgeRadii { get; init; }
    public float? ObstructionClearFractionOfRemaining { get; init; }
    public double? MeridianFlipObstructionZoneMinutesBefore { get; init; }
    public double? MeridianFlipEarliestMinutesAfter { get; init; }
    public double? MeridianFlipLatestMinutesAfter { get; init; }
    public double? GuiderRecoveryGraceMinutes { get; init; }
    public double? ScheduledStartLeadTimeSeconds { get; init; }
    public bool? FirstScoutOracleEnabled { get; init; }
    public float? OracleFactor { get; init; }
    public int? MinOracleStarCount { get; init; }
    public float? CloudGateEfficiencyFloor { get; init; }
    public bool? TakeFlatsOnSessionEnd { get; init; }
    public UnattendedPromptResponse? UnattendedPromptResponse { get; init; }
    public double? FlatTargetAduFraction { get; init; }
    public double? FlatAduTolerance { get; init; }
    public int? FlatMaxBrackets { get; init; }
    public int? FlatsPerFilter { get; init; }
    public double? FlatInitialExposureSeconds { get; init; }
    public double? FlatMinExposureSeconds { get; init; }
    public double? FlatMaxExposureSeconds { get; init; }
    public int? FlatCalibratorBrightnessPercent { get; init; }
    public FlatIlluminationSource? FlatSource { get; init; }
    public bool? TakeSkyFlatsAtDusk { get; init; }
    public double? FlatSkyMeridianTiltSeconds { get; init; }
    public double? FlatSkyMaxDurationSeconds { get; init; }
    public double? FlatSkySettleIntervalSeconds { get; init; }
    public double? FlatSkySunAltitudeBrightDeg { get; init; }
    public double? FlatSkySunAltitudeDarkDeg { get; init; }
    public int? FocusDriftSampleSize { get; init; }
    public int? FocusDriftMinSamples { get; init; }
    public bool? SaveIntermediates { get; init; }

    /// <summary>The configuration: every set field over the declared defaults.</summary>
    public SessionConfiguration ToConfiguration()
    {
        var d = new SessionConfiguration();
        return d with
        {
            SetpointCCDTemperature = SetpointTemperature is { } setpoint
                ? new SetpointTemp((sbyte)setpoint, SetpointKind ?? SetpointTempKind.Normal)
                : d.SetpointCCDTemperature,
            CooldownRampInterval = Seconds(CooldownRampSeconds) ?? d.CooldownRampInterval,
            WarmupRampInterval = Seconds(WarmupRampSeconds) ?? d.WarmupRampInterval,
            MinHeightAboveHorizon = MinHeightAboveHorizon ?? d.MinHeightAboveHorizon,
            DitherPixel = DitherPixel ?? d.DitherPixel,
            SettlePixel = SettlePixel ?? d.SettlePixel,
            DitherEveryNthFrame = DitherEveryNthFrame ?? d.DitherEveryNthFrame,
            SettleTime = Seconds(SettleTimeSeconds) ?? d.SettleTime,
            GuidingTries = GuidingTries ?? d.GuidingTries,
            SiteLatitude = SiteLatitude ?? d.SiteLatitude,
            SiteLongitude = SiteLongitude ?? d.SiteLongitude,
            AutoFocusRange = AutoFocusRange ?? d.AutoFocusRange,
            AutoFocusStepCount = AutoFocusStepCount ?? d.AutoFocusStepCount,
            FocusDriftThreshold = FocusDriftThreshold ?? d.FocusDriftThreshold,
            MaxWaitForRisingTarget = Seconds(MaxWaitForRisingTargetSeconds) ?? d.MaxWaitForRisingTarget,
            AlwaysRefocusOnNewTarget = AlwaysRefocusOnNewTarget ?? d.AlwaysRefocusOnNewTarget,
            BaselineHfdFrameCount = BaselineHfdFrameCount ?? d.BaselineHfdFrameCount,
            DefaultSubExposure = Seconds(DefaultSubExposureSeconds) ?? d.DefaultSubExposure,
            FocusFilterStrategy = FocusFilterStrategy ?? d.FocusFilterStrategy,
            MosaicOverlap = MosaicOverlap ?? d.MosaicOverlap,
            MosaicMargin = MosaicMargin ?? d.MosaicMargin,
            ConditionDeteriorationThreshold = ConditionDeteriorationThreshold ?? d.ConditionDeteriorationThreshold,
            ConditionRecoveryTimeout = Minutes(ConditionRecoveryTimeoutMinutes) ?? d.ConditionRecoveryTimeout,
            WarmCamerasOnSessionEnd = WarmCamerasOnSessionEnd ?? d.WarmCamerasOnSessionEnd,
            DeviceFaultEscalationThreshold = DeviceFaultEscalationThreshold ?? d.DeviceFaultEscalationThreshold,
            DeviceFaultDecayFrames = DeviceFaultDecayFrames ?? d.DeviceFaultDecayFrames,
            ScoutExposure = Seconds(ScoutExposureSeconds) ?? d.ScoutExposure,
            ObstructionStarCountRatioHealthy = ObstructionStarCountRatioHealthy ?? d.ObstructionStarCountRatioHealthy,
            ObstructionStarCountRatioSevere = ObstructionStarCountRatioSevere ?? d.ObstructionStarCountRatioSevere,
            ObstructionNudgeRadii = ObstructionNudgeRadii ?? d.ObstructionNudgeRadii,
            ObstructionClearFractionOfRemaining = ObstructionClearFractionOfRemaining ?? d.ObstructionClearFractionOfRemaining,
            MeridianFlipObstructionZoneMinutesBefore = MeridianFlipObstructionZoneMinutesBefore ?? d.MeridianFlipObstructionZoneMinutesBefore,
            MeridianFlipEarliestMinutesAfter = MeridianFlipEarliestMinutesAfter ?? d.MeridianFlipEarliestMinutesAfter,
            MeridianFlipLatestMinutesAfter = MeridianFlipLatestMinutesAfter ?? d.MeridianFlipLatestMinutesAfter,
            GuiderRecoveryGrace = Minutes(GuiderRecoveryGraceMinutes) ?? d.GuiderRecoveryGrace,
            ScheduledStartLeadTime = Seconds(ScheduledStartLeadTimeSeconds) ?? d.ScheduledStartLeadTime,
            FirstScoutOracleEnabled = FirstScoutOracleEnabled ?? d.FirstScoutOracleEnabled,
            OracleFactor = OracleFactor ?? d.OracleFactor,
            MinOracleStarCount = MinOracleStarCount ?? d.MinOracleStarCount,
            CloudGateEfficiencyFloor = CloudGateEfficiencyFloor ?? d.CloudGateEfficiencyFloor,
            TakeFlatsOnSessionEnd = TakeFlatsOnSessionEnd ?? d.TakeFlatsOnSessionEnd,
            UnattendedPromptResponse = UnattendedPromptResponse ?? d.UnattendedPromptResponse,
            FlatTargetAduFraction = FlatTargetAduFraction ?? d.FlatTargetAduFraction,
            FlatAduTolerance = FlatAduTolerance ?? d.FlatAduTolerance,
            FlatMaxBrackets = FlatMaxBrackets ?? d.FlatMaxBrackets,
            FlatsPerFilter = FlatsPerFilter ?? d.FlatsPerFilter,
            FlatInitialExposure = Seconds(FlatInitialExposureSeconds) ?? d.FlatInitialExposure,
            FlatMinExposure = Seconds(FlatMinExposureSeconds) ?? d.FlatMinExposure,
            FlatMaxExposure = Seconds(FlatMaxExposureSeconds) ?? d.FlatMaxExposure,
            FlatCalibratorBrightnessPercent = FlatCalibratorBrightnessPercent ?? d.FlatCalibratorBrightnessPercent,
            FlatSource = FlatSource ?? d.FlatSource,
            TakeSkyFlatsAtDusk = TakeSkyFlatsAtDusk ?? d.TakeSkyFlatsAtDusk,
            FlatSkyMeridianTilt = Seconds(FlatSkyMeridianTiltSeconds) ?? d.FlatSkyMeridianTilt,
            FlatSkyMaxDuration = Seconds(FlatSkyMaxDurationSeconds) ?? d.FlatSkyMaxDuration,
            FlatSkySettleInterval = Seconds(FlatSkySettleIntervalSeconds) ?? d.FlatSkySettleInterval,
            FlatSkySunAltitudeBrightDeg = FlatSkySunAltitudeBrightDeg ?? d.FlatSkySunAltitudeBrightDeg,
            FlatSkySunAltitudeDarkDeg = FlatSkySunAltitudeDarkDeg ?? d.FlatSkySunAltitudeDarkDeg,
            FocusDriftSampleSize = FocusDriftSampleSize ?? d.FocusDriftSampleSize,
            FocusDriftMinSamples = FocusDriftMinSamples ?? d.FocusDriftMinSamples,
            SaveIntermediates = SaveIntermediates ?? d.SaveIntermediates,
        };
    }

    /// <summary>Every field of <paramref name="config"/>, so the node runs exactly what the client holds.</summary>
    public static SessionConfigApiDto FromConfiguration(in SessionConfiguration config) => new SessionConfigApiDto
    {
        SetpointTemperature = config.SetpointCCDTemperature.TempC,
        SetpointKind = config.SetpointCCDTemperature.Kind,
        CooldownRampSeconds = config.CooldownRampInterval.TotalSeconds,
        WarmupRampSeconds = config.WarmupRampInterval.TotalSeconds,
        MinHeightAboveHorizon = config.MinHeightAboveHorizon,
        DitherPixel = config.DitherPixel,
        SettlePixel = config.SettlePixel,
        DitherEveryNthFrame = config.DitherEveryNthFrame,
        SettleTimeSeconds = config.SettleTime.TotalSeconds,
        GuidingTries = config.GuidingTries,
        // A NaN is not JSON, and an unset site IS the default, so it travels as absent.
        SiteLatitude = double.IsNaN(config.SiteLatitude) ? null : config.SiteLatitude,
        SiteLongitude = double.IsNaN(config.SiteLongitude) ? null : config.SiteLongitude,
        AutoFocusRange = config.AutoFocusRange,
        AutoFocusStepCount = config.AutoFocusStepCount,
        FocusDriftThreshold = config.FocusDriftThreshold,
        MaxWaitForRisingTargetSeconds = config.MaxWaitForRisingTarget?.TotalSeconds,
        AlwaysRefocusOnNewTarget = config.AlwaysRefocusOnNewTarget,
        BaselineHfdFrameCount = config.BaselineHfdFrameCount,
        DefaultSubExposureSeconds = config.DefaultSubExposure?.TotalSeconds,
        FocusFilterStrategy = config.FocusFilterStrategy,
        MosaicOverlap = config.MosaicOverlap,
        MosaicMargin = config.MosaicMargin,
        ConditionDeteriorationThreshold = config.ConditionDeteriorationThreshold,
        ConditionRecoveryTimeoutMinutes = config.ConditionRecoveryTimeout?.TotalMinutes,
        WarmCamerasOnSessionEnd = config.WarmCamerasOnSessionEnd,
        DeviceFaultEscalationThreshold = config.DeviceFaultEscalationThreshold,
        DeviceFaultDecayFrames = config.DeviceFaultDecayFrames,
        ScoutExposureSeconds = config.ScoutExposure?.TotalSeconds,
        ObstructionStarCountRatioHealthy = config.ObstructionStarCountRatioHealthy,
        ObstructionStarCountRatioSevere = config.ObstructionStarCountRatioSevere,
        ObstructionNudgeRadii = config.ObstructionNudgeRadii,
        ObstructionClearFractionOfRemaining = config.ObstructionClearFractionOfRemaining,
        MeridianFlipObstructionZoneMinutesBefore = config.MeridianFlipObstructionZoneMinutesBefore,
        MeridianFlipEarliestMinutesAfter = config.MeridianFlipEarliestMinutesAfter,
        MeridianFlipLatestMinutesAfter = config.MeridianFlipLatestMinutesAfter,
        GuiderRecoveryGraceMinutes = config.GuiderRecoveryGrace?.TotalMinutes,
        ScheduledStartLeadTimeSeconds = config.ScheduledStartLeadTime?.TotalSeconds,
        FirstScoutOracleEnabled = config.FirstScoutOracleEnabled,
        OracleFactor = config.OracleFactor,
        MinOracleStarCount = config.MinOracleStarCount,
        CloudGateEfficiencyFloor = config.CloudGateEfficiencyFloor,
        TakeFlatsOnSessionEnd = config.TakeFlatsOnSessionEnd,
        UnattendedPromptResponse = config.UnattendedPromptResponse,
        FlatTargetAduFraction = config.FlatTargetAduFraction,
        FlatAduTolerance = config.FlatAduTolerance,
        FlatMaxBrackets = config.FlatMaxBrackets,
        FlatsPerFilter = config.FlatsPerFilter,
        FlatInitialExposureSeconds = config.FlatInitialExposure?.TotalSeconds,
        FlatMinExposureSeconds = config.FlatMinExposure?.TotalSeconds,
        FlatMaxExposureSeconds = config.FlatMaxExposure?.TotalSeconds,
        FlatCalibratorBrightnessPercent = config.FlatCalibratorBrightnessPercent,
        FlatSource = config.FlatSource,
        TakeSkyFlatsAtDusk = config.TakeSkyFlatsAtDusk,
        FlatSkyMeridianTiltSeconds = config.FlatSkyMeridianTilt?.TotalSeconds,
        FlatSkyMaxDurationSeconds = config.FlatSkyMaxDuration?.TotalSeconds,
        FlatSkySettleIntervalSeconds = config.FlatSkySettleInterval?.TotalSeconds,
        FlatSkySunAltitudeBrightDeg = config.FlatSkySunAltitudeBrightDeg,
        FlatSkySunAltitudeDarkDeg = config.FlatSkySunAltitudeDarkDeg,
        FocusDriftSampleSize = config.FocusDriftSampleSize,
        FocusDriftMinSamples = config.FocusDriftMinSamples,
        SaveIntermediates = config.SaveIntermediates,
    };

    private static TimeSpan? Seconds(double? seconds) => seconds is { } s ? TimeSpan.FromSeconds(s) : null;

    private static TimeSpan? Minutes(double? minutes) => minutes is { } m ? TimeSpan.FromMinutes(m) : null;
}
