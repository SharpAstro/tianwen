using System;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

[Collection("Encoding")]
public class SessionPersistenceTests(ITestOutputHelper output)
{
    private static Profile CreateTestProfile(int otaCount = 1, Uri? cameraUri = null)
    {
        var otas = new OTAData[otaCount];
        for (var i = 0; i < otaCount; i++)
        {
            otas[i] = new OTAData(
                Name: $"Telescope #{i}",
                FocalLength: 800,
                Camera: cameraUri ?? new Uri($"Camera://FakeDevice/{i + 1}#Fake Camera {i + 1}"),
                Cover: null,
                Focuser: null,
                FilterWheel: null,
                PreferOutwardFocus: null,
                OutwardIsPositive: null,
                Aperture: 100
            );
        }

        return new Profile(Guid.NewGuid(), "Test Profile", new ProfileData(
            Mount: NoneDevice.Instance.DeviceUri,
            Guider: NoneDevice.Instance.DeviceUri,
            OTAs: [.. otas]
        ));
    }

    [Fact]
    public async Task GivenNonDefaultConfigWhenSaveAndLoadThenAllScalarFieldsRestored()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var profile = CreateTestProfile();

        var original = new SessionTabState
        {
            Configuration = new SessionConfiguration(
                SetpointCCDTemperature: new SetpointTemp(-20, SetpointTempKind.Normal),
                CooldownRampInterval: TimeSpan.FromMinutes(8),
                WarmupRampInterval: TimeSpan.FromMinutes(12),
                MinHeightAboveHorizon: 25,
                DitherPixel: 3.5,
                SettlePixel: 0.8,
                DitherEveryNthFrame: 7,
                SettleTime: TimeSpan.FromSeconds(15),
                GuidingTries: 5,
                AutoFocusRange: 300,
                AutoFocusStepCount: 11,
                FocusDriftThreshold: 1.15f,
                MaxWaitForRisingTarget: TimeSpan.FromMinutes(30),
                AlwaysRefocusOnNewTarget: true,
                BaselineHfdFrameCount: 5,
                DefaultSubExposure: TimeSpan.FromSeconds(180),
                FocusFilterStrategy: FocusFilterStrategy.UseLuminance,
                MosaicOverlap: 0.3,
                MosaicMargin: 0.15,
                ConditionDeteriorationThreshold: 0.7f,
                ConditionRecoveryTimeout: TimeSpan.FromMinutes(20))
        };
        original.InitializeFromProfile(profile);

        await SessionPersistence.SaveAsync(original, profile, external, ct);

        // Load into fresh state
        var restored = new SessionTabState();
        var loaded = await SessionPersistence.TryLoadAsync(restored, profile, external, ct);

        loaded.ShouldBeTrue();
        var c = restored.Configuration;
        c.SetpointCCDTemperature.TempC.ShouldBe((sbyte)-20);
        c.CooldownRampInterval.ShouldBe(TimeSpan.FromMinutes(8));
        c.WarmupRampInterval.ShouldBe(TimeSpan.FromMinutes(12));
        c.MinHeightAboveHorizon.ShouldBe((byte)25);
        c.DitherPixel.ShouldBe(3.5);
        c.SettlePixel.ShouldBe(0.8);
        c.DitherEveryNthFrame.ShouldBe(7);
        c.SettleTime.ShouldBe(TimeSpan.FromSeconds(15));
        c.GuidingTries.ShouldBe(5);
        c.AutoFocusRange.ShouldBe(300);
        c.AutoFocusStepCount.ShouldBe(11);
        c.FocusDriftThreshold.ShouldBe(1.15f);
        c.MaxWaitForRisingTarget.ShouldBe(TimeSpan.FromMinutes(30));
        c.AlwaysRefocusOnNewTarget.ShouldBeTrue();
        c.BaselineHfdFrameCount.ShouldBe(5);
        c.DefaultSubExposure.ShouldBe(TimeSpan.FromSeconds(180));
        c.FocusFilterStrategy.ShouldBe(FocusFilterStrategy.UseLuminance);
        c.MosaicOverlap.ShouldBe(0.3);
        c.MosaicMargin.ShouldBe(0.15);
        c.ConditionDeteriorationThreshold.ShouldBe(0.7f);
        c.ConditionRecoveryTimeout.ShouldBe(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public async Task GivenPerOtaSetpointWhenSaveAndLoadThenSetpointRestored()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var profile = CreateTestProfile();

        var state = new SessionTabState();
        state.InitializeFromProfile(profile);
        state.CameraSettings[0].SetpointTempC = -25;
        state.CameraSettings[0].Gain = 200;
        state.CameraSettings[0].Offset = 30;

        await SessionPersistence.SaveAsync(state, profile, external, ct);

        var restored = new SessionTabState();
        var loaded = await SessionPersistence.TryLoadAsync(restored, profile, external, ct);

        loaded.ShouldBeTrue();
        restored.CameraSettings.Count.ShouldBe(1);
        restored.CameraSettings[0].SetpointTempC.ShouldBe((sbyte)-25);
        restored.CameraSettings[0].Gain.ShouldBe(200);
        restored.CameraSettings[0].Offset.ShouldBe(30);
    }

    [Fact]
    public async Task GivenNullProfileWhenTryLoadThenReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var state = new SessionTabState();

        var loaded = await SessionPersistence.TryLoadAsync(state, null, external, ct);

        loaded.ShouldBeFalse();
    }

    [Fact]
    public async Task GivenNoSavedFileWhenTryLoadThenReturnsFalseButInitializesFromProfile()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var profile = CreateTestProfile(otaCount: 2);
        var state = new SessionTabState();

        var loaded = await SessionPersistence.TryLoadAsync(state, profile, external, ct);

        loaded.ShouldBeFalse();
        // InitializeFromProfile should still have been called
        state.CameraSettings.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GivenOtaCountMismatchWhenLoadThenPerOtaSettingsNotRestored()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var profile1Ota = CreateTestProfile(otaCount: 1);

        // Save with 1 OTA
        var state = new SessionTabState();
        state.InitializeFromProfile(profile1Ota);
        state.CameraSettings[0].SetpointTempC = -30;
        await SessionPersistence.SaveAsync(state, profile1Ota, external, ct);

        // Load with a profile that has 2 OTAs but same profile ID
        var profile2Otas = new Profile(profile1Ota.ProfileId, "Test Profile", new ProfileData(
            Mount: NoneDevice.Instance.DeviceUri,
            Guider: NoneDevice.Instance.DeviceUri,
            OTAs: [
                new OTAData("Scope 1", 800, new Uri("Camera://FakeDevice/1#Cam1"), null, null, null, null, null, 100),
                new OTAData("Scope 2", 400, new Uri("Camera://FakeDevice/2#Cam2"), null, null, null, null, null, 60)
            ]
        ));

        var restored = new SessionTabState();
        var loaded = await SessionPersistence.TryLoadAsync(restored, profile2Otas, external, ct);

        loaded.ShouldBeTrue(); // config fields still restored
        restored.CameraSettings.Count.ShouldBe(2);
        // Per-OTA settings should be defaults since OTA count mismatched
        restored.CameraSettings[0].SetpointTempC.ShouldBe((sbyte)-10);
        restored.CameraSettings[1].SetpointTempC.ShouldBe((sbyte)-10);
    }

    [Fact]
    public async Task GivenCameraUriChangedWhenLoadThenThatOtaSkipped()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var cameraUri1 = new Uri("Camera://FakeDevice/1#Cam1");
        var profile = CreateTestProfile(cameraUri: cameraUri1);

        var state = new SessionTabState();
        state.InitializeFromProfile(profile);
        state.CameraSettings[0].SetpointTempC = -25;
        await SessionPersistence.SaveAsync(state, profile, external, ct);

        // Change the camera URI in the profile (same profile ID)
        var cameraUri2 = new Uri("Camera://FakeDevice/2#Cam2");
        var updatedProfile = new Profile(profile.ProfileId, "Test Profile", new ProfileData(
            Mount: NoneDevice.Instance.DeviceUri,
            Guider: NoneDevice.Instance.DeviceUri,
            OTAs: [new OTAData("Telescope #0", 800, cameraUri2, null, null, null, null, null, 100)]
        ));

        var restored = new SessionTabState();
        var loaded = await SessionPersistence.TryLoadAsync(restored, updatedProfile, external, ct);

        loaded.ShouldBeTrue();
        // Setpoint should be the default since camera changed
        restored.CameraSettings[0].SetpointTempC.ShouldBe((sbyte)-10);
    }

    [Fact]
    public async Task GivenNullableTimeSpanFieldsNullWhenRoundTripThenRemainNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var profile = CreateTestProfile();

        var state = new SessionTabState
        {
            Configuration = SessionTabState.DefaultConfiguration with
            {
                MaxWaitForRisingTarget = null,
                DefaultSubExposure = null,
                ConditionRecoveryTimeout = null
            }
        };
        state.InitializeFromProfile(profile);
        await SessionPersistence.SaveAsync(state, profile, external, ct);

        var restored = new SessionTabState();
        var loaded = await SessionPersistence.TryLoadAsync(restored, profile, external, ct);

        loaded.ShouldBeTrue();
        restored.Configuration.MaxWaitForRisingTarget.ShouldBeNull();
        restored.Configuration.DefaultSubExposure.ShouldBeNull();
        restored.Configuration.ConditionRecoveryTimeout.ShouldBeNull();
    }

    [Fact]
    public async Task GivenSiteLatLonWhenSaveAndLoadThenNotPersisted()
    {
        var ct = TestContext.Current.CancellationToken;
        var external = new FakeExternal(output);
        var profile = CreateTestProfile();

        var state = new SessionTabState
        {
            Configuration = SessionTabState.DefaultConfiguration with
            {
                SiteLatitude = 48.2,
                SiteLongitude = 16.3
            }
        };
        state.InitializeFromProfile(profile);
        await SessionPersistence.SaveAsync(state, profile, external, ct);

        var restored = new SessionTabState();
        await SessionPersistence.TryLoadAsync(restored, profile, external, ct);

        // Site coordinates are injected at start time, not persisted
        double.IsNaN(restored.Configuration.SiteLatitude).ShouldBeTrue();
        double.IsNaN(restored.Configuration.SiteLongitude).ShouldBeTrue();
    }
}
