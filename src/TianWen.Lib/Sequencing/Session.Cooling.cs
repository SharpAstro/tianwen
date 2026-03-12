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
    /// Attention: Cannot be cancelled (as it would possibly destroy the cameras)
    /// </summary>
    /// <param name="rampTime">Interval between temperature checks</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal ValueTask<bool> CoolCamerasToAmbientAsync(TimeSpan rampTime)
        => CoolCamerasToSetpointAsync(new SetpointTemp(sbyte.MinValue, SetpointTempKind.Ambient), rampTime, 0.1, SetupointDirection.Up, CancellationToken.None);

    /// <summary>
    /// Assumes that power is on (c.f. <see cref="CoolCamerasToSensorTempAsync(TimeSpan, CancellationToken)"/>).
    /// </summary>
    /// <param name="desiredSetpointTemp">Desired degrees Celcius setpoint temperature,
    /// if <paramref name="desiredSetpointTemp"/>'s <see cref="SetpointTemp.Kind"/> is <see cref="SetpointTempKind.CCD" /> then sensor temperature is chosen,
    /// if its <see cref="SetpointTempKind.Normal" /> then the temp value is chosen
    /// or else ambient temperature is chosen (if available)</param>
    /// <param name="rampInterval">interval to wait until further adjusting setpoint.</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal async ValueTask<bool> CoolCamerasToSetpointAsync(
        SetpointTemp desiredSetpointTemp,
        TimeSpan rampInterval,
        double thresPower,
        SetupointDirection direction,
        CancellationToken cancellationToken
    )
    {
        var scopes = Setup.Telescopes.Count;
        var coolingStates = new CameraCoolingState[scopes];

        var accSleep = TimeSpan.Zero;
        do
        {
            for (var i = 0; i < Setup.Telescopes.Count; i++)
            {
                var camera = Setup.Telescopes[i].Camera;
                coolingStates[i] = await camera.Driver.CoolToSetpointAsync(desiredSetpointTemp, thresPower, direction, coolingStates[i], cancellationToken);
            }

            accSleep += rampInterval;
            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, quitting cooldown loop");
                break;
            }
            else
            {
                await External.SleepAsync(rampInterval, cancellationToken).ConfigureAwait(false);
            }
        } while (coolingStates.Any(state => state.IsRamping) && accSleep < rampInterval * 100 && !cancellationToken.IsCancellationRequested);

        return coolingStates.All(state => !(state.IsCoolable ?? false) || (state.TargetSetpointReached ?? false));
    }



}
