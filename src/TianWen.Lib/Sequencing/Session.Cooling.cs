using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    /// <summary>
    /// Idea is that we keep cooler on but only on the currently reached temperature, so we have less cases to manage in the imaging loop.
    /// Assumes that power is switched on.
    /// </summary>
    /// <param name="rampTime">Interval between temperature checks</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal ValueTask<bool> CoolCamerasToSensorTempAsync(TimeSpan rampTime, CancellationToken cancellationToken)
        => CoolCamerasToSetpointAsync(new SetpointTemp(sbyte.MinValue, SetpointTempKind.CCD), rampTime, 0.1, SetupointDirection.Up, cancellationToken);


    /// <summary>
    /// Warms cameras back to ambient temperature. Not cancellable — abruptly cutting power
    /// to a cooled sensor risks thermal shock and condensation damage.
    /// </summary>
    /// <param name="rampTime">Interval between temperature checks</param>
    /// <returns>True if ambient temperature was reached.</returns>
    internal ValueTask<bool> CoolCamerasToAmbientAsync(TimeSpan rampTime)
        => CoolCamerasToSetpointAsync(new SetpointTemp(sbyte.MinValue, SetpointTempKind.Ambient), rampTime, 0.1, SetupointDirection.Up, CancellationToken.None);

    /// <summary>
    /// Ramps camera cooling/warming to the target temperature over a total time budget.
    /// Each step adjusts the setpoint by ~1°C, with the sleep interval computed from
    /// <paramref name="totalRampTime"/> / estimated step count.
    /// </summary>
    /// <param name="desiredSetpointTemp">Desired degrees Celcius setpoint temperature,
    /// if <paramref name="desiredSetpointTemp"/>'s <see cref="SetpointTemp.Kind"/> is <see cref="SetpointTempKind.CCD" /> then sensor temperature is chosen,
    /// if its <see cref="SetpointTempKind.Normal" /> then the temp value is chosen
    /// or else ambient temperature is chosen (if available)</param>
    /// <param name="totalRampTime">Total time budget for the ramp (not per-step).</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal async ValueTask<bool> CoolCamerasToSetpointAsync(
        SetpointTemp desiredSetpointTemp,
        TimeSpan totalRampTime,
        double thresPower,
        SetupointDirection direction,
        CancellationToken cancellationToken
    )
    {
        var scopes = Setup.Telescopes.Length;
        var coolingStates = new CameraCoolingState[scopes];

        // Estimate step count from initial temperature delta to compute per-step sleep.
        // CoolToSetpointAsync adjusts by ~1°C per call, so steps ≈ |delta|.
        // Clamp to reasonable bounds: at least 1s per step, at most totalRampTime.
        var maxDelta = 1.0;
        for (var i = 0; i < scopes; i++)
        {
            var camera = Setup.Telescopes[i].Camera;
            var ccdTemp = await External.CatchAsync(camera.Driver.GetCCDTemperatureAsync, cancellationToken, double.NaN);
            if (!double.IsNaN(ccdTemp))
            {
                var target = desiredSetpointTemp.Kind switch
                {
                    SetpointTempKind.Normal => (double)desiredSetpointTemp.TempC,
                    _ => ccdTemp // CCD/Ambient: target will be resolved later, estimate delta = 0
                };
                maxDelta = Math.Max(maxDelta, Math.Abs(ccdTemp - target));
            }
        }
        // For CCD/Ambient kinds: check if cooler is even on before ramping
        if (desiredSetpointTemp.Kind is not SetpointTempKind.Normal && maxDelta <= 1)
        {
            // Check if any camera's cooler is actually on
            var anyCoolerOn = false;
            for (var i = 0; i < scopes; i++)
            {
                var power = await External.CatchAsync(Setup.Telescopes[i].Camera.Driver.GetCoolerPowerAsync, cancellationToken, 0.0);
                if (power > 1)
                {
                    anyCoolerOn = true;
                    break;
                }
            }
            if (!anyCoolerOn)
            {
                External.AppLogger.LogInformation("Cooling: all cameras already at ambient, skipping warmup ramp.");
                return true;
            }
            maxDelta = 30;
        }
        var stepCount = Math.Max((int)Math.Ceiling(maxDelta), 1);
        // Fixed 15-second step interval (like NINA). Total ramp time = max(user config, steps × 15s).
        var rampInterval = TimeSpan.FromSeconds(15);
        var actualRampTime = TimeSpan.FromTicks(Math.Max(totalRampTime.Ticks, stepCount * rampInterval.Ticks));

        var targetLabel = desiredSetpointTemp.Kind switch
        {
            SetpointTempKind.Normal => $"{desiredSetpointTemp.TempC}\u00B0C",
            SetpointTempKind.Ambient => "ambient",
            _ => "sensor"
        };

        var accSleep = TimeSpan.Zero;
        do
        {
            for (var i = 0; i < Setup.Telescopes.Length; i++)
            {
                var camera = Setup.Telescopes[i].Camera;
                coolingStates[i] = await camera.Driver.CoolToSetpointAsync(desiredSetpointTemp, thresPower, direction, coolingStates[i], cancellationToken);

                // Record cooling sample for the live session graph
                var ccdTemp = await External.CatchAsync(camera.Driver.GetCCDTemperatureAsync, cancellationToken, double.NaN);
                var setpoint = await External.CatchAsyncIf(camera.Driver.CanSetCCDTemperature, camera.Driver.GetSetCCDTemperatureAsync, cancellationToken, double.NaN);
                var power = await External.CatchAsyncIf(camera.Driver.CanGetCoolerPower, camera.Driver.GetCoolerPowerAsync, cancellationToken, double.NaN);
                if (!double.IsNaN(ccdTemp))
                {
                    _coolingSamples.Enqueue(new CoolingSample(External.TimeProvider.GetUtcNow(), i, ccdTemp, double.IsNaN(setpoint) ? 0 : setpoint, double.IsNaN(power) ? 0 : power));
                    _currentActivity = $"{ccdTemp:F0}\u00B0C \u2192 {targetLabel} ({(double.IsNaN(power) ? 0 : power):F0}% power)";
                }
            }

            // Check exit condition before sleeping — avoid unnecessary 10s wait when already at target
            if (!coolingStates.Any(state => state.IsRamping) || cancellationToken.IsCancellationRequested)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    External.AppLogger.LogWarning("Cancellation requested, quitting cooldown loop");
                }
                break;
            }

            accSleep += rampInterval;
            var estimatedRampTime = stepCount * rampInterval;
            if (accSleep >= actualRampTime * 2)
            {
                External.AppLogger.LogWarning("Cooling: safety cap reached ({AccSleep} >= 2x {ActualRamp}), exiting ramp loop.",
                    accSleep, actualRampTime);
                break;
            }

            await External.SleepAsync(rampInterval, cancellationToken).ConfigureAwait(false);
        } while (true);

        return coolingStates.All(state => !(state.IsCoolable ?? false) || (state.TargetSetpointReached ?? false));
    }



}
