using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.UI.Abstractions;

/// <summary>
/// Persists and restores session configuration state keyed by profile.
/// Files stored under {AppDataFolder}/Session/{profileId}.json.
/// </summary>
public static class SessionPersistence
{
    /// <summary>
    /// Saves the current session configuration to disk.
    /// </summary>
    public static Task SaveAsync(SessionTabState state, Profile profile, IExternal external, CancellationToken ct)
        => external.AtomicWriteJsonAsync(
            GetSessionFilePath(profile, external),
            CreateDto(state, profile),
            SessionConfigJsonContext.Default.SessionConfigDto,
            ct);

    /// <summary>
    /// Attempts to load a previously saved session configuration. Returns true if state was restored.
    /// </summary>
    /// <summary>
    /// Attempts to load a previously saved session configuration. Returns true if state was restored.
    /// Per-OTA camera settings are only restored if the camera URI matches (prevents applying
    /// gain/offset/setpoint from a different camera).
    /// </summary>
    public static async Task<bool> TryLoadAsync(SessionTabState state, Profile? profile, IExternal external, CancellationToken ct, IDeviceUriRegistry? registry = null)
    {
        if (profile is null)
        {
            return false;
        }

        // Ensure per-OTA camera settings are populated before restoring saved values
        state.InitializeFromProfile(profile, registry);

        var dto = await external.TryReadJsonAsync(
            GetSessionFilePath(profile, external),
            SessionConfigJsonContext.Default.SessionConfigDto, logger: null, ct);

        if (dto is null)
        {
            return false;
        }

        // Restore configuration
        state.Configuration = new SessionConfiguration(
            SetpointCCDTemperature: new SetpointTemp(dto.SetpointTempC, dto.SetpointTempKind),
            CooldownRampInterval: TimeSpan.FromSeconds(dto.CooldownRampSeconds),
            WarmupRampInterval: TimeSpan.FromSeconds(dto.WarmupRampSeconds),
            MinHeightAboveHorizon: dto.MinHeightAboveHorizon,
            DitherPixel: dto.DitherPixel,
            SettlePixel: dto.SettlePixel,
            DitherEveryNthFrame: dto.DitherEveryNthFrame,
            SettleTime: TimeSpan.FromSeconds(dto.SettleTimeSeconds),
            GuidingTries: dto.GuidingTries,
            MeasureBacklashIfUnknown: dto.MeasureBacklashIfUnknown,
            AutoFocusRange: dto.AutoFocusRange,
            AutoFocusStepCount: dto.AutoFocusStepCount,
            FocusDriftThreshold: dto.FocusDriftThreshold,
            MaxWaitForRisingTarget: dto.MaxWaitForRisingTargetMinutes.HasValue
                ? TimeSpan.FromMinutes(dto.MaxWaitForRisingTargetMinutes.Value)
                : null,
            AlwaysRefocusOnNewTarget: dto.AlwaysRefocusOnNewTarget,
            BaselineHfdFrameCount: dto.BaselineHfdFrameCount,
            DefaultSubExposure: dto.DefaultSubExposureSeconds.HasValue
                ? TimeSpan.FromSeconds(dto.DefaultSubExposureSeconds.Value)
                : null,
            FocusFilterStrategy: dto.FocusFilterStrategy,
            MosaicOverlap: dto.MosaicOverlap,
            MosaicMargin: dto.MosaicMargin,
            ConditionDeteriorationThreshold: dto.ConditionDeteriorationThreshold,
            ConditionRecoveryTimeout: dto.ConditionRecoveryTimeoutMinutes.HasValue
                ? TimeSpan.FromMinutes(dto.ConditionRecoveryTimeoutMinutes.Value)
                : null);

        // Restore per-OTA camera settings: only if OTA count matches and camera URI matches per slot
        var otas = profile.Data?.OTAs ?? [];
        if (dto.CameraSettings is { Length: > 0 }
            && dto.CameraSettings.Length == state.CameraSettings.Count
            && dto.CameraSettings.Length == otas.Length)
        {
            for (var i = 0; i < dto.CameraSettings.Length; i++)
            {
                var saved = dto.CameraSettings[i];
                // Skip this OTA if the camera changed
                if (saved.CameraUri is not null && saved.CameraUri != otas[i].Camera)
                {
                    continue;
                }

                state.CameraSettings[i].SetpointTempC = saved.SetpointTempC;
                state.CameraSettings[i].Gain = saved.Gain;
                state.CameraSettings[i].Offset = saved.Offset;
            }
        }

        state.NeedsRedraw = true;
        return true;
    }

    private static SessionConfigDto CreateDto(SessionTabState state, Profile profile)
    {
        var config = state.Configuration;
        var otas = profile.Data?.OTAs ?? [];
        var cameraSettings = new PerOtaCameraSettingsDto[state.CameraSettings.Count];
        for (var i = 0; i < state.CameraSettings.Count; i++)
        {
            var cam = state.CameraSettings[i];
            var cameraUri = i < otas.Length ? otas[i].Camera : null;
            cameraSettings[i] = new PerOtaCameraSettingsDto(cam.SetpointTempC, cam.Gain, cam.Offset, cameraUri);
        }

        return new SessionConfigDto(
            SetpointTempC: config.SetpointCCDTemperature.TempC,
            SetpointTempKind: config.SetpointCCDTemperature.Kind,
            CooldownRampSeconds: config.CooldownRampInterval.TotalSeconds,
            WarmupRampSeconds: config.WarmupRampInterval.TotalSeconds,
            MinHeightAboveHorizon: config.MinHeightAboveHorizon,
            DitherPixel: config.DitherPixel,
            SettlePixel: config.SettlePixel,
            DitherEveryNthFrame: config.DitherEveryNthFrame,
            SettleTimeSeconds: config.SettleTime.TotalSeconds,
            GuidingTries: config.GuidingTries,
            MeasureBacklashIfUnknown: config.MeasureBacklashIfUnknown,
            AutoFocusRange: config.AutoFocusRange,
            AutoFocusStepCount: config.AutoFocusStepCount,
            FocusDriftThreshold: config.FocusDriftThreshold,
            MaxWaitForRisingTargetMinutes: config.MaxWaitForRisingTarget?.TotalMinutes,
            AlwaysRefocusOnNewTarget: config.AlwaysRefocusOnNewTarget,
            BaselineHfdFrameCount: config.BaselineHfdFrameCount,
            DefaultSubExposureSeconds: config.DefaultSubExposure?.TotalSeconds,
            FocusFilterStrategy: config.FocusFilterStrategy,
            MosaicOverlap: config.MosaicOverlap,
            MosaicMargin: config.MosaicMargin,
            ConditionDeteriorationThreshold: config.ConditionDeteriorationThreshold,
            ConditionRecoveryTimeoutMinutes: config.ConditionRecoveryTimeout?.TotalMinutes,
            CameraSettings: cameraSettings);
    }

    private static string GetSessionFilePath(Profile profile, IExternal external)
    {
        var profileId = profile.ProfileId.ToString("D");
        return Path.Combine(external.AppDataFolder.FullName, "Session", profileId + ".json");
    }
}

/// <summary>DTO for a saved session configuration.</summary>
public record SessionConfigDto(
    sbyte SetpointTempC,
    SetpointTempKind SetpointTempKind,
    double CooldownRampSeconds,
    double WarmupRampSeconds,
    byte MinHeightAboveHorizon,
    double DitherPixel,
    double SettlePixel,
    int DitherEveryNthFrame,
    double SettleTimeSeconds,
    int GuidingTries,
    bool MeasureBacklashIfUnknown,
    int AutoFocusRange,
    int AutoFocusStepCount,
    float FocusDriftThreshold,
    double? MaxWaitForRisingTargetMinutes,
    bool AlwaysRefocusOnNewTarget,
    int BaselineHfdFrameCount,
    double? DefaultSubExposureSeconds,
    FocusFilterStrategy FocusFilterStrategy,
    double MosaicOverlap,
    double MosaicMargin,
    float ConditionDeteriorationThreshold,
    double? ConditionRecoveryTimeoutMinutes,
    PerOtaCameraSettingsDto[] CameraSettings);

/// <summary>DTO for per-OTA camera settings.</summary>
public record PerOtaCameraSettingsDto(sbyte SetpointTempC, int Gain, int Offset, Uri? CameraUri);

[JsonSerializable(typeof(SessionConfigDto))]
internal partial class SessionConfigJsonContext : JsonSerializerContext
{
}
