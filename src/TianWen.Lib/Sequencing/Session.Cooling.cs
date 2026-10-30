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
    /// <param name="maxRateCPerMinute">The most the sensor may move per minute, degrees C.</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal ValueTask<bool> CoolCamerasToSensorTempAsync(double maxRateCPerMinute, CancellationToken cancellationToken)
        => CoolCamerasToSetpointAsync(new SetpointTemp(sbyte.MinValue, SetpointTempKind.CCD), maxRateCPerMinute, 0.1, SetupointDirection.Up, cancellationToken);


    /// <summary>
    /// Warms cameras back to ambient temperature. Not cancellable; abruptly cutting power
    /// to a cooled sensor risks thermal shock and condensation damage.
    /// </summary>
    /// <param name="maxRateCPerMinute">The most the sensor may move per minute, degrees C.</param>
    /// <returns>True if ambient temperature was reached.</returns>
    internal async ValueTask<bool> CoolCamerasToAmbientAsync(double maxRateCPerMinute)
    {
        // Each camera aims at its own resolved target (WarmUpTarget, the hub's rule too): a camera with no heat sink
        // used to resolve NaN in the driver and never warm at all.
        var scopes = Setup.Telescopes.Length;
        var targets = new SetpointTemp[scopes];
        var labels = new string[scopes];
        for (var i = 0; i < scopes; i++)
        {
            var camera = Setup.Telescopes[i].Camera;
            var heatsink = await PollDriverReadAsyncIf(camera.Driver, camera.Driver.CanGetHeatsinkTemperature, camera.Driver.GetHeatSinkTemperatureAsync, double.NaN, CancellationToken.None);
            SensorAmbientReading? preCool = DeviceHub is { } hub && hub.TryGetPreCoolReading(camera.Device.DeviceUri, out var recorded) ? recorded : null;
            var resolved = WarmUpTarget.Resolve(heatsink, preCool);
            _logger.LogInformation("Warming {Camera} to {TargetC:F1} C: {Source}", camera.Device.DeviceUri, resolved.TempC, resolved.Describe());
            if (resolved.Source is WarmUpTargetSource.HeatSink)
            {
                targets[i] = new SetpointTemp(sbyte.MinValue, SetpointTempKind.Ambient);
                labels[i] = "the heat sink";
            }
            else
            {
                targets[i] = new SetpointTemp(CameraCoolingRamp.TargetOf(resolved.TempC), SetpointTempKind.Normal);
                labels[i] = resolved.Source is WarmUpTargetSource.SettledSensorBeforeCooling ? "the settled sensor" : "fallback";
            }
        }

        return await CoolCamerasToSetpointAsync(new SetpointTemp(sbyte.MinValue, SetpointTempKind.Ambient), maxRateCPerMinute, CameraCoolingRamp.WarmUpPowerThreshold, SetupointDirection.Up, CancellationToken.None, targets, labels).ConfigureAwait(false);
    }

    /// <summary>
    /// Ramps camera cooling/warming to the target temperature no faster than a maximum rate.
    /// Each step (<see cref="CameraCoolingRamp.StepInterval"/>) moves the setpoint by at most one degree and at most the rate allows.
    /// </summary>
    /// <param name="desiredSetpointTemp">Desired degrees Celcius setpoint temperature,
    /// if <paramref name="desiredSetpointTemp"/>'s <see cref="SetpointTemp.Kind"/> is <see cref="SetpointTempKind.CCD" /> then sensor temperature is chosen,
    /// if its <see cref="SetpointTempKind.Normal" /> then the temp value is chosen
    /// or else ambient temperature is chosen (if available)</param>
    /// <param name="maxRateCPerMinute">The most the sensor may move per minute, degrees C.</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal async ValueTask<bool> CoolCamerasToSetpointAsync(
        SetpointTemp desiredSetpointTemp,
        double maxRateCPerMinute,
        double thresPower,
        SetupointDirection direction,
        CancellationToken cancellationToken,
        SetpointTemp[]? perCameraTargets = null,
        string[]? perCameraLabels = null
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
        return await CameraCoolingRamp.RunAsync(cameras, desiredSetpointTemp, maxRateCPerMinute, thresPower, direction, _timeProvider, _logger,
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
                    var label = perCameraLabels is { } labels && i < labels.Length ? labels[i] : targetLabel;
                    _currentActivity = $"{ccdTemp:F0}\u00B0C \u2192 {label} ({(double.IsNaN(power) ? 0 : power):F0}% power)";
                }
            },
            (i, reading) =>
            {
                // The sensor a cool-down started from, beside the cooler intent: a warm-up with no heat sink aims at it.
                DeviceHub?.SetPreCoolReading(Setup.Telescopes[i].Camera.Device.DeviceUri, reading);
                return ValueTask.CompletedTask;
            }, perCameraTargets, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a ramp to <paramref name="target"/> asks of a cooler, or null for one that names no target.</summary>
    internal static CoolerIntent? IntentOf(SetpointTemp target) => target.Kind switch
    {
        SetpointTempKind.Normal => CoolerIntent.CoolTo(target.TempC),
        SetpointTempKind.Ambient => CoolerIntent.Warm,
        _ => null,
    };



}
