using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Fake;
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

    public static TheoryData<int, double, bool, SetupointDirection> HealthyRamps()
    {
        var data = new TheoryData<int, double, bool, SetupointDirection>();
        foreach (var direction in (SetupointDirection[])[SetupointDirection.Down, SetupointDirection.Up])
        {
            foreach (var wholeDegree in (bool[])[false, true])
            {
                foreach (var rate in (double[])[0.5, 2, 10])
                {
                    foreach (var drop in (int[])[0, 1, 2, 3, 5])
                    {
                        data.Add(drop, rate, wholeDegree, direction);
                    }
                }
            }
        }
        return data;
    }

    /// <summary>
    /// #1839: a ramp at its target still takes three counting steps and the one that ends it, a setpoint's hold apart on a
    /// whole-degree camera, so a cap sized from the drop alone cut a small healthy ramp short and logged it as a stuck cooler.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [MemberData(nameof(HealthyRamps))]
    public async Task ARampWhoseSensorFollowsEndsOnItsOwnNeverOnTheCap(int drop, double rate, bool wholeDegree, SetupointDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await ConnectedCameraAsync(ct);
        if (wholeDegree)
        {
            camera.MakeWholeDegreeSetpoint();
        }
        var from = StartOf(direction);
        await SettleAtAsync(camera, from, ct);
        var to = direction is SetupointDirection.Down ? from - drop : from + drop;
        var sleeps = new List<TimeSpan>();
        var logger = new WarningLogger(FakeExternal.CreateLogger(output));

        var reached = await CameraCoolingRamp.RunAsync([camera], new SetpointTemp((sbyte)to, SetpointTempKind.Normal), rate,
            ThresholdOf(direction), direction, EquipmentWarmUpTests.RecordingClock(sleeps), logger, afterStep: null, ct);

        var took = TimeSpan.FromTicks(sleeps.Sum(s => s.Ticks));
        output.WriteLine($"{from} -> {to} at {rate} C/min, whole degrees {wholeDegree}: {sleeps.Count} steps, {took.TotalSeconds:F0} s");
        reached.ShouldBeTrue();
        (await camera.GetCCDTemperatureAsync(ct)).ShouldBe(to, 0.5);
        logger.Warnings.ShouldNotContain(w => w.Contains("safety cap"), "a ramp whose sensor follows its setpoint ends on its own exit");
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACoolerThatNeverFollowsStillEndsOnTheCap(bool wholeDegree)
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await ConnectedCameraAsync(ct);
        if (wholeDegree)
        {
            camera.MakeWholeDegreeSetpoint();
        }
        await SettleAtAsync(camera, 10, ct);
        camera.MakeCoolerLag(0);
        var sleeps = new List<TimeSpan>();
        var logger = new WarningLogger(FakeExternal.CreateLogger(output));

        var reached = await CameraCoolingRamp.RunAsync([camera], new SetpointTemp(5, SetpointTempKind.Normal), 2.0,
            CameraCoolingRamp.CoolDownPowerThreshold, SetupointDirection.Down, EquipmentWarmUpTests.RecordingClock(sleeps), logger, afterStep: null, ct);

        var took = TimeSpan.FromTicks(sleeps.Sum(s => s.Ticks));
        output.WriteLine($"A cooler that never moves was let go after {took.TotalMinutes:F2} min");
        reached.ShouldBeFalse();
        logger.Warnings.ShouldContain(w => w.Contains("safety cap"));
        took.ShouldBeGreaterThanOrEqualTo(CameraCoolingRamp.MinimumSafetyCap - OneStep, "a slow cooler is given a few minutes");
        took.ShouldBeLessThan(TimeSpan.FromMinutes(30), "and is still let go");
    }

    /// <summary>
    /// #1839: a start that cannot be read used to plan a degree, so a whole cool-down was let go after a minute and the
    /// session went on to image warm.
    /// </summary>
    [Theory(Timeout = 60_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnreadableStartPlansTheLongDropNotADegree(bool throws)
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await ConnectedCameraAsync(ct);
        await SettleAtAsync(camera, 20, ct);
        camera.MakeNextTemperatureReadsUnreadable(1, throws);
        const double rate = 2.0;
        var sleeps = new List<TimeSpan>();
        var logger = new WarningLogger(FakeExternal.CreateLogger(output));

        var reached = await CameraCoolingRamp.RunAsync([camera], new SetpointTemp(-5, SetpointTempKind.Normal), rate,
            CameraCoolingRamp.CoolDownPowerThreshold, SetupointDirection.Down, EquipmentWarmUpTests.RecordingClock(sleeps), logger, afterStep: null, ct);

        var took = TimeSpan.FromTicks(sleeps.Sum(s => s.Ticks));
        output.WriteLine($"25 C drop with an unreadable start took {took.TotalMinutes:F2} min");
        reached.ShouldBeTrue();
        (await camera.GetCCDTemperatureAsync(ct)).ShouldBe(-5, 0.5);
        logger.Warnings.ShouldNotContain(w => w.Contains("safety cap"));
        took.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(25 / rate) - OneStep);
    }

    /// <summary>#1839: a rate that is not finite, or not above zero, is the direction's default, checked once in the ramp for every caller.</summary>
    [Theory(Timeout = 60_000)]
    [InlineData(double.NaN, SetupointDirection.Down)]
    [InlineData(double.PositiveInfinity, SetupointDirection.Down)]
    [InlineData(double.NegativeInfinity, SetupointDirection.Down)]
    [InlineData(0d, SetupointDirection.Down)]
    [InlineData(-1d, SetupointDirection.Down)]
    [InlineData(double.NaN, SetupointDirection.Up)]
    [InlineData(double.PositiveInfinity, SetupointDirection.Up)]
    [InlineData(double.NegativeInfinity, SetupointDirection.Up)]
    [InlineData(0d, SetupointDirection.Up)]
    [InlineData(-1d, SetupointDirection.Up)]
    public async Task ARateThatIsNotARateRampsAtTheDefault(double rate, SetupointDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        var camera = await ConnectedCameraAsync(ct);
        var from = StartOf(direction);
        await SettleAtAsync(camera, from, ct);
        const int drop = 5;
        var to = direction is SetupointDirection.Down ? from - drop : from + drop;
        var givenBefore = camera.SetpointsGiven.Count;
        var sleeps = new List<TimeSpan>();
        var logger = new WarningLogger(FakeExternal.CreateLogger(output));

        var reached = await CameraCoolingRamp.RunAsync([camera], new SetpointTemp((sbyte)to, SetpointTempKind.Normal), rate,
            ThresholdOf(direction), direction, EquipmentWarmUpTests.RecordingClock(sleeps), logger, afterStep: null, ct);

        var took = TimeSpan.FromTicks(sleeps.Sum(s => s.Ticks));
        var defaultRate = CameraCoolingRamp.DefaultMaxRateOf(direction);
        output.WriteLine($"{rate} C/min {direction}: {drop} C took {took.TotalSeconds:F0} s");
        reached.ShouldBeTrue();
        logger.Warnings.ShouldContain(w => w.Contains("not a rate"));
        var given = camera.SetpointsGiven.Skip(givenBefore).ToList();
        given.ShouldNotBeEmpty();
        given.ShouldAllBe(g => double.IsFinite(g.SetpointC), "a NaN rate never becomes a NaN setpoint");
        given.ShouldAllBe(g => Math.Abs(g.SensorC - g.SetpointC) <= defaultRate * OneStep.TotalMinutes + Epsilon);
        took.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(drop / defaultRate) - OneStep);
        took.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(drop / defaultRate) + 4 * OneStep);
    }

    /// <summary>Cooling starts above, and warming below, the fake's 20 C heat sink, so the cooler is working throughout.</summary>
    private static int StartOf(SetupointDirection direction) => direction is SetupointDirection.Down ? 10 : -10;

    private static double ThresholdOf(SetupointDirection direction)
        => direction is SetupointDirection.Down ? CameraCoolingRamp.CoolDownPowerThreshold : CameraCoolingRamp.WarmUpPowerThreshold;

    private async Task<FakeCameraDriver> ConnectedCameraAsync(CancellationToken cancellationToken)
    {
        var hub = new FakeExternal(output).BuildServiceProvider().GetRequiredService<IDeviceHub>();
        return (FakeCameraDriver)await hub.ConnectAsync(new FakeDevice(DeviceType.Camera, 1), cancellationToken);
    }

    /// <summary>Takes the sensor to <paramref name="tempC"/> with the cooler on: the fake follows its setpoint a degree a read.</summary>
    private static async Task SettleAtAsync(FakeCameraDriver camera, int tempC, CancellationToken cancellationToken)
    {
        await camera.SetCoolerOnAsync(true, cancellationToken);
        await camera.SetSetCCDTemperatureAsync(tempC, cancellationToken);
        for (var read = 0; read < 100 && Math.Abs(await camera.GetCCDTemperatureAsync(cancellationToken) - tempC) > Epsilon; read++)
        {
        }
    }

    /// <summary>Writes to the test's output and keeps every warning, so a test can tell which exit a ramp took.</summary>
    private sealed class WarningLogger(ILogger inner) : ILogger
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                _warnings.Enqueue(formatter(state, exception));
            }
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
