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
    /// <summary>A step every 15 s, as N.I.N.A. does: each step moves a setpoint about one degree.</summary>
    public static readonly TimeSpan StepInterval = TimeSpan.FromSeconds(15);

    /// <summary>The cooler power a cool-down stops pushing at: what every session cool-down passes.</summary>
    public const double CoolDownPowerThreshold = 80;

    /// <summary>
    /// The whole degree a ramp to <paramref name="setpointC"/> drives a sensor to, which is also the cooler intent it
    /// records. <see cref="sbyte.MinValue"/> is the ramp's "no value" for the sensor and ambient kinds, so never a target.
    /// </summary>
    public static sbyte TargetOf(double setpointC) => (sbyte)Math.Clamp(Math.Round(setpointC), sbyte.MinValue + 1, sbyte.MaxValue);

    /// <summary>
    /// Ramps every camera in <paramref name="cameras"/> towards <paramref name="target"/> together, a step each per
    /// <see cref="StepInterval"/>, over at least <paramref name="totalRampTime"/>, and gives up at twice the ramp it
    /// planned.
    /// </summary>
    /// <param name="target">The setpoint: <see cref="SetpointTempKind.Normal"/> is its value,
    /// <see cref="SetpointTempKind.CCD"/> the sensor's own temperature, otherwise ambient (where the camera can say).</param>
    /// <param name="totalRampTime">Total time budget for the ramp (not per step).</param>
    /// <param name="afterStep">Called for each camera (by index) after each of its steps: a session samples its
    /// cooling telemetry here.</param>
    /// <returns>True if every coolable camera reached the setpoint.</returns>
    public static async ValueTask<bool> RunAsync(
        IReadOnlyList<ICameraDriver> cameras,
        SetpointTemp target,
        TimeSpan totalRampTime,
        double thresPower,
        SetupointDirection direction,
        ITimeProvider timeProvider,
        ILogger logger,
        Func<int, ICameraDriver, CancellationToken, ValueTask>? afterStep,
        CancellationToken cancellationToken)
    {
        var coolingStates = new CameraCoolingState[cameras.Count];

        // Estimate step count from initial temperature delta to compute per-step sleep.
        // CoolToSetpointAsync adjusts by ~1°C per call, so steps ≈ |delta|.
        // Clamp to reasonable bounds: at least 1s per step, at most totalRampTime.
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
                var power = await logger.CatchAsync(camera.GetCoolerPowerAsync, cancellationToken, 0.0);
                if (power > 1)
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
        var stepCount = Math.Max((int)Math.Ceiling(maxDelta), 1);
        // Total ramp time = max(the budget asked for, steps x the step interval).
        var actualRampTime = TimeSpan.FromTicks(Math.Max(totalRampTime.Ticks, stepCount * StepInterval.Ticks));

        var accSleep = TimeSpan.Zero;
        do
        {
            for (var i = 0; i < cameras.Count; i++)
            {
                coolingStates[i] = await cameras[i].CoolToSetpointAsync(target, thresPower, direction, coolingStates[i], cancellationToken);
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

            accSleep += StepInterval;
            if (accSleep >= actualRampTime * 2)
            {
                logger.LogWarning("Cooling: safety cap reached ({AccSleep} >= 2x {ActualRamp}), exiting ramp loop.",
                    accSleep, actualRampTime);
                break;
            }

            await timeProvider.SleepAsync(StepInterval, cancellationToken).ConfigureAwait(false);
        } while (true);

        return coolingStates.All(state => !(state.IsCoolable ?? false) || (state.TargetSetpointReached ?? false));
    }
}
