using Shouldly;
using System;
using TianWen.Lib.Devices;
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
}
