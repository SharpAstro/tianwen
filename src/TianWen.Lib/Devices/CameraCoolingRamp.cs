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
    /// How far one step moves a setpoint past the sensor's reading at <paramref name="maxRateCPerMinute"/>: the rate times
    /// <see cref="StepInterval"/>, never more than the whole degree a step has always been (a rate of 4 C/min or more is
    /// today's ramp).
    /// </summary>
    public static double MaxStepOf(double maxRateCPerMinute)
        => Math.Clamp(maxRateCPerMinute * StepInterval.TotalMinutes, 0.01, 1d);

    /// <summary>
    /// Ramps every camera in <paramref name="cameras"/> towards <paramref name="target"/> together, a step each per
    /// <see cref="StepInterval"/>, moving the sensor no faster than <paramref name="maxRateCPerMinute"/>, and gives up at
    /// twice the time the drop takes at that rate.
    /// </summary>
    /// <param name="target">The setpoint: <see cref="SetpointTempKind.Normal"/> is its value,
    /// <see cref="SetpointTempKind.CCD"/> the sensor's own temperature, otherwise ambient (where the camera can say).</param>
    /// <param name="maxRateCPerMinute">The most the sensor may move per minute, degrees C: a ceiling, so a small drop is never
    /// stretched to fill a time and a cooler slower than it is not hurried.</param>
    /// <param name="afterStep">Called for each camera (by index) after each of its steps: a session samples its
    /// cooling telemetry here.</param>
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
        CancellationToken cancellationToken)
    {
        var coolingStates = new CameraCoolingState[cameras.Count];

        // The size of the drop, from the initial temperature delta: it sets the cap below.
        var maxDelta = 1.0;
        foreach (var camera in cameras)
        {
            var ccdTemp = await logger.CatchAsync(camera.GetCCDTemperatureAsync, cancellationToken, double.NaN);
            if (!double.IsNaN(ccdTemp))
            {
                var targetC = target.Kind switch
                {
                    SetpointTempKind.Normal => (double)target.TempC,
                    _ => ccdTemp // CCD/Ambient: target will be resolved later, estimate delta = 0
                };
                maxDelta = Math.Max(maxDelta, Math.Abs(ccdTemp - targetC));
            }
        }
        // For CCD/Ambient kinds: check if cooler is even on before ramping
        if (target.Kind is not SetpointTempKind.Normal && maxDelta <= 1)
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
            maxDelta = 30;
        }
        // The ceiling bounds how far one step may move the setpoint; a rate at or above a degree per step is the old whole degree.
        var maxStepC = MaxStepOf(maxRateCPerMinute);
        // A camera that takes only whole degrees cannot be given a fractional step: it keeps its whole degree and holds each
        // setpoint for 1 C / rate minutes, a whole number of StepIntervals (rounded up, so never faster than the ceiling).
        var stepsPerSetpoint = new int[cameras.Count];
        var stepC = new double[cameras.Count];
        var plannedSteps = 0L;
        for (var i = 0; i < cameras.Count; i++)
        {
            if (cameras[i].SetpointResolutionC >= 1d)
            {
                stepC[i] = 1d;
                stepsPerSetpoint[i] = Math.Max(1, (int)Math.Ceiling(1d / (Math.Max(maxRateCPerMinute, 0.01) * StepInterval.TotalMinutes) - 1e-9));
            }
            else
            {
                stepC[i] = maxStepC;
                stepsPerSetpoint[i] = 1;
            }
            plannedSteps = Math.Max(plannedSteps, (long)Math.Ceiling(maxDelta / stepC[i]) * stepsPerSetpoint[i]);
        }
        // The time the drop takes at that ceiling, and the cap is twice it: a cooler that lags the setpoint is given room,
        // one that never gets there is still let go.
        var plannedRampTime = TimeSpan.FromTicks(plannedSteps * StepInterval.Ticks);

        var accSleep = TimeSpan.Zero;
        var iteration = 0;
        do
        {
            for (var i = 0; i < cameras.Count; i++)
            {
                // A whole-degree camera takes a new setpoint only every stepsPerSetpoint steps; the others it is held, and polled.
                if (iteration % stepsPerSetpoint[i] == 0)
                {
                    coolingStates[i] = await cameras[i].CoolToSetpointAsync(target, thresPower, direction, coolingStates[i], stepC[i], cancellationToken);
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
            if (accSleep >= plannedRampTime * 2)
            {
                logger.LogWarning("Cooling: safety cap reached ({AccSleep} >= 2x {PlannedRamp}), exiting ramp loop.",
                    accSleep, plannedRampTime);
                break;
            }

            await timeProvider.SleepAsync(StepInterval, cancellationToken).ConfigureAwait(false);
        } while (true);

        return coolingStates.All(state => !(state.IsCoolable ?? false) || (state.TargetSetpointReached ?? false));
    }
}
