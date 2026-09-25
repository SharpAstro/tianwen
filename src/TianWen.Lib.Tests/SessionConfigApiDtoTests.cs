using System;
using System.Text.Json;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The whole session configuration crosses the wire (P0b item 10, #752): it used to carry 16 fields of 60,
/// so the site, the flip windows, the unattended prompt answer and 40 more could not be set over the API.
/// </summary>
public class SessionConfigApiDtoTests
{
    // Every field away from its default, so a field the DTO drops comes back as the default and fails.
    // A field added to SessionConfiguration is added here too.
    private static readonly SessionConfiguration EveryFieldSet = new SessionConfiguration(
        SetpointCCDTemperature: new SetpointTemp(-15, SetpointTempKind.CCD),
        CooldownRampInterval: TimeSpan.FromSeconds(301),
        WarmupRampInterval: TimeSpan.FromSeconds(302),
        MinHeightAboveHorizon: 25,
        DitherPixel: 6.5,
        SettlePixel: 1.5,
        DitherEveryNthFrame: 4,
        SettleTime: TimeSpan.FromSeconds(12),
        GuidingTries: 4,
        SiteLatitude: -37.8136,
        SiteLongitude: 144.9631,
        AutoFocusRange: 250,
        AutoFocusStepCount: 11,
        FocusDriftThreshold: 1.2f,
        MaxWaitForRisingTarget: TimeSpan.FromMinutes(20),
        AlwaysRefocusOnNewTarget: true,
        BaselineHfdFrameCount: 4,
        DefaultSubExposure: TimeSpan.FromSeconds(180),
        FocusFilterStrategy: FocusFilterStrategy.UseScheduledFilter,
        MosaicOverlap: 0.25,
        MosaicMargin: 0.15,
        ConditionDeteriorationThreshold: 0.6f,
        ConditionRecoveryTimeout: TimeSpan.FromMinutes(30),
        WarmCamerasOnSessionEnd: false,
        DeviceFaultEscalationThreshold: 6,
        DeviceFaultDecayFrames: 12,
        ScoutExposure: TimeSpan.FromSeconds(8),
        ObstructionStarCountRatioHealthy: 0.75f,
        ObstructionStarCountRatioSevere: 0.35f,
        ObstructionNudgeRadii: 1.5f,
        ObstructionClearFractionOfRemaining: 0.25f,
        MeridianFlipObstructionZoneMinutesBefore: 3,
        MeridianFlipEarliestMinutesAfter: 6,
        MeridianFlipLatestMinutesAfter: 12,
        GuiderRecoveryGrace: TimeSpan.FromMinutes(4),
        ScheduledStartLeadTime: TimeSpan.FromMinutes(5),
        FirstScoutOracleEnabled: false,
        OracleFactor: 0.5f,
        MinOracleStarCount: 12,
        CloudGateEfficiencyFloor: 0.2f,
        TakeFlatsOnSessionEnd: true,
        UnattendedPromptResponse: UnattendedPromptResponse.Proceed,
        FlatTargetAduFraction: 0.45,
        FlatAduTolerance: 0.06,
        FlatMaxBrackets: 7,
        FlatsPerFilter: 20,
        FlatInitialExposure: TimeSpan.FromSeconds(2),
        FlatMinExposure: TimeSpan.FromSeconds(0.5),
        FlatMaxExposure: TimeSpan.FromSeconds(10),
        FlatCalibratorBrightnessPercent: 60,
        FlatSource: FlatIlluminationSource.TwilightSky,
        TakeSkyFlatsAtDusk: true,
        FlatSkyMeridianTilt: TimeSpan.FromMinutes(40),
        FlatSkyMaxDuration: TimeSpan.FromMinutes(20),
        FlatSkySettleInterval: TimeSpan.FromSeconds(15),
        FlatSkySunAltitudeBrightDeg: -2,
        FlatSkySunAltitudeDarkDeg: -12,
        FocusDriftSampleSize: 40,
        FocusDriftMinSamples: 6,
        SaveIntermediates: true);

    [Fact]
    public void EveryFieldSurvivesTheWire()
    {
        var json = JsonSerializer.Serialize(SessionConfigApiDto.FromConfiguration(EveryFieldSet), HostingJsonContext.Default.SessionConfigApiDto);
        var back = JsonSerializer.Deserialize(json, HostingJsonContext.Default.SessionConfigApiDto).ShouldNotBeNull();

        back.ToConfiguration().ShouldBe(EveryFieldSet);
    }

    [Fact]
    public void AnEmptyBodyIsTheDeclaredDefaults()
    {
        var empty = JsonSerializer.Deserialize("{}", HostingJsonContext.Default.SessionConfigApiDto).ShouldNotBeNull();

        empty.ToConfiguration().ShouldBe(new SessionConfiguration());
    }

    [Fact]
    public void AnUnsetSiteTravelsAsAbsentNotAsANaN()
    {
        // A NaN is not JSON: an unset site must serialise, and come back unset.
        var json = JsonSerializer.Serialize(SessionConfigApiDto.FromConfiguration(new SessionConfiguration()), HostingJsonContext.Default.SessionConfigApiDto);
        var back = JsonSerializer.Deserialize(json, HostingJsonContext.Default.SessionConfigApiDto).ShouldNotBeNull().ToConfiguration();

        double.IsNaN(back.SiteLatitude).ShouldBeTrue();
        double.IsNaN(back.SiteLongitude).ShouldBeTrue();
    }
}
