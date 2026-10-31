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
        // Each camera aims at its own resolved target (WarmUpTarget, the hub's rule too), and one already within
        // CameraReading's tolerance of it has nothing to warm, as the hub's WarmCameraAsync decides with the same inputs.
        var ambient = new SetpointTemp(sbyte.MinValue, SetpointTempKind.Ambient);
        var scopes = Setup.Telescopes.Length;
        var targets = new SetpointTemp[scopes];
        var labels = new string[scopes];
        var anyToWarm = false;
        for (var i = 0; i < scopes; i++)
        {
            var camera = Setup.Telescopes[i].Camera;
            var heatsink = await PollDriverReadAsyncIf(camera.Driver, camera.Driver.CanGetHeatsinkTemperature, camera.Driver.GetHeatSinkTemperatureAsync, double.NaN, CancellationToken.None);
            SensorAmbientReading? preCool = DeviceHub is { } hub && hub.TryGetPreCoolReading(camera.Device.DeviceUri, out var recorded) ? recorded : null;
            // The rig's own ambient (focuser, then weather) is the target's source for a camera with no heat sink, read here, never driver to driver.
            var rigAmbient = double.IsFinite(heatsink) ? double.NaN : await ReadRigAmbientAsync(Setup.Telescopes[i], CancellationToken.None);
            var resolved = WarmUpTarget.Resolve(heatsink, preCool, rigAmbient);
            _logger.LogInformation("Warming {Camera} to {TargetC:F1} C: {Source}", camera.Device.DeviceUri, resolved.TempC, resolved.Describe());
            var current = await PollDriverReadAsync(camera.Driver, camera.Driver.GetCCDTemperatureAsync, double.NaN, CancellationToken.None);
            // A cooler that reads off is held, never switched on to ramp (the hub's WarmCameraAsync skips it the same way);
            // one that cannot be read is assumed on, so a cold sensor is never left unwarmed.
            var coolerOn = await PollDriverReadAsyncIf(camera.Driver, camera.Driver.CanGetCoolerOn, camera.Driver.GetCoolerOnAsync, true, CancellationToken.None);
            if (!CameraReading.NeedsWarmUpFrom(coolerOn, current, heatsink, preCool, rigAmbient))
            {
                // Held at the sensor's own reading, which matters only on a rig where another camera ramps: this one then
                // rides along with that camera's steps.
                _logger.LogInformation("Camera {Camera} reads {Current:F1} C with its cooler {Cooler}, nothing to warm", camera.Device.DeviceUri, current, coolerOn ? "on" : "off");
                targets[i] = new SetpointTemp(sbyte.MinValue, SetpointTempKind.CCD);
                labels[i] = "already warm";
                continue;
            }

            anyToWarm = true;
            if (resolved.Source is WarmUpTargetSource.HeatSink)
            {
                targets[i] = ambient;
                labels[i] = "the heat sink";
            }
            else
            {
                targets[i] = new SetpointTemp(CameraCoolingRamp.TargetOf(resolved.TempC), SetpointTempKind.Normal);
                labels[i] = resolved.Source switch
                {
                    WarmUpTargetSource.RigAmbient => "the rig's ambient",
                    WarmUpTargetSource.SettledSensorBeforeCooling => "the settled sensor",
                    _ => "fallback"
                };
            }
        }

        if (!anyToWarm)
        {
            // Nothing to warm on any camera, so no ramp at all, as the hub's WarmCameraAsync skips its own: a held sensor
            // target would still cost the ramp's confirming steps (45 s) on a cooler working above the ramp's power exit,
            // or one that cannot say its power. The warm-up is still the recorded intent, as a ramp would record it.
            RecordCoolerIntent(ambient);
            _logger.LogInformation("Cooling: every camera is already at its warm-up target, skipping the warm-up ramp.");
            return true;
        }

        return await CoolCamerasToSetpointAsync(ambient, maxRateCPerMinute, CameraCoolingRamp.WarmUpPowerThreshold, SetupointDirection.Up, CancellationToken.None, targets, labels).ConfigureAwait(false);
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

        RecordCoolerIntent(desiredSetpointTemp);

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

    /// <summary>
    /// Records a ramp to <paramref name="target"/> as every camera's cooler intent. The ramp's TARGET is what a node that
    /// crashed part-way re-establishes, never the step it had reached (the crash journal, P1 of
    /// docs/plans/hardware-in-the-server.md). Holding at the sensor's own temperature names no target, so it records nothing.
    /// </summary>
    private void RecordCoolerIntent(SetpointTemp target)
    {
        if (DeviceHub is { } hub && IntentOf(target) is { } intent)
        {
            for (var i = 0; i < Setup.Telescopes.Length; i++)
            {
                hub.SetCoolerIntent(Setup.Telescopes[i].Camera.Device.DeviceUri, intent);
            }
        }
    }

    /// <summary>
    /// The rig's ambient for one OTA's camera (<see cref="RigAmbient"/>), most trusted first: the OTA's focuser
    /// temperature, then the profile's weather device. NaN when neither answers. A power box's ambient channel is one more
    /// entry here once a profile holds power boxes (#1734).
    /// </summary>
    internal ValueTask<double> ReadRigAmbientAsync(OTA telescope, CancellationToken cancellationToken)
    {
        var sources = new System.Collections.Generic.List<Func<CancellationToken, ValueTask<double>>>(2);

        if (telescope.Focuser?.Driver is { Connected: true } focuser)
        {
            sources.Add(ct => PollDriverReadAsync(focuser, focuser.GetTemperatureAsync, double.NaN, ct));
        }

        if (Setup.Weather?.Driver is { Connected: true } weather)
        {
            sources.Add(_ => ValueTask.FromResult(weather.Temperature));
        }

        return RigAmbient.FirstPlausibleAsync(sources, cancellationToken);
    }

    /// <summary>What a ramp to <paramref name="target"/> asks of a cooler, or null for one that names no target.</summary>
    internal static CoolerIntent? IntentOf(SetpointTemp target) => target.Kind switch
    {
        SetpointTempKind.Normal => CoolerIntent.CoolTo(target.TempC),
        SetpointTempKind.Ambient => CoolerIntent.Warm,
        _ => null,
    };



}
