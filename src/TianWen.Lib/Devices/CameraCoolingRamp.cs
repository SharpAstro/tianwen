using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Extensions;

namespace TianWen.Lib.Devices;

/// <summary>
/// The cooling ramp, ONE for every host: a session's cool-down and warm-up (<c>Session.CoolCamerasToSetpointAsync</c>)
/// and a node re-establishing a camera's cooling after the node before it crashed (the crash journal, P1 of
/// docs/plans/hardware-in-the-server.md, #917), which must cool "through the same cool-down the session uses, never as a
/// jump". It lived inside <c>Session</c>, where nothing without a session could reach it; a second copy would be a
/// second answer to how fast a sensor may be cooled.
/// </summary>
public static class CameraCoolingRamp
{
    /// <summary>A step every 15 s, as N.I.N.A. does: each step moves a setpoint at most a degree, and at most what the rate allows.</summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromSeconds(15);

    /// <summary>The cooler power a cool-down stops pushing at: what every session cool-down passes.</summary>
    public const double CoolDownPowerThreshold = 80;

    /// <summary>
    /// The cooler power (percent) at or below which a warm-up counts the cooler as no longer holding the sensor down: a few
    /// consecutive steps at it end the ramp whatever the sensor reads. A cooler cannot warm a sensor past ambient, so an
    /// unreachable target would otherwise step until the safety cap (the QHY294PROC, 2026-10-26: 15 minutes stepping at 0 %
    /// power toward an unreachable +25 C).
    /// </summary>
    public const double WarmUpPowerThreshold = 1;

    /// <summary>
    /// The whole degree a ramp to <paramref name="setpointC"/> drives a sensor to, which is also the cooler intent it
    /// records. <see cref="sbyte.MinValue"/> is the ramp's "no value" for the sensor and ambient kinds, so never a target.
    /// </summary>
    public static sbyte TargetOf(double setpointC) => (sbyte)Math.Clamp(Math.Round(setpointC), sbyte.MinValue + 1, sbyte.MaxValue);

    /// <summary>The ceiling a cool-down moves the sensor at, degrees C per minute: gentler than the 4 to 4.5 measured on the QHY294PROC, so a 20 C drop takes about ten minutes.</summary>
    public const double DefaultCoolingMaxRateCPerMinute = 2.0;

    /// <summary>The ceiling a warm-up moves the sensor at, degrees C per minute.</summary>
    public const double DefaultWarmingMaxRateCPerMinute = 2.0;

    /// <summary>
    /// The fastest ceiling that changes anything: a step moves a setpoint at most a degree, so a rate above a degree per
    /// <see cref="StepInterval"/> (4 C/min) ramps as this one does.
    /// </summary>
    public static double FastestMaxRateCPerMinute => 1d / StepInterval.TotalMinutes;

    /// <summary>The default ceiling of <paramref name="direction"/>: cooling for <see cref="SetupointDirection.Down"/>, warming for <see cref="SetupointDirection.Up"/>.</summary>
    public static double DefaultMaxRateOf(SetupointDirection direction)
        => direction is SetupointDirection.Up ? DefaultWarmingMaxRateCPerMinute : DefaultCoolingMaxRateCPerMinute;

    /// <summary>
    /// The drop a ramp plans when its start says nothing of it (the sensor's first reading failed, or the target is the
    /// sensor or ambient with a cooler working): it sizes only the safety cap, so it errs long.
    /// </summary>
    private const double UnknownDropC = 30;

    /// <summary>
    /// The shortest safety cap: a cooler that settles slowly on a small drop is given this long before the ramp lets it go.
    /// </summary>
    public static readonly TimeSpan MinimumSafetyCap = TimeSpan.FromMinutes(5);

    /// <summary>The steps a ramp at its target still takes: three counting the target as reached, and the one that ends it.</summary>
    private const int ConfirmingSteps = 4;

    /// <summary>
    /// How far one step moves a setpoint past the sensor's reading at <paramref name="maxRateCPerMinute"/>: the rate times
    /// <see cref="StepInterval"/>, never more than the whole degree a step has always been (a rate of 4 C/min or more is
    /// today's ramp).
    /// </summary>
    public static double MaxStepOf(double maxRateCPerMinute)
        => Math.Clamp(maxRateCPerMinute * StepInterval.TotalMinutes, 0.01, 1d);

    /// <summary>
    /// Ramps every camera in <paramref name="cameras"/> towards <paramref name="target"/> together, a step each per
    /// <see cref="StepInterval"/>, moving the sensor no faster than <paramref name="maxRateCPerMinute"/>, and gives up at
    /// twice the time the drop takes at that rate plus the confirming steps, never sooner than <see cref="MinimumSafetyCap"/>.
    /// </summary>
    /// <param name="target">The setpoint: <see cref="SetpointTempKind.Normal"/> is its value,
    /// <see cref="SetpointTempKind.CCD"/> the sensor's own temperature, otherwise ambient (where the camera can say).</param>
    /// <param name="maxRateCPerMinute">The most the sensor may move per minute, degrees C: a ceiling, so a small drop is never
    /// stretched to fill a time and a cooler slower than it is not hurried. A rate that is not finite, or not above zero, is
    /// the default of <paramref name="direction"/> (<see cref="DefaultMaxRateOf"/>).</param>
    /// <param name="afterStep">Called for each camera (by index) after each of its steps: a session samples its
    /// cooling telemetry here.</param>
    /// <param name="onSensorAmbient">Called once per camera (by index) with the sensor reading taken BEFORE its first
    /// cooling step, for a downward ramp to a <see cref="SetpointTempKind.Normal"/> target: the ramp knows no hub, so
    /// the caller records it. A camera whose cooler is already on, or that gives no CCD temperature, is never called for.
    /// Null skips the sampling altogether, for a ramp whose sensor says nothing about the air (a node cooling a camera
    /// back after a crash).</param>
    /// <param name="targets">A setpoint for each camera, by index, in place of <paramref name="target"/> for the ramp
    /// itself: a warm-up aims each camera at its own resolved target (<see cref="WarmUpTarget"/>). Null ramps every
    /// camera to <paramref name="target"/>.</param>
    /// <returns>True if every coolable camera reached the setpoint.</returns>
    public static async ValueTask<bool> RunAsync(
        IReadOnlyList<ICameraDriver> cameras,
        SetpointTemp target,
        double maxRateCPerMinute,
        double thresPower,
        SetupointDirection direction,
        ITimeProvider timeProvider,
        ILogger logger,
        Func<int, ICameraDriver, CancellationToken, ValueTask>? afterStep,
        Func<int, SensorAmbientReading, ValueTask>? onSensorAmbient,
        IReadOnlyList<SetpointTemp>? targets,
        CancellationToken cancellationToken)
    {
        SetpointTemp TargetOfCamera(int i) => targets is { } each ? each[i] : target;

        if (onSensorAmbient is not null && direction == SetupointDirection.Down && target.Kind == SetpointTempKind.Normal)
        {
            await ReadSensorAmbientAsync(cameras, timeProvider, logger, onSensorAmbient, cancellationToken).ConfigureAwait(false);
        }

        var coolingStates = new CameraCoolingState[cameras.Count];

        // Checked once here for every caller: a NaN rate would become a NaN setpoint, and one of zero or less an endless ramp.
        if (!double.IsFinite(maxRateCPerMinute) || maxRateCPerMinute <= 0)
        {
            var defaultRate = DefaultMaxRateOf(direction);
            logger.LogWarning("Cooling: {Rate} C/min is not a rate, ramping at the default {DefaultRate} C/min.", maxRateCPerMinute, defaultRate);
            maxRateCPerMinute = defaultRate;
        }

        // The size of the drop, from the initial temperature delta: it sets the cap below.
        var maxDelta = 1.0;
        var anyNotNormal = false;
        for (var c = 0; c < cameras.Count; c++)
        {
            var camera = cameras[c];
            var cameraTarget = TargetOfCamera(c);
            anyNotNormal |= cameraTarget.Kind is not SetpointTempKind.Normal;
            var ccdTemp = await logger.CatchAsync(camera.GetCCDTemperatureAsync, cancellationToken, double.NaN);
            if (double.IsNaN(ccdTemp))
            {
                // A start that cannot be read says nothing of the drop: plan the long one, never a degree, or the cap would
                // let a whole cool-down go after a minute.
                if (target.Kind is SetpointTempKind.Normal)
                {
                    maxDelta = Math.Max(maxDelta, UnknownDropC);
                }
            }
            else
            {
                var targetC = cameraTarget.Kind switch
                {
                    SetpointTempKind.Normal => (double)cameraTarget.TempC,
                    _ => ccdTemp // CCD/Ambient: target will be resolved later, estimate delta = 0
                };
                maxDelta = Math.Max(maxDelta, Math.Abs(ccdTemp - targetC));
            }
        }
        // For CCD/Ambient kinds: check if cooler is even on before ramping
        if (anyNotNormal && maxDelta <= 1)
        {
            // Check if any camera's cooler is actually on
            var anyCoolerOn = false;
            foreach (var camera in cameras)
            {
                // A camera that cannot say its cooler power may well have the cooler on: ramp it on the sensor.
                var power = camera.CanGetCoolerPower
                    ? await logger.CatchAsync(camera.GetCoolerPowerAsync, cancellationToken, double.NaN)
                    : double.NaN;
                if (double.IsNaN(power) || power > 1)
                {
                    anyCoolerOn = true;
                    break;
                }
            }
            if (!anyCoolerOn)
            {
                logger.LogInformation("Cooling: all cameras already at ambient, skipping warmup ramp.");
                return true;
            }
            maxDelta = UnknownDropC;
        }
        // The ceiling bounds how far one step may move the setpoint; a rate at or above a degree per step is the old whole degree.
        var maxStepC = MaxStepOf(maxRateCPerMinute);
        // A camera that takes only whole degrees cannot be given a fractional step: it keeps its whole degree and holds each
        // setpoint for 1 C / rate minutes, a whole number of StepIntervals (rounded up, so never faster than the ceiling).
        var stepsPerSetpoint = new int[cameras.Count];
        var stepC = new double[cameras.Count];
        var plannedSteps = 0L;
        var confirmingSteps = 0L;
        for (var i = 0; i < cameras.Count; i++)
        {
            if (cameras[i].SetpointResolutionC >= 1d)
            {
                stepC[i] = 1d;
                stepsPerSetpoint[i] = Math.Max(1, (int)Math.Ceiling(1d / (maxRateCPerMinute * StepInterval.TotalMinutes) - 1e-9));
            }
            else
            {
                stepC[i] = maxStepC;
                stepsPerSetpoint[i] = 1;
            }
            plannedSteps = Math.Max(plannedSteps, (long)Math.Ceiling(maxDelta / stepC[i]) * stepsPerSetpoint[i]);
            // A camera's confirming steps come a setpoint's hold apart, as its ramping ones do.
            confirmingSteps = Math.Max(confirmingSteps, (long)ConfirmingSteps * stepsPerSetpoint[i]);
        }
        // The time the drop takes at that ceiling. The cap is twice it, plus the steps that confirm the target once there, so
        // a ramp whose sensor follows its setpoint always ends on its own; a cooler that lags is given room, and one that never
        // gets there is still let go.
        var plannedRampTime = TimeSpan.FromTicks(plannedSteps * StepInterval.Ticks);
        var safetyCap = TimeSpan.FromTicks(Math.Max(MinimumSafetyCap.Ticks, (2 * plannedSteps + confirmingSteps) * StepInterval.Ticks));

        var accSleep = TimeSpan.Zero;
        var iteration = 0;
        do
        {
            for (var i = 0; i < cameras.Count; i++)
            {
                // A whole-degree camera takes a new setpoint only every stepsPerSetpoint steps; the others it is held, and polled.
                if (iteration % stepsPerSetpoint[i] == 0)
                {
                    coolingStates[i] = await cameras[i].CoolToSetpointAsync(TargetOfCamera(i), thresPower, direction, coolingStates[i], stepC[i], cancellationToken);
                }
                if (afterStep is not null)
                {
                    await afterStep(i, cameras[i], cancellationToken);
                }
            }

            // Check exit condition before sleeping: avoid an unnecessary wait when already at target
            if (!coolingStates.Any(state => state.IsRamping) || cancellationToken.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Cancellation requested, quitting cooldown loop");
                }
                break;
            }

            iteration++;
            accSleep += StepInterval;
            if (accSleep >= safetyCap)
            {
                logger.LogWarning("Cooling: safety cap reached ({AccSleep} >= {SafetyCap}, the ramp planned {PlannedRamp}), exiting ramp loop.",
                    accSleep, safetyCap, plannedRampTime);
                break;
            }

            await timeProvider.SleepAsync(StepInterval, cancellationToken).ConfigureAwait(false);
        } while (true);

        return coolingStates.All(state => !(state.IsCoolable ?? false) || (state.TargetSetpointReached ?? false));
    }

    /// <summary>How often a camera with its cooler off is sampled for its settled sensor reading.</summary>
    public static readonly TimeSpan AmbientSampleInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest the ramp waits for a sensor to settle before it cools anyway: two windows, so a sensor that settles
    /// late still has a full window, and one that never does costs a bounded two minutes.
    /// </summary>
    public static readonly TimeSpan AmbientBudget = SensorAmbientSampler.Window * 2;

    /// <summary>
    /// Samples each camera whose cooler reads OFF every <see cref="AmbientSampleInterval"/> until every one has settled
    /// or <see cref="AmbientBudget"/> runs out, and hands each reading out. Sleeps through <see cref="ITimeProvider"/>,
    /// so a fake clock advances it.
    /// </summary>
    private static async ValueTask ReadSensorAmbientAsync(
        IReadOnlyList<ICameraDriver> cameras, ITimeProvider timeProvider, ILogger logger,
        Func<int, SensorAmbientReading, ValueTask>? onSensorAmbient, CancellationToken cancellationToken)
    {
        var samplers = new SensorAmbientSampler?[cameras.Count];
        for (var i = 0; i < cameras.Count; i++)
        {
            var camera = cameras[i];
            if (!camera.CanGetCoolerOn || await logger.CatchAsync(camera.GetCoolerOnAsync, cancellationToken, true))
            {
                continue; // cooler on, or unreadable: the sensor says nothing about the air around it
            }

            samplers[i] = new SensorAmbientSampler();
        }

        var waited = TimeSpan.Zero;
        while (true)
        {
            var allSettled = true;
            for (var i = 0; i < cameras.Count; i++)
            {
                if (samplers[i] is not { Settled: false } sampler)
                {
                    continue;
                }

                var temp = await logger.CatchAsync(cameras[i].GetCCDTemperatureAsync, cancellationToken, double.NaN);
                if (!double.IsFinite(temp) && sampler.Current is null)
                {
                    samplers[i] = null; // gives no CCD temperature: records nothing
                    continue;
                }

                allSettled &= sampler.Add(timeProvider.GetUtcNow(), temp);
            }

            if (allSettled || waited >= AmbientBudget || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await timeProvider.SleepAsync(AmbientSampleInterval, cancellationToken).ConfigureAwait(false);
            waited += AmbientSampleInterval;
        }

        for (var i = 0; i < cameras.Count; i++)
        {
            if (samplers[i] is not { Current: { } reading } sampler)
            {
                continue;
            }

            if (reading.Settled)
            {
                logger.LogInformation("Cooling: sensor {Temp:F1} C settled over {Window:F0} s before cooling", reading.TempC, SensorAmbientSampler.Window.TotalSeconds);
            }
            else
            {
                logger.LogInformation("Cooling: sensor still moving {Spread:F1} C, not taken as ambient", double.IsNaN(sampler.WindowSpreadC) ? 0 : sampler.WindowSpreadC);
            }

            if (onSensorAmbient is not null)
            {
                await onSensorAmbient(i, reading).ConfigureAwait(false);
            }
        }
    }
}
