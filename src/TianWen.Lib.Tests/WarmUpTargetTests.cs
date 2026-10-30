using Shouldly;
using System;
using System.Text.Json;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;
using TianWen.RemoteClient;
using TianWen.UI.Abstractions;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>The one warm-up target rule (#1842): heat sink, else a SETTLED pre-cool reading, else +25 C.</summary>
public class WarmUpTargetTests
{
    private static SensorAmbientReading Reading(double tempC, bool settled) => new(tempC, settled, DateTimeOffset.UnixEpoch);

    [Fact]
    public void TheHeatSinkBeatsASettledReading()
    {
        var target = WarmUpTarget.Resolve(18.0, Reading(12.0, settled: true));

        target.Source.ShouldBe(WarmUpTargetSource.HeatSink);
        target.TempC.ShouldBe(18.0);
    }

    [Fact]
    public void ASettledReadingIsTheTargetWithNoHeatSink()
    {
        var target = WarmUpTarget.Resolve(double.NaN, Reading(12.1, settled: true));

        target.Source.ShouldBe(WarmUpTargetSource.SettledSensorBeforeCooling);
        target.TempC.ShouldBe(12.1);
    }

    [Fact]
    public void AnUnsettledReadingNeverSetsTheTarget()
    {
        var target = WarmUpTarget.Resolve(double.NaN, Reading(12.1, settled: false));

        target.Source.ShouldBe(WarmUpTargetSource.Fallback);
        target.TempC.ShouldBe(CameraReading.WarmUpFallbackTargetC);
    }

    [Fact]
    public void NothingKnownFallsBackToTwentyFive()
    {
        var target = WarmUpTarget.Resolve(double.NaN, null);

        target.Source.ShouldBe(WarmUpTargetSource.Fallback);
        target.TempC.ShouldBe(25.0);
    }

    /// <summary>The quit question and the ramp ask the same rule: the 2026-10-27 QHYminiCam8M at 11.5 C needs no warm-up.</summary>
    [Fact]
    public void TheQuitQuestionAgreesWithTheRamp()
    {
        CameraReading.NeedsWarmUpFrom(coolerOn: true, 11.5, double.NaN, Reading(12.0, settled: true)).ShouldBeFalse();
        CameraReading.NeedsWarmUpFrom(coolerOn: true, 11.5, double.NaN, null).ShouldBeTrue();
        CameraReading.NeedsWarmUpFrom(coolerOn: true, 11.5, double.NaN, Reading(12.0, settled: false)).ShouldBeTrue();
    }

    // The 2026-10-27 case: a fan-cooled body, no heat sink, the sensor back at the air it started from.
    private static CameraReading FanCooled(double ccdC, SensorAmbientReading? preCool) => new CameraReading(
        ccdC, double.NaN, 10, 20, true, CameraState.Idle, true, false, 0, 100, 50, [], 1920, 1080, default) { PreCool = preCool };

    [Fact]
    public void ASettledReadingReachesTheDtoAndTheClientAgreesWithTheNodeThatNoWarmUpIsNeeded()
    {
        var reading = FanCooled(12.0, Reading(12.1, settled: true));

        var dto = CameraDeviceStateDto.FromReading(reading, intent: null);

        dto.WarmUpTargetC.ShouldBe(12.1);
        dto.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.SettledSensorBeforeCooling);
        reading.NeedsWarmUp.ShouldBeFalse("the node's ramp");
        dto.ToReading().NeedsWarmUp.ShouldBeFalse("the client's quit question");
        FanCooled(-5, Reading(12.1, settled: true)).NeedsWarmUp.ShouldBeTrue();
        CameraDeviceStateDto.FromReading(FanCooled(-5, Reading(12.1, settled: true)), intent: null).ToReading().NeedsWarmUp.ShouldBeTrue();
    }

    [Fact]
    public void TheTargetAndItsSourceSurviveTheWire()
    {
        var dto = CameraDeviceStateDto.FromReading(FanCooled(12.0, Reading(12.1, settled: true)), intent: null);

        var json = JsonSerializer.Serialize(dto, HostingJsonContext.Default.CameraDeviceStateDto);
        var read = JsonSerializer.Deserialize(json, HostingJsonContext.Default.CameraDeviceStateDto).ShouldNotBeNull();

        read.WarmUpTargetC.ShouldBe(12.1);
        read.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.SettledSensorBeforeCooling);
    }

    [Fact]
    public void AnOlderNodesCameraReadsAsTheFallback()
    {
        var read = JsonSerializer.Deserialize(
            """{"coolerOn":true,"state":0,"usesGainValue":false,"usesGainMode":false,"gainMin":0,"gainMax":0,"gain":0,"sensorWidth":1,"sensorHeight":1}""",
            HostingJsonContext.Default.CameraDeviceStateDto).ShouldNotBeNull();

        read.WarmUpTargetC.ShouldBeNull();
        read.WarmUpTargetSource.ShouldBe(WarmUpTargetSource.Fallback, "a property with a declared default is set, never init");
        read.ToReading().PreCool.ShouldBeNull();
    }

    [Fact]
    public void TheShutdownProgressNamesTheTargetAndWhereItCameFrom()
    {
        var dto = CameraDeviceStateDto.FromReading(FanCooled(-5, Reading(12.0, settled: true)), intent: null);

        RigShutdown.WarmingTo(dto).ShouldBe("to 12 C, the sensor before cooling");
    }
}
