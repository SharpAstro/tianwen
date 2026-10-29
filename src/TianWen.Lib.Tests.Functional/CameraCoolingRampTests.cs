using Shouldly;
using System;
using System.Linq;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// The one warm-up (#1840): an ambient target with no heat sink aims for the +25 C fallback, and the ramp ends on the
/// cooler's power, so an unreachable target costs a few steps and not the safety cap.
/// </summary>
[Collection("Session")]
public class CameraCoolingRampTests(ITestOutputHelper output)
{
    /// <summary>A degree per 15 s step: the ramp as it was before a ceiling.</summary>
    private const double FastRate = 4.0;

    private static readonly TimeSpan OneStep = CameraCoolingRamp.StepInterval;
    private const double Epsilon = 1e-9;

    [Fact(Timeout = 60_000)]
    public async Task AHeatsinklessCooledCameraIsWarmedTowardTheFallbackTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        ctx.Camera.MakeHeatsinkless(CameraReading.WarmUpFallbackTargetC, sensorCannotPassAmbient: false);

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), FastRate, 80, SetupointDirection.Down, ct);
        var cooled = await ctx.Camera.GetCCDTemperatureAsync(ct);
        cooled.ShouldBeLessThan(0);

        var reached = await ctx.Session.CoolCamerasToAmbientAsync(FastRate);

        reached.ShouldBeTrue();
        var warmed = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"Cooled {cooled:F1} C, warmed {warmed:F1} C");
        warmed.ShouldBe(CameraReading.WarmUpFallbackTargetC, 1.0);
    }

    [Fact(Timeout = 60_000)]
    public async Task AWarmUpEndsOnCoolerPowerWhenTheTargetCannotBeReached()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        // A cold night: the room is 12 C, so the sensor stalls there at 0 % power, well short of +25 C.
        ctx.Camera.MakeHeatsinkless(12, sensorCannotPassAmbient: true);

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), FastRate, 80, SetupointDirection.Down, ct);

        var start = ctx.TimeProvider.GetUtcNow();
        var reached = await ctx.Session.CoolCamerasToAmbientAsync(FastRate);
        var slept = ctx.TimeProvider.GetUtcNow() - start;

        var sensor = await ctx.Camera.GetCCDTemperatureAsync(ct);
        var power = await ctx.Camera.GetCoolerPowerAsync(ct);
        output.WriteLine($"Sensor {sensor:F1} C, power {power:F1} %, {slept.TotalSeconds:F0} s slept");
        sensor.ShouldBe(12.0, 0.5);
        power.ShouldBeLessThanOrEqualTo(CameraCoolingRamp.WarmUpPowerThreshold);
        reached.ShouldBeFalse();
        // At 4 C/min a step is a degree, so the walk up is about 22 steps, then a few at no power; the cap is twice the 30 planned.
        slept.ShouldBeLessThan(TimeSpan.FromSeconds(8 * 15) + TimeSpan.FromSeconds(22 * 15));
    }

    /// <summary>#1839: the ceiling in degrees per minute, over a drop the cooler could make faster.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ADropTakesAsLongAsTheCeilingSaysAndNoStepIsFasterThanIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        const double rate = 2.0;
        var maxStep = rate * OneStep.TotalMinutes;

        var start = ctx.TimeProvider.GetUtcNow();
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(0, SetpointTempKind.Normal), rate, 80, SetupointDirection.Down, ct);
        var took = ctx.TimeProvider.GetUtcNow() - start;

        output.WriteLine($"20 C drop at {rate} C/min took {took.TotalSeconds:F0} s");
        reached.ShouldBeTrue();
        // 20 C at 2 C/min is 10 minutes, plus the ramp's few confirming steps once there.
        took.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(10) - OneStep);
        took.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(10) + 6 * OneStep);
        ctx.Camera.SetpointsGiven.ShouldNotBeEmpty();
        ctx.Camera.SetpointsGiven.ShouldAllBe(g => g.SensorC - g.SetpointC <= maxStep + Epsilon, "no step past the ceiling");
    }

    [Fact(Timeout = 60_000)]
    public async Task ASmallDropIsNotStretchedBelowWhatTheCoolerGives()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);

        var start = ctx.TimeProvider.GetUtcNow();
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(17, SetpointTempKind.Normal), 10.0, 80, SetupointDirection.Down, ct);
        var took = ctx.TimeProvider.GetUtcNow() - start;

        output.WriteLine($"3 C drop at 10 C/min took {took.TotalSeconds:F0} s");
        reached.ShouldBeTrue();
        // A degree a step is the most a step moves, however high the ceiling: three steps, then the confirming ones.
        took.ShouldBeLessThanOrEqualTo(7 * OneStep);
        ctx.Camera.SetpointsGiven.ShouldAllBe(g => g.SetpointC == Math.Round(g.SetpointC), "a whole-degree step stays whole");
    }

    [Fact(Timeout = 60_000)]
    public async Task ALaggingCoolerHoldsTheSetpointBackToTheSensor()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        ctx.Camera.MakeCoolerLag(0.1);
        const double rate = 2.0;

        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(10, SetpointTempKind.Normal), rate, 80, SetupointDirection.Down, ct);

        var given = ctx.Camera.SetpointsGiven;
        given.Count.ShouldBeGreaterThan(3);
        // The setpoint follows the sensor, never its own schedule: one step past what the sensor reads, however slowly it goes.
        given.ShouldAllBe(g => g.SensorC - g.SetpointC <= rate * OneStep.TotalMinutes + Epsilon);
        given[^1].SetpointC.ShouldBeGreaterThan(10, "the sensor never got far, so the setpoint did not run ahead to the target");
    }

    [Fact(Timeout = 60_000)]
    public async Task AWarmUpIsHeldToTheCeilingToo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        const double rate = 2.0;
        await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(-10, SetpointTempKind.Normal), FastRate, 80, SetupointDirection.Down, ct);
        var coolSteps = ctx.Camera.SetpointsGiven.Count;

        var start = ctx.TimeProvider.GetUtcNow();
        var reached = await ctx.Session.CoolCamerasToAmbientAsync(rate);
        var took = ctx.TimeProvider.GetUtcNow() - start;

        output.WriteLine($"30 C warm-up at {rate} C/min took {took.TotalSeconds:F0} s");
        reached.ShouldBeTrue();
        // 30 C at 2 C/min is 15 minutes.
        took.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(15) - 2 * OneStep);
        took.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(15) + 6 * OneStep);
        var warming = ctx.Camera.SetpointsGiven.Skip(coolSteps).ToList();
        warming.ShouldNotBeEmpty();
        warming.ShouldAllBe(g => g.SetpointC - g.SensorC <= rate * OneStep.TotalMinutes + Epsilon);
    }

    /// <summary>#1839: a ceiling that binds on a small drop times it by the rate, not by a fixed wait.</summary>
    [Fact(Timeout = 60_000)]
    public async Task ASmallDropUnderABindingCeilingTakesAboutDropOverRate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        const double rate = 2.0;

        var start = ctx.TimeProvider.GetUtcNow();
        var reached = await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp(18, SetpointTempKind.Normal), rate, 80, SetupointDirection.Down, ct);
        var took = ctx.TimeProvider.GetUtcNow() - start;

        output.WriteLine($"2 C drop at {rate} C/min took {took.TotalSeconds:F0} s");
        reached.ShouldBeTrue();
        // 2 C at 2 C/min is a minute, then the confirming steps; far from the 10 minutes a fixed ramp time would take.
        took.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(1) - OneStep);
        took.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(1) + 6 * OneStep);
    }

    /// <summary>
    /// #1839: a camera that takes whole degrees only (every DAL camera truncates a fractional setpoint toward zero) is held
    /// to the ceiling by timing whole-degree steps, in both directions and through 0 C, and the ramp still finishes.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(20, -10, true)]
    [InlineData(-5, -10, true)]
    [InlineData(-10, 20, false)]
    [InlineData(5, 20, false)]
    public async Task AWholeDegreeCameraIsHeldToTheCeilingThroughZero(int from, int to, bool cooling)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var ctx = await SessionTestHelper.CreateSessionAsync(output, cancellationToken: ct);
        const double rate = 2.0;
        ctx.Camera.MakeWholeDegreeSetpoint();
        await ctx.Camera.SetCoolerOnAsync(true, ct);
        await ctx.Camera.SetSetCCDTemperatureAsync(from, ct);
        // The fake follows its setpoint on a read; the heat sink stays where it is, above any target here.
        for (var read = 0; read < 100 && Math.Abs(await ctx.Camera.GetCCDTemperatureAsync(ct) - from) > Epsilon; read++)
        {
        }
        var givenBefore = ctx.Camera.SetpointsGiven.Count;

        var start = ctx.TimeProvider.GetUtcNow();
        var reached = cooling
            ? await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp((sbyte)to, SetpointTempKind.Normal), rate, 80, SetupointDirection.Down, ct)
            : await ctx.Session.CoolCamerasToSetpointAsync(new SetpointTemp((sbyte)to, SetpointTempKind.Normal), rate, CameraCoolingRamp.WarmUpPowerThreshold, SetupointDirection.Up, ct);
        var took = ctx.TimeProvider.GetUtcNow() - start;

        var sensor = await ctx.Camera.GetCCDTemperatureAsync(ct);
        output.WriteLine($"{from} -> {to} at {rate} C/min took {took.TotalSeconds:F0} s, ends at {sensor:F1} C");
        reached.ShouldBeTrue();
        sensor.ShouldBe(to, 0.5);
        var drop = Math.Abs(to - from);
        took.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(drop / rate) - 2 * OneStep);
        took.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(drop / rate) + 8 * OneStep);

        // No minute moves the sensor more than the rate: each commanded setpoint is a whole degree, at most a degree from
        // the sensor, and a setpoint is held for 1 / rate minutes before the next.
        var given = ctx.Camera.SetpointsGiven.Skip(givenBefore).ToList();
        given.ShouldAllBe(g => g.SetpointC == Math.Round(g.SetpointC));
        given.ShouldAllBe(g => Math.Abs(g.SensorC - g.SetpointC) <= 1.5 + Epsilon);
        given.Count.ShouldBeLessThanOrEqualTo((int)Math.Ceiling(took.TotalMinutes * rate) + 1, "a setpoint is held for 1 / rate minutes");
    }
}
