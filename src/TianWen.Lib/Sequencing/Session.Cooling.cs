using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;

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
    /// Warms cameras back to ambient temperature. Not cancellable; abruptly cutting power
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

        // The ramp's TARGET is what a node that crashed part-way re-establishes, never the step it had reached
        // (the crash journal, P1 of docs/plans/hardware-in-the-server.md). Holding at the sensor's own temperature
        // names no target, so it records nothing.
        if (DeviceHub is { } hub && IntentOf(desiredSetpointTemp) is { } intent)
        {
            for (var i = 0; i < scopes; i++)
            {
                hub.SetCoolerIntent(Setup.Telescopes[i].Camera.Device.DeviceUri, intent);
            }
        }

        var targetLabel = desiredSetpointTemp.Kind switch
        {
            SetpointTempKind.Normal => $"{desiredSetpointTemp.TempC}\u00B0C",
            SetpointTempKind.Ambient => "ambient",
            _ => "sensor"
        };

        var cameras = new ICameraDriver[scopes];
        for (var i = 0; i < scopes; i++)
        {
            cameras[i] = Setup.Telescopes[i].Camera.Driver;
        }

        // The ramp itself is the one every host uses (CameraCoolingRamp); what is the session's is the telemetry.
        return await CameraCoolingRamp.RunAsync(cameras, desiredSetpointTemp, totalRampTime, thresPower, direction, _timeProvider, _logger,
            async (i, driver, ct) =>
            {
                // Record cooling sample for the live session graph. These run every step (15 s) for the whole ramp
                // (20-30 min typical), so a USB drop here is exactly the kind of silent cumulative failure that
                // PollDriverReadAsync is designed for.
                var ccdTemp = await PollDriverReadAsync(driver, driver.GetCCDTemperatureAsync, double.NaN, ct);
                var setpoint = await PollDriverReadAsyncIf(driver, driver.CanSetCCDTemperature, driver.GetSetCCDTemperatureAsync, double.NaN, ct);
                var power = await PollDriverReadAsyncIf(driver, driver.CanGetCoolerPower, driver.GetCoolerPowerAsync, double.NaN, ct);
                if (!double.IsNaN(ccdTemp))
                {
                    _coolingSamples.Enqueue(new CoolingSample(_timeProvider.GetUtcNow(), i, ccdTemp, double.IsNaN(setpoint) ? 0 : setpoint, double.IsNaN(power) ? 0 : power));
                    _currentActivity = $"{ccdTemp:F0}\u00B0C \u2192 {targetLabel} ({(double.IsNaN(power) ? 0 : power):F0}% power)";
                }
            }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a ramp to <paramref name="target"/> asks of a cooler, or null for one that names no target.</summary>
    internal static CoolerIntent? IntentOf(SetpointTemp target) => target.Kind switch
    {
        SetpointTempKind.Normal => CoolerIntent.CoolTo(target.TempC),
        SetpointTempKind.Ambient => CoolerIntent.Warm,
        _ => null,
    };



}
