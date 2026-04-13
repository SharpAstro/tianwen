using Shouldly;
using System;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

[Collection("Session")]
public class SessionCoolingTests(ITestOutputHelper output)
{
    [Fact(Timeout = 60_000)]
    public async Task GivenCoolableCameraWhenCoolDownThenReachesSetpoint()
    {
        // given — camera starts at 20°C, target is -10°C
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        var initialTemp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Initial CCD temp: {initialTemp:F1} °C");
        initialTemp.ShouldBe(20.0, 0.1, "camera should start at ambient temperature");

        // when — cool down to -10°C
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            TimeSpan.FromSeconds(60),
            80,
            SetupointDirection.Down,
            ct
        );

        // then
        reached.ShouldBeTrue("setpoint should be reached");

        var finalTemp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Final CCD temp: {finalTemp:F1} °C");
        finalTemp.ShouldBe(-10.0, 1.0, "CCD temperature should be near setpoint");

        var coolerOn = await ctx.Camera.GetCoolerOnAsync(ct);
        coolerOn.ShouldBeTrue("cooler should be on after cooldown");

        var power = await ctx.Camera.GetCoolerPowerAsync(ct);
        output.WriteLine($"Cooler power: {power:F1}%");
        power.ShouldBeGreaterThan(0, "cooler should be drawing power");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCooledCameraWhenWarmUpThenReachesHigherTemp()
    {
        // given — cool camera down first
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            TimeSpan.FromSeconds(60),
            80,
            SetupointDirection.Down,
            ct
        );

        var cooledTemp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Cooled CCD temp: {cooledTemp:F1} °C");
        cooledTemp.ShouldBeLessThan(0, "camera should be below zero after cooldown");

        // when — warm up toward 0°C (a concrete target above cooled temp)
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(0, SetpointTempKind.Normal),
            TimeSpan.FromSeconds(60),
            0.1,
            SetupointDirection.Up,
            ct
        );

        // then
        reached.ShouldBeTrue("warmup setpoint should be reached");

        var finalTemp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Final CCD temp after warmup: {finalTemp:F1} °C");
        finalTemp.ShouldBe(0.0, 1.0, "temperature should have risen to ~0°C");
        finalTemp.ShouldBeGreaterThan(cooledTemp, "temperature should have risen from cooled state");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCoolableCameraWhenCoolDownThenCoolerTurnsOn()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        var coolerBefore = await ctx.Camera.GetCoolerOnAsync(ct);
        coolerBefore.ShouldBeFalse("cooler should be off initially");

        // when — start cooling
        await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            TimeSpan.FromSeconds(60),
            80,
            SetupointDirection.Down,
            ct
        );

        // then
        var coolerAfter = await ctx.Camera.GetCoolerOnAsync(ct);
        coolerAfter.ShouldBeTrue("cooler should be on after cooldown started");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCoolableCameraWhenCoolDownCancelledThenStopsEarly()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        // when — cancel after 10 fake-time seconds via timer on the FakeTimeProvider.
        // With totalRampTime=60s for 30°C delta, each step sleeps ~2s of fake time.
        // 10s allows ~5 steps (5°C cooled) before the cancel timer fires
        // deterministically inside the loop's own Advance call. No Task.Run race.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancelTimer = ctx.TimeProvider.CreateTimer(
            _ => cts.Cancel(), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            TimeSpan.FromSeconds(60),
            80,
            SetupointDirection.Down,
            cts.Token
        );

        // then — should not have fully reached setpoint (20°C → -10°C is 30 steps, only 3 completed)
        var temp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Temp after cancellation: {temp:F1} °C");
        temp.ShouldBeGreaterThan(-10, "should not have reached setpoint when cancelled early");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCoolableCameraWhenCoolToSensorTempThenUsesCurrentCCDTemp()
    {
        // given — camera at 20°C
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        // when — cool to CCD sensor temp (should stabilize at current temp)
        var reached = await ctx.Session.CoolCamerasToSensorTempAsync(TimeSpan.FromSeconds(1), ct);

        // then — should reach target quickly since setpoint ≈ current temp
        reached.ShouldBeTrue("should reach sensor temp setpoint");

        var temp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Temp after CoolToSensorTemp: {temp:F1} °C");
        temp.ShouldBe(20.0, 1.0, "should stay near initial temperature");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCooldownWhenTemperatureRampsThenPowerIncreasesGradually()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        // when — cool to -10°C
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            TimeSpan.FromSeconds(60),
            80,
            SetupointDirection.Down,
            ct
        );

        // then — power should reflect the temperature delta from heatsink
        reached.ShouldBeTrue();

        var finalPower = await ctx.Camera.GetCoolerPowerAsync(ct);
        var finalTemp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        var heatsinkTemp = await ctx.Camera.GetHeatSinkTemperatureAsync(ct);

        output.WriteLine($"Heatsink: {heatsinkTemp:F1} °C, CCD: {finalTemp:F1} °C, Power: {finalPower:F1}%");

        // Power = (heatsink - ccd) / 40 * 100 ≈ (20 - (-10)) / 40 * 100 = 75%
        finalPower.ShouldBeGreaterThan(50, "cooler power should be significant at -10°C");
        finalPower.ShouldBeLessThan(100, "cooler power should not exceed 100%");
    }
}
