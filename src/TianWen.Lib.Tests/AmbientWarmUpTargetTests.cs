using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests;

public class AmbientWarmUpTargetTests
{
    [Fact]
    public void TheHeatSinkWinsOverTheRigAmbient()
        => WarmUpTarget.Resolve(18, null, 10).ShouldBe(new WarmUpTarget(18, WarmUpTargetSource.HeatSink));

    [Fact]
    public void TheRigAmbientIsTheTargetWhenTheCameraHasNoHeatSink()
        => WarmUpTarget.Resolve(double.NaN, null, 10).ShouldBe(new WarmUpTarget(10, WarmUpTargetSource.RigAmbient));

    [Fact]
    public void TheRigAmbientBeatsTheSettledSensor()
        => WarmUpTarget.Resolve(double.NaN, new SensorAmbientReading(14, true, DateTimeOffset.UnixEpoch), 10).Source.ShouldBe(WarmUpTargetSource.RigAmbient);

    [Fact]
    public void TheSettledSensorIsTheTargetWhenTheRigGivesNoAmbient()
        => WarmUpTarget.Resolve(double.NaN, new SensorAmbientReading(14, true, DateTimeOffset.UnixEpoch), double.NaN)
            .ShouldBe(new WarmUpTarget(14, WarmUpTargetSource.SettledSensorBeforeCooling));

    [Fact]
    public void AnUnsettledSensorReadingNeverSetsTheTarget()
        => WarmUpTarget.Resolve(double.NaN, new SensorAmbientReading(14, false, DateTimeOffset.UnixEpoch), double.NaN).Source.ShouldBe(WarmUpTargetSource.Fallback);

    [Fact]
    public void TheFallbackIsTheTargetWhenNeitherGivesOne()
    {
        WarmUpTarget.Resolve(double.NaN, null, double.NaN).ShouldBe(new WarmUpTarget(CameraReading.WarmUpFallbackTargetC, WarmUpTargetSource.Fallback));
        WarmUpTarget.Resolve(double.NaN, null).ShouldBe(new WarmUpTarget(CameraReading.WarmUpFallbackTargetC, WarmUpTargetSource.Fallback));
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(85)]
    [InlineData(double.PositiveInfinity)]
    public void AnImplausibleRigAmbientIsIgnored(double bad)
        => WarmUpTarget.Resolve(double.NaN, null, bad).Source.ShouldBe(WarmUpTargetSource.Fallback);

    [Fact]
    public void ASensorAtTheRigAmbientNeedsNoWarmUpOnAColdNight()
    {
        // 10 C sensor on an 8 C night: it is above the 8 C target, so nothing to warm (the +25 C fallback would say warm).
        CameraReading.NeedsWarmUpFrom(coolerOn: true, 10, double.NaN, null, 8).ShouldBeFalse();
        CameraReading.NeedsWarmUpFrom(coolerOn: true, 10, double.NaN).ShouldBeTrue();
        CameraReading.NeedsWarmUpFrom(coolerOn: true, -5, double.NaN, null, 8).ShouldBeTrue();
    }

    [Fact(Timeout = 30_000)]
    public async Task TheFirstPlausibleSourceInOrderWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var reading = await RigAmbient.FirstPlausibleAsync(
        [
            _ => ValueTask.FromResult(double.NaN),
            _ => throw new InvalidOperationException("a source that throws is skipped"),
            _ => ValueTask.FromResult(200.0),
            _ => ValueTask.FromResult(12.5),
            _ => ValueTask.FromResult(15.0),
        ], ct);

        reading.ShouldBe(12.5);
    }

    [Fact(Timeout = 30_000)]
    public async Task NoPlausibleSourceReadsNaN()
    {
        var reading = await RigAmbient.FirstPlausibleAsync([_ => ValueTask.FromResult(double.NaN)], TestContext.Current.CancellationToken);

        double.IsNaN(reading).ShouldBeTrue();
    }
}
