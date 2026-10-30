using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>A degree per 15 s step: the ramp as it was before a ceiling, so these tests keep their pace.</summary>
    private const double Rate = 4.0;

    [Fact(Timeout = 60_000)]
    public async Task GivenCoolableCameraWhenCoolDownThenReachesSetpoint()
    {
        // given: camera starts at 20°C, target is -10°C
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        var initialTemp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Initial CCD temp: {initialTemp:F1} °C");
        initialTemp.ShouldBe(20.0, 0.1, "camera should start at ambient temperature");

        // when: cool down to -10°C
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            Rate,
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

        // What a node that crashed mid-ramp would re-establish: the TARGET, not a step on the way (the crash journal).
        ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>()
            .TryGetCoolerIntent(ctx.Session.Setup.Telescopes[0].Camera.Device.DeviceUri, out var intent).ShouldBeTrue();
        intent.ShouldBe(CoolerIntent.CoolTo(-10));
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenSteadySensorWhenCoolDownThenTheSettledReadingBeforeCoolingIsRecordedOnTheHub()
    {
        // The sensor rests at 20 C with the cooler off: after the window it is ambient, and a warm-up with no heat sink
        // can aim at it (#1842).
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), Rate, 80, SetupointDirection.Down, ct);

        ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>()
            .TryGetPreCoolReading(ctx.Session.Setup.Telescopes[0].Camera.Device.DeviceUri, out var reading).ShouldBeTrue();
        reading.Settled.ShouldBeTrue();
        reading.TempC.ShouldBe(20.0, 0.1, "the sensor before the first cooling step, not a step on the way down");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCooledCameraWhenWarmUpThenReachesHigherTemp()
    {
        // given: cool camera down first
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            Rate,
            80,
            SetupointDirection.Down,
            ct
        );

        var cooledTemp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Cooled CCD temp: {cooledTemp:F1} °C");
        cooledTemp.ShouldBeLessThan(0, "camera should be below zero after cooldown");

        // when: warm up toward 0°C (a concrete target above cooled temp)
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(0, SetpointTempKind.Normal),
            Rate,
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
    public async Task GivenNoHeatSinkWhenWarmUpThenTheSettledReadingBeforeCoolingIsTheTarget()
    {
        // The 2026-10-27 case: a fan-cooled body with no heat sink that cooled from a settled 12 C. The session's ambient
        // warm-up aims at that reading.
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        ctx.Camera.HasHeatsink = false;
        var hub = ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>();
        var uri = ctx.Session.Setup.Telescopes[0].Camera.Device.DeviceUri;

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), Rate, 80, SetupointDirection.Down, ct);
        hub.SetPreCoolReading(uri, new SensorAmbientReading(5, Settled: true, ctx.TimeProvider.GetUtcNow()));

        var reached = await ctx.Session.CoolCamerasToAmbientAsync(Rate);

        reached.ShouldBeTrue();
        var temp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        temp.ShouldBe(5.0, 1.0, "the settled reading, not the +25 C fallback");
        hub.TryGetCoolerIntent(uri, out var intent).ShouldBeTrue();
        intent.ShouldBe(CoolerIntent.Warm, "a crash mid-warm-up goes on warming, never re-cools");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenNoHeatSinkAndASensorAlreadyAtTheSettledReadingWhenWarmUpThenItEndsAtOnce()
    {
        // The 2026-10-27 shape: the sensor sits within a degree under the settled reading with the cooler off. The hub's
        // warm-up ends at once there; the session's must too, with no ramp step slept.
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        ctx.Camera.HasHeatsink = false;
        var hub = ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>();
        var uri = ctx.Session.Setup.Telescopes[0].Camera.Device.DeviceUri;
        hub.SetPreCoolReading(uri, new SensorAmbientReading(20.5, Settled: true, ctx.TimeProvider.GetUtcNow()));
        var before = ctx.TimeProvider.GetUtcNow();

        var reached = await ctx.Session.CoolCamerasToAmbientAsync(Rate);

        reached.ShouldBeTrue();
        (ctx.TimeProvider.GetUtcNow() - before).ShouldBe(TimeSpan.Zero, "no ramp step was slept");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenNoHeatSinkNoReadingAndTheCoolerOffWhenWarmUpThenTheCoolerIsLeftOff()
    {
        // A session that failed before its cool-down: no heat sink, nothing recorded, the cooler off and the sensor at 20 C,
        // under the +25 C fallback. The hub holds such a camera; the session's warm-up must not switch the cooler on to ramp it.
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        ctx.Camera.HasHeatsink = false;
        (await ctx.Camera.GetCoolerOnAsync(ct)).ShouldBeFalse("the precondition: the cooler was never switched on");
        var before = ctx.TimeProvider.GetUtcNow();

        var reached = await ctx.Session.CoolCamerasToAmbientAsync(Rate);

        reached.ShouldBeTrue();
        (ctx.TimeProvider.GetUtcNow() - before).ShouldBe(TimeSpan.Zero, "no ramp step was slept");
        (await ctx.Camera.GetCoolerOnAsync(ct)).ShouldBeFalse("a cooler that was off stays off");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenNoHeatSinkAndAnUnsettledReadingWhenWarmUpThenTheFallbackIsTheTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        ctx.Camera.HasHeatsink = false;
        var hub = ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>();
        var uri = ctx.Session.Setup.Telescopes[0].Camera.Device.DeviceUri;

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), Rate, 80, SetupointDirection.Down, ct);
        hub.SetPreCoolReading(uri, new SensorAmbientReading(5, Settled: false, ctx.TimeProvider.GetUtcNow()));

        await ctx.Session.CoolCamerasToAmbientAsync(Rate);

        var temp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        // The fake's sensor rests at its 20 C ambient and cannot rise past it, so +25 C shows as 20 C, well above the 5 C
        // an unsettled reading would have aimed at if it were trusted.
        temp.ShouldBe(20.0, 1.0, "an unsettled reading never sets the target, so it warms toward +25 C, not the recorded 5 C");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenAHeatSinkWhenWarmUpThenItBeatsTheRecordedReading()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        var hub = ctx.Session.ServiceProvider.GetRequiredService<IDeviceHub>();
        var uri = ctx.Session.Setup.Telescopes[0].Camera.Device.DeviceUri;

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), Rate, 80, SetupointDirection.Down, ct);
        hub.SetPreCoolReading(uri, new SensorAmbientReading(5, Settled: true, ctx.TimeProvider.GetUtcNow()));

        await ctx.Session.CoolCamerasToAmbientAsync(Rate);

        var temp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        temp.ShouldBe(20.0, 1.0, "the heat sink's 20 C, not the recorded 5 C");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCoolableCameraWhenCoolDownThenCoolerTurnsOn()
    {
        // given
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        var coolerBefore = await ctx.Camera.GetCoolerOnAsync(ct);
        coolerBefore.ShouldBeFalse("cooler should be off initially");

        // when: start cooling
        await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            Rate,
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
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        // when: cancel after 10 fake-time seconds via timer on the FakeTimeProvider.
        // A step is 15 s of fake time and a degree.
        // 10s allows no step past the first (1°C cooled) before the cancel timer fires
        // deterministically inside the loop's own Advance call. No Task.Run race.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var cancelTimer = ctx.TimeProvider.CreateTimer(
            _ => cts.Cancel(), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        // then: cancellation surfaces as OperationCanceledException, which is what the REAL clock does --
        // Task.Delay(duration, timeProvider, ct) throws when the token is cancelled mid-wait, and the timer
        // above fires inside the loop's own sleep. The loop's graceful break covers only a cancel observed
        // BETWEEN sleeps, and a 15 s ramp interval means production almost never lands there. Nothing is
        // swallowed on purpose: RunAsync catches this and sets SessionPhase.Aborted, which is the wanted
        // outcome, and the one path that must not be cancellable (warming back to ambient) already passes
        // CancellationToken.None rather than relying on the loop to be lenient.
        // This test asserted a graceful return until the fake clock started honouring its token; that
        // return was reachable only under the fake.
        await Should.ThrowAsync<OperationCanceledException>(async () => await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            Rate,
            80,
            SetupointDirection.Down,
            cts.Token
        ));

        // and: it stopped EARLY, which is the substantive claim (20°C -> -10°C is 30 steps)
        var temp = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Temp after cancellation: {temp:F1} °C");
        temp.ShouldBeGreaterThan(-10, "should not have reached setpoint when cancelled early");
    }

    [Fact(Timeout = 60_000)]
    public async Task GivenCoolableCameraWhenCoolToSensorTempThenUsesCurrentCCDTemp()
    {
        // given: camera at 20°C
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        // when: cool to CCD sensor temp (should stabilize at current temp)
        var reached = await ctx.Session.CoolCamerasToSensorTempAsync(Rate, ct);

        // then: should reach target quickly since setpoint ≈ current temp
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
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        // when: cool to -10°C
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(
            new SetpointTemp(-10, SetpointTempKind.Normal),
            Rate,
            80,
            SetupointDirection.Down,
            ct
        );

        // then: power should reflect the temperature delta from heatsink
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
