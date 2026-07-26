using NSubstitute;
using Shouldly;
using System;
using System.Collections.Immutable;
using System.Text.Json;
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
/// Pins the one rule that every hosting DTO has to obey: <b>a non-finite double must never reach the
/// JSON writer.</b>
/// <para>
/// JSON cannot express NaN or Infinity and no context here enables
/// <c>AllowNamedFloatingPointLiterals</c>, so <c>Utf8JsonWriter</c> throws -- and because serialization
/// runs while the response is already streaming, the caller gets a <b>bodiless HTTP 500 for the whole
/// endpoint</b>. One unknown focuser temperature takes down the entire session-state payload.
/// </para>
/// <para>
/// NaN is the ordinary "not known" value across the domain (a pre-poll mount, a camera with no
/// temperature sensor, a weather station that does not measure rain), so these are the healthy paths,
/// not edge cases. Each test therefore feeds an <b>all-NaN</b> source through the real projection and
/// requires the result to serialize -- the shape of test that was missing when
/// <c>/v2/api/equipment/camera/info</c> shipped a 500 for every disconnected camera.
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

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void JsonNumberCoercesEveryNonFiniteValue(double value)
    {
        JsonNumber.Finite(value).ShouldBe(0.0);
        JsonNumber.Finite(value, fallback: -1.0).ShouldBe(-1.0);
        JsonNumber.Finite((float)value).ShouldBe(0f);
    }

    [Fact]
    public void JsonNumberLeavesFiniteValuesAlone()
    {
        JsonNumber.Finite(5.588).ShouldBe(5.588);
        JsonNumber.Finite(0.0).ShouldBe(0.0);
        JsonNumber.Finite(-273.15).ShouldBe(-273.15);
        JsonNumber.Finite(3.1f).ShouldBe(3.1f);
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

        json.ShouldNotContain("NaN");
        json.ShouldContain("\"rightAscension\":0");
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

        var dto = SessionStateDto.FromSession(session);

        var json = Should.NotThrow(() => SerializeV1(dto, HostingJsonContext.Default.SessionStateDto));
        json.ShouldNotContain("NaN");
        json.ShouldNotContain("Infinity");
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
        json.ShouldNotContain("NaN");
    }

    [Fact]
    public void NinaCameraDisconnectedSentinelSerializes()
    {
        var json = Should.NotThrow(() => SerializeNina(NinaCameraInfoDto.Disconnected, typeof(NinaCameraInfoDto)));
        json.ShouldNotContain("NaN");
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
        json.ShouldNotContain("NaN");
    }

    [Fact]
    public void NinaFocuserDisconnectedSentinelSerializes()
    {
        var json = Should.NotThrow(() => SerializeNina(NinaFocuserInfoDto.Disconnected, typeof(NinaFocuserInfoDto)));
        json.ShouldNotContain("NaN");
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
        json.ShouldNotContain("NaN");
    }

    [Fact]
    public void NinaWeatherDisconnectedSentinelSerializes()
    {
        var json = Should.NotThrow(() => SerializeNina(NinaWeatherInfoDto.Disconnected, typeof(NinaWeatherInfoDto)));
        json.ShouldNotContain("NaN");
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
        json.ShouldNotContain("NaN");
    }
}
