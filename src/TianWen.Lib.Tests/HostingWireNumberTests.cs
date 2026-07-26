using NSubstitute;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.Dto.NinaV2;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.Weather;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the rule every hosting DTO has to obey: <b>a value the JSON contract cannot represent must
/// never reach the writer.</b>
/// <para>
/// The contract decides, not the DTO -- <see cref="JsonNumber.WireAllowsNonFinite"/> is derived from the
/// serializer options, so these tests assert the <i>policy</i> and the <i>behaviour under it</i>
/// separately. Under the current strict contract <c>Utf8JsonWriter</c> throws on NaN or Infinity, and
/// because serialization runs while the response is already streaming, the caller gets a <b>bodiless
/// HTTP 500 for the whole endpoint</b>: one unknown focuser temperature takes down the entire
/// session-state payload.
/// </para>
/// <para>
/// NaN is the ordinary "not known" value across the domain (a pre-poll mount, a camera with no
/// temperature sensor, a weather station that does not measure rain), so these are the healthy paths,
/// not edge cases. Each test therefore feeds an <b>all-NaN</b> source through the real projection and
/// the real <c>JsonSerializerContext</c>, and requires the result to serialize -- the shape of test that
/// was missing when <c>/v2/api/equipment/camera/info</c> shipped a 500 for every disconnected camera.
/// </para>
/// </summary>
public class HostingWireNumberTests
{
    private static string SerializeV1<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Serialize(value, typeInfo);

    // Serializes through the REAL NinaApiJsonContext (internal to TianWen.Hosting, reachable via
    // InternalsVisibleTo). Duplicating its options here would let a test pass while the actual
    // endpoint still throws.
    private static string SerializeNina(object value, Type type) =>
        JsonSerializer.Serialize(value, type, NinaApiJsonContext.Default);

    /// <summary>
    /// The invariant every payload must hold: it serialized at all. Absence of a <c>NaN</c> token is
    /// only implied by a STRICT contract -- if the policy is ever flipped to allow named literals, a NaN
    /// in the payload is correct, and asserting it away here would fail a legitimate contract change for
    /// the wrong reason.
    /// </summary>
    private static void ShouldHonourWirePolicy(string json)
    {
        if (!JsonNumber.WireAllowsNonFinite)
        {
            json.ShouldNotContain("NaN");
            json.ShouldNotContain("Infinity");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The policy itself
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheWireContractDoesNotCarryNonFiniteNumbers()
    {
        // Deliberate: AllowNamedFloatingPointLiterals would emit the non-standard "NaN" token, which
        // real nina clients do not parse. This asserts the CURRENT contract, and is meant to fail if
        // someone flips it -- at which point the substitutions below stop happening on their own and
        // the wire starts carrying NaN, which is a contract change to make knowingly.
        JsonNumber.WireAllowsNonFinite.ShouldBeFalse();
        HostingJsonContext.Default.Options.NumberHandling
            .HasFlag(JsonNumberHandling.AllowNamedFloatingPointLiterals).ShouldBeFalse();
    }

    [Fact]
    public void BothWireContextsAgreeOnNumberHandling()
    {
        // JsonNumber derives its policy from HostingJsonContext because that is the context in its own
        // assembly. The nina shim serializes the same coerced values through a DIFFERENT context, so
        // the two must agree or the policy would be describing only half the surface.
        NinaApiJsonContext.Default.Options.NumberHandling
            .ShouldBe(HostingJsonContext.Default.Options.NumberHandling);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ForWireFollowsWhicheverPolicyTheContractDeclares(double value)
    {
        if (JsonNumber.WireAllowsNonFinite)
        {
            // The contract can carry it, so substituting would destroy information for no reason:
            // "not known" must stay distinguishable from a real reading of 0.
            JsonNumber.ForWire(value).ShouldBe(value);
            JsonNumber.ForWire((float)value).ShouldBe((float)value);
            double.IsNaN(JsonNumber.Unknown).ShouldBeTrue();
        }
        else
        {
            JsonNumber.ForWire(value).ShouldBe(0.0);
            JsonNumber.ForWire(value, fallback: -1.0).ShouldBe(-1.0);
            JsonNumber.ForWire((float)value).ShouldBe(0f);
            JsonNumber.Unknown.ShouldBe(0.0);
        }
    }

    [Fact]
    public void FiniteValuesArePassedThroughUnchangedWhateverThePolicy()
    {
        JsonNumber.ForWire(5.588).ShouldBe(5.588);
        JsonNumber.ForWire(0.0).ShouldBe(0.0);
        JsonNumber.ForWire(-273.15).ShouldBe(-273.15);
        JsonNumber.ForWire(3.1f).ShouldBe(3.1f);
    }

    // ---------------------------------------------------------------------------------------------
    // Native v1
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void MountStateSerializesBeforeTheFirstPoll()
    {
        // MountState is all-NaN until the session's first PollDeviceStatesAsync.
        var dto = MountStateDto.FromState(default);

        var json = SerializeV1(dto, HostingJsonContext.Default.MountStateDto);

        ShouldHonourWirePolicy(json);
    }

    [Fact]
    public void SessionStateSerializesWithEveryNumericUnknown()
    {
        // The worst realistic case: no focuser fitted (NaN temperature), no frame measured yet (NaN
        // HFD/FWHM), mount not yet polled, a guider that exists but has folded in no samples, and a
        // target whose coordinates are unknown.
        var session = Substitute.For<ISessionTelemetry>();
        session.Phase.Returns(SessionPhase.Cooling);
        session.MountDisplayName.Returns("Mount");
        session.MountState.Returns(default(MountState));
        session.TelescopeDisplays.Returns([new TelescopeDisplayInfo("Cam", HasFocuser: false, HasFilterWheel: false)]);
        session.CameraStates.Returns(
        [
            new CameraExposureState(0, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(60), 0, "L", 0,
                CameraState.Idle, FocuserTemperature: double.NaN, FocuserIsMoving: false),
        ]);
        session.LastFrameMetrics.Returns([new FrameMetrics(0, float.NaN, float.NaN, TimeSpan.Zero, 0)]);
        session.LastGuideStats.Returns(GuideStats.FromRms(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN));
        session.GuideSamples.Returns(
            [new GuideErrorSample(DateTimeOffset.UnixEpoch, double.NaN, double.NaN, double.NaN, double.NaN)]);
        session.Observations.Returns(new ScheduledObservationTree(
        [
            new ScheduledObservation(new Target(double.NaN, double.NaN, "Unknown", null),
                DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(30), false, [], null, null),
        ]));
        session.PhaseTimeline.Returns([]);

        // The deep telemetry added for a remote V-curve / cooling chart carries its own unknowns: a
        // cooling probe that reports no power reading, an autofocus that recorded no hyperbola fit, and
        // a frame whose HFD could not be measured. Each is a double/float straight onto the wire.
        session.CoolingSamples.Returns(
        [
            new CoolingSample(DateTimeOffset.UnixEpoch, 0, double.NaN, double.NaN, double.NaN),
        ]);
        session.FocusHistory.Returns(
        [
            new FocusRunRecord(DateTimeOffset.UnixEpoch, "OTA", "L", 0, float.NaN,
                [(0, float.NaN)], FitA: double.NaN, FitB: double.NaN),
        ]);
        session.ActiveFocusSamples.Returns([(0, float.NaN)]);
        session.ExposureLog.Returns(
        [
            new ExposureLogEntry(DateTimeOffset.UnixEpoch, "Unknown", "L", TimeSpan.FromSeconds(60), 1, float.NaN, 0),
        ]);

        var dto = SessionStateDto.FromSession(session);

        var json = Should.NotThrow(() => SerializeV1(dto, HostingJsonContext.Default.SessionStateDto));
        ShouldHonourWirePolicy(json);
    }

    [Fact]
    public void ProfileDetailSerializesForAProfileThatDefersItsSiteToTheMount()
    {
        // SiteLatitude/Longitude/Elevation are nullable on ProfileData: a profile that lets the mount
        // supply the site leaves all three null, and they must stay omitted rather than becoming 0
        // (which would read as "on the equator at the prime meridian" to a client computing alt/az).
        var dto = ProfileDetailDto.FromProfile(new Profile(Guid.NewGuid(), "Rig", ProfileData.Empty));

        dto.SiteLatitude.ShouldBeNull();
        dto.SiteLongitude.ShouldBeNull();
        dto.SiteElevation.ShouldBeNull();

        var json = Should.NotThrow(() => SerializeV1(dto, HostingJsonContext.Default.ProfileDetailDto));
        ShouldHonourWirePolicy(json);
        json.ShouldNotContain("siteLatitude");
    }

    // ---------------------------------------------------------------------------------------------
    // ninaAPI v2 shim
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NinaCameraInfoSerializesForACameraWithNoTemperatureSensor()
    {
        // The exact 500 found by the AOT publish smoke test: no camera connected / no sensor.
        var driver = Substitute.For<ICameraDriver>();
        driver.Connected.Returns(true);
        driver.Name.Returns("Cam");
        driver.CanGetCCDTemperature.Returns(false);
        driver.CanSetCCDTemperature.Returns(false);
        driver.CanGetCoolerPower.Returns(false);
        driver.GetCameraStateAsync(Arg.Any<CancellationToken>()).Returns(CameraState.Idle);

        var dto = await NinaCameraInfoDto.FromDriverAsync(driver, TestContext.Current.CancellationToken);

        var json = Should.NotThrow(() => SerializeNina(dto, typeof(NinaCameraInfoDto)));
        ShouldHonourWirePolicy(json);
    }

    [Fact]
    public void NinaCameraDisconnectedSentinelSerializes()
    {
        var json = Should.NotThrow(() => SerializeNina(NinaCameraInfoDto.Disconnected, typeof(NinaCameraInfoDto)));
        ShouldHonourWirePolicy(json);
    }

    [Fact]
    public async Task NinaFocuserInfoSerializesWithoutATemperatureProbe()
    {
        var driver = Substitute.For<IFocuserDriver>();
        driver.Connected.Returns(true);
        driver.Name.Returns("Foc");
        driver.GetPositionAsync(Arg.Any<CancellationToken>()).Returns(980);
        driver.GetIsMovingAsync(Arg.Any<CancellationToken>()).Returns(false);
        driver.GetTemperatureAsync(Arg.Any<CancellationToken>()).Returns(double.NaN);

        var dto = await NinaFocuserInfoDto.FromDriverAsync(driver, TestContext.Current.CancellationToken);

        var json = Should.NotThrow(() => SerializeNina(dto, typeof(NinaFocuserInfoDto)));
        ShouldHonourWirePolicy(json);
    }

    [Fact]
    public void NinaFocuserDisconnectedSentinelSerializes()
    {
        var json = Should.NotThrow(() => SerializeNina(NinaFocuserInfoDto.Disconnected, typeof(NinaFocuserInfoDto)));
        ShouldHonourWirePolicy(json);
    }

    [Fact]
    public void NinaWeatherInfoSerializesWhenTheStationReportsNothing()
    {
        // Most real stations measure only a handful of these; the rest come back NaN.
        var driver = Substitute.For<IWeatherDriver>();
        driver.Connected.Returns(true);
        driver.CloudCover.Returns(double.NaN);
        driver.DewPoint.Returns(double.NaN);
        driver.Humidity.Returns(double.NaN);
        driver.Pressure.Returns(double.NaN);
        driver.RainRate.Returns(double.NaN);
        driver.SkyQuality.Returns(double.NaN);
        driver.SkyTemperature.Returns(double.NaN);
        driver.StarFWHM.Returns(double.NaN);
        driver.Temperature.Returns(double.NaN);
        driver.WindDirection.Returns(double.NaN);
        driver.WindGust.Returns(double.NaN);
        driver.WindSpeed.Returns(double.NaN);

        var dto = NinaWeatherInfoDto.FromDriver(driver);

        var json = Should.NotThrow(() => SerializeNina(dto, typeof(NinaWeatherInfoDto)));
        ShouldHonourWirePolicy(json);
    }

    [Fact]
    public void NinaWeatherDisconnectedSentinelSerializes()
    {
        var json = Should.NotThrow(() => SerializeNina(NinaWeatherInfoDto.Disconnected, typeof(NinaWeatherInfoDto)));
        ShouldHonourWirePolicy(json);
    }

    [Fact]
    public async Task NinaMountInfoSerializesBeforeTheFirstPoll()
    {
        var driver = Substitute.For<IMountDriver>();
        driver.Connected.Returns(true);
        driver.Name.Returns("Mount");
        driver.GetTrackingSpeedAsync(Arg.Any<CancellationToken>()).Returns(TrackingSpeed.Sidereal);
        driver.AtParkAsync(Arg.Any<CancellationToken>()).Returns(false);
        driver.AtHomeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var dto = await NinaMountInfoDto.FromDriverAsync(driver, default, TestContext.Current.CancellationToken);

        var json = Should.NotThrow(() => SerializeNina(dto, typeof(NinaMountInfoDto)));
        ShouldHonourWirePolicy(json);
    }
}
