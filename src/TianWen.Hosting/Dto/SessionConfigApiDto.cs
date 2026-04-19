using System;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Dto;

/// <summary>
/// Session configuration DTO for the REST API.
/// All fields are optional — unset fields use <see cref="SessionConfiguration"/> defaults.
/// </summary>
public sealed class SessionConfigApiDto
{
    public double? SetpointTemperature { get; init; }
    public double? CooldownRampSeconds { get; init; }
    public double? WarmupRampSeconds { get; init; }
    public byte? MinHeightAboveHorizon { get; init; }
    public double? DitherPixel { get; init; }
    public double? SettlePixel { get; init; }
    public int? DitherEveryNthFrame { get; init; }
    public double? SettleTimeSeconds { get; init; }
    public int? GuidingTries { get; init; }
    public int? AutoFocusRange { get; init; }
    public int? AutoFocusStepCount { get; init; }
    public float? FocusDriftThreshold { get; init; }
    public double? DefaultSubExposureSeconds { get; init; }
    public float? ConditionDeteriorationThreshold { get; init; }
    public double? ConditionRecoveryTimeoutMinutes { get; init; }

    /// <summary>
    /// Builds a <see cref="SessionConfiguration"/> by overlaying set fields onto defaults.
    /// </summary>
    public SessionConfiguration ToConfiguration()
    {
        var defaults = new SessionConfiguration();
        return defaults with
        {
            SetpointCCDTemperature = SetpointTemperature.HasValue
                ? new SetpointTemp((sbyte)SetpointTemperature.Value, SetpointTempKind.Normal)
                : defaults.SetpointCCDTemperature,
            CooldownRampInterval = CooldownRampSeconds.HasValue
                ? TimeSpan.FromSeconds(CooldownRampSeconds.Value)
                : defaults.CooldownRampInterval,
            WarmupRampInterval = WarmupRampSeconds.HasValue
                ? TimeSpan.FromSeconds(WarmupRampSeconds.Value)
                : defaults.WarmupRampInterval,
            MinHeightAboveHorizon = MinHeightAboveHorizon ?? defaults.MinHeightAboveHorizon,
            DitherPixel = DitherPixel ?? defaults.DitherPixel,
            SettlePixel = SettlePixel ?? defaults.SettlePixel,
            DitherEveryNthFrame = DitherEveryNthFrame ?? defaults.DitherEveryNthFrame,
            SettleTime = SettleTimeSeconds.HasValue
                ? TimeSpan.FromSeconds(SettleTimeSeconds.Value)
                : defaults.SettleTime,
            GuidingTries = GuidingTries ?? defaults.GuidingTries,
            AutoFocusRange = AutoFocusRange ?? defaults.AutoFocusRange,
            AutoFocusStepCount = AutoFocusStepCount ?? defaults.AutoFocusStepCount,
            FocusDriftThreshold = FocusDriftThreshold ?? defaults.FocusDriftThreshold,
            DefaultSubExposure = DefaultSubExposureSeconds.HasValue
                ? TimeSpan.FromSeconds(DefaultSubExposureSeconds.Value)
                : defaults.DefaultSubExposure,
            ConditionDeteriorationThreshold = ConditionDeteriorationThreshold ?? defaults.ConditionDeteriorationThreshold,
            ConditionRecoveryTimeout = ConditionRecoveryTimeoutMinutes.HasValue
                ? TimeSpan.FromMinutes(ConditionRecoveryTimeoutMinutes.Value)
                : defaults.ConditionRecoveryTimeout,
        };
    }

    public static SessionConfigApiDto FromConfiguration(in SessionConfiguration config)
    {
        return new SessionConfigApiDto
        {
            SetpointTemperature = config.SetpointCCDTemperature.TempC,
            CooldownRampSeconds = config.CooldownRampInterval.TotalSeconds,
            WarmupRampSeconds = config.WarmupRampInterval.TotalSeconds,
            MinHeightAboveHorizon = config.MinHeightAboveHorizon,
            DitherPixel = config.DitherPixel,
            SettlePixel = config.SettlePixel,
            DitherEveryNthFrame = config.DitherEveryNthFrame,
            SettleTimeSeconds = config.SettleTime.TotalSeconds,
            GuidingTries = config.GuidingTries,
            AutoFocusRange = config.AutoFocusRange,
            AutoFocusStepCount = config.AutoFocusStepCount,
            FocusDriftThreshold = config.FocusDriftThreshold,
            DefaultSubExposureSeconds = config.DefaultSubExposure?.TotalSeconds,
            ConditionDeteriorationThreshold = config.ConditionDeteriorationThreshold,
            ConditionRecoveryTimeoutMinutes = config.ConditionRecoveryTimeout?.TotalMinutes,
        };
    }
}
