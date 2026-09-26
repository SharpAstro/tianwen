using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TianWen.Lib.Devices;

/// <summary>
/// Whether a connected device can be disconnected now. Cameras are the concern: a cold disconnect risks
/// thermal shock, a busy one interrupts an exposure. Anything that is not a camera is <see cref="Safe"/>.
/// </summary>
public enum DisconnectSafety
{
    /// <summary>Safe to disconnect immediately (not a camera, or cooler off and idle).</summary>
    Safe,
    /// <summary>Camera cooler is on: needs warm-up ramp before disconnect.</summary>
    CoolerOn,
    /// <summary>Camera is mid-exposure / downloading: should finish before disconnect.</summary>
    Busy,
    /// <summary>Both cooler on and camera busy.</summary>
    BusyAndCool,
    /// <summary>State could not be read (driver error); caller should treat as unsafe.</summary>
    Unknown
}

/// <summary>
/// Stopping a camera without harming it, for every host: the disconnect-safety read, the out-of-session
/// warm-up ramp, and the shutdown sequence that stops every connected camera.
/// </summary>
/// <remarks>
/// These are device-model operations, so they live in Lib and not in a UI project: the headless server
/// stops its cameras on exactly the rules the GUI and the TUI do, and a second copy of a thermal ramp is
/// a second answer to how fast a sensor may warm (P0b of docs/plans/hardware-in-the-server.md, #752).
/// </remarks>
public static class DeviceHubCameraSafetyExtensions
{
    extension(IDeviceHub hub)
    {
        /// <summary>
        /// Reads camera state and cooler status to decide whether the device can be disconnected now
        /// without a warm-up or interrupting work in flight. <see cref="DisconnectSafety.Safe"/> for
        /// anything that is not a camera. A HARDWARE check only: whether a run owns the device is the
        /// ownership gate's question, never this one.
        /// </summary>
        public async ValueTask<DisconnectSafety> GetDisconnectSafetyAsync(Uri deviceUri, CancellationToken cancellationToken = default)
        {
            if (!hub.TryGetConnectedDriver<ICameraDriver>(deviceUri, out var camera))
            {
                return DisconnectSafety.Safe;
            }

            bool busy, cool = false;
            try
            {
                var state = await camera.GetCameraStateAsync(cancellationToken);
                busy = state is not (CameraState.Idle or CameraState.NotConnected);
            }
            catch
            {
                return DisconnectSafety.Unknown;
            }

            try
            {
                if (camera.CanGetCoolerOn)
                {
                    cool = await camera.GetCoolerOnAsync(cancellationToken);
                }
            }
            catch
            {
                return DisconnectSafety.Unknown;
            }

            return (busy, cool) switch
            {
                (false, false) => DisconnectSafety.Safe,
                (false, true) => DisconnectSafety.CoolerOn,
                (true, false) => DisconnectSafety.Busy,
                (true, true) => DisconnectSafety.BusyAndCool
            };
        }

        /// <summary>
        /// Out-of-session warm-up and disconnect for a single camera. Ramps the setpoint toward the heat
        /// sink (or +25 °C) in 2 °C steps every 30 s, then turns the cooler off and disconnects.
        /// Non-camera devices disconnect directly. The spirit of the session's own warm-up
        /// (<c>Session.Cooling</c>), without its multi-camera orchestration and telemetry.
        /// </summary>
        /// <param name="force">Disconnect even if a run owns the camera. Reserved for the shutdown path;
        /// see <see cref="IDeviceHub.DisconnectAsync"/>.</param>
        public ValueTask WarmAndDisconnectAsync(Uri deviceUri, ITimeProvider timeProvider, ILogger logger, bool force, CancellationToken cancellationToken)
            => WarmCameraAsync(hub, deviceUri, timeProvider, logger, disconnectAfter: true, force, cancellationToken);

        /// <summary>
        /// The warm-up ramp and cooler off without disconnecting, so the camera stays available to cool
        /// again. The same condensation reasoning as <see cref="WarmAndDisconnectAsync"/>.
        /// </summary>
        public ValueTask WarmAndCoolerOffAsync(Uri deviceUri, ITimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
            => WarmCameraAsync(hub, deviceUri, timeProvider, logger, disconnectAfter: false, force: false, cancellationToken);

        /// <summary>
        /// Cools the camera at <paramref name="cameraUri"/> to <paramref name="setpointC"/> (rounded to a whole degree)
        /// through the session's own ramp (<see cref="CameraCoolingRamp"/>), never as a jump, and records it as the
        /// camera's intent. For a host re-establishing a cooler with no session to do it: a node after the node before it
        /// crashed (the crash journal, P1 of docs/plans/hardware-in-the-server.md).
        /// </summary>
        /// <returns>True when the setpoint was reached; false when it was not, or the camera is not connected.</returns>
        public async ValueTask<bool> CoolToSetpointAsync(Uri cameraUri, double setpointC, TimeSpan totalRampTime, ITimeProvider timeProvider, ILogger logger,
            CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<ICameraDriver>(cameraUri, out var camera))
            {
                return false;
            }

            // sbyte.MinValue is the ramp's "no value" for the sensor and ambient kinds, so never a target.
            var target = (sbyte)Math.Clamp(Math.Round(setpointC), sbyte.MinValue + 1, sbyte.MaxValue);
            hub.SetCoolerIntent(cameraUri, CoolerIntent.CoolTo(target));
            return await CameraCoolingRamp.RunAsync([camera], new SetpointTemp(target, SetpointTempKind.Normal), totalRampTime,
                CameraCoolingRamp.CoolDownPowerThreshold, SetupointDirection.Down, timeProvider, logger, afterStep: null, cancellationToken);
        }

        /// <summary>
        /// Records, as the camera's <see cref="CoolerIntent"/>, what an IMMEDIATE command left its cooler doing:
        /// cooling to its setpoint, or off. For a surface that commands the cooler directly, a device plane or a
        /// compatibility shim, rather than through a ramp, whose TARGET is the intent instead. A camera that cannot
        /// say whether its cooler is on records nothing: an intent guessed is worse than none.
        /// </summary>
        public async ValueTask RecordCommandedCoolerAsync(Uri cameraUri, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<ICameraDriver>(cameraUri, out var camera) || !camera.CanGetCoolerOn)
            {
                return;
            }

            if (!await camera.GetCoolerOnAsync(cancellationToken))
            {
                hub.SetCoolerIntent(cameraUri, CoolerIntent.Off);
            }
            else if (camera.CanSetCCDTemperature)
            {
                hub.SetCoolerIntent(cameraUri, CoolerIntent.CoolTo(await camera.GetSetCCDTemperatureAsync(cancellationToken)));
            }
        }

        /// <summary>
        /// Disconnects every connected camera for good, warming first each one whose cooler is on, all
        /// at once. The last step of stopping a rig, for a host that is shutting down: call it only once
        /// every run has ENDED, since a run's own <c>Finalise</c> warms its cameras and two ramps on one
        /// camera fight. Never throws; a camera that fails to stop is logged.
        /// </summary>
        /// <remarks>
        /// It forces past a lease, because a run that failed to release one must not keep the process from
        /// exiting (force is for shutdown only, and this is it), and its ramps take no token, because a
        /// thermal ramp that has started must finish whatever else is being torn down.
        /// </remarks>
        /// <param name="progress">A one-line status as it goes (how many cameras, which one it warms).
        /// Called from whichever thread the stop is on.</param>
        public async Task StopConnectedCamerasAsync(ITimeProvider timeProvider, ILogger logger, Action<string>? progress = null)
        {
            var cameras = new List<(Uri Uri, string Name)>();
            foreach (var (uri, driver) in hub.ConnectedDevices)
            {
                if (driver is ICameraDriver)
                {
                    cameras.Add((uri, hub.TryGetDeviceFromUri(uri, out var device) ? device.DisplayName : uri.Host));
                }
            }

            if (cameras.Count == 0)
            {
                return;
            }

            progress?.Invoke(cameras.Count == 1 ? $"Disconnecting {cameras[0].Name}" : $"Disconnecting {cameras.Count} cameras");
            var stops = new Task[cameras.Count];
            for (var i = 0; i < cameras.Count; i++)
            {
                stops[i] = StopCameraAsync(hub, cameras[i].Uri, cameras[i].Name, timeProvider, logger, progress);
            }
            await Task.WhenAll(stops);
        }
    }

    private static async Task StopCameraAsync(IDeviceHub hub, Uri uri, string name, ITimeProvider timeProvider, ILogger logger, Action<string>? progress)
    {
        try
        {
            if (await hub.GetDisconnectSafetyAsync(uri, CancellationToken.None) == DisconnectSafety.Safe)
            {
                await hub.DisconnectAsync(uri, force: true, CancellationToken.None);
            }
            else
            {
                progress?.Invoke($"Warming {name}");
                await hub.WarmAndDisconnectAsync(uri, timeProvider, logger, force: true, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Shutdown: stopping camera {Uri} failed", uri);
        }
    }

    // Steps the setpoint toward the heat sink (or +25 °C) in 2 °C / 30 s increments, capped at 15 min, then
    // turns the cooler off and, when asked, disconnects.
    private static async ValueTask WarmCameraAsync(
        IDeviceHub hub, Uri deviceUri, ITimeProvider timeProvider, ILogger logger, bool disconnectAfter, bool force, CancellationToken cancellationToken)
    {
        // A camera a run holds is the run's to warm (its Finalise does). Refused BEFORE the ramp: the ramp used
        // to run first, and only the disconnect at its end was refused, so a run's cooled camera warmed under it
        // (P0c item 2 of docs/plans/hardware-in-the-server.md). The shutdown path forces past, and nothing else.
        if (!force && hub.TryGetLease(deviceUri, out var lease))
        {
            throw new DeviceLeasedException(lease);
        }

        if (!hub.TryGetConnectedDriver<ICameraDriver>(deviceUri, out var camera))
        {
            if (disconnectAfter) await hub.DisconnectAsync(deviceUri, force, cancellationToken);
            return;
        }

        // Skip the ramp entirely when the cooler was never on -- the whole
        // point of the ramp is condensation-mitigation as the sensor returns
        // to ambient, and a never-cooled camera has nothing to mitigate. The
        // previous implementation walked the full 30-step / 30s loop with a
        // 25C heat-sink fallback whenever GetHeatsinkTemperature was
        // unsupported, producing ~2.5 minutes of "Warming cameras..." per
        // camera against fakes / drivers without thermal telemetry.
        if (camera.CanGetCoolerOn)
        {
            bool coolerOn;
            try { coolerOn = await camera.GetCoolerOnAsync(cancellationToken); }
            catch (Exception ex)
            {
                // If we can't read the cooler state, fall through to the ramp
                // -- safer to over-wait than to thermal-shock a real sensor.
                logger.LogWarning(ex, "GetCoolerOnAsync failed for {Uri}; running warm-up ramp defensively", deviceUri);
                coolerOn = true;
            }
            if (!coolerOn)
            {
                logger.LogInformation("Camera cooler is off for {Uri}; skipping warm-up ramp", deviceUri);
                hub.SetCoolerIntent(deviceUri, CoolerIntent.Off);
                if (disconnectAfter) await hub.DisconnectAsync(deviceUri, force, cancellationToken);
                return;
            }
        }

        // What a node that crashed mid-ramp re-establishes: the warm-up, from wherever the sensor then is, never a
        // cool-down back to the setpoint this ramp is leaving.
        hub.SetCoolerIntent(deviceUri, CoolerIntent.Warm);

        // Determine target temperature: heat-sink if available, else +25°C.
        double target = 25.0;
        if (camera.CanGetHeatsinkTemperature)
        {
            try { target = await camera.GetHeatSinkTemperatureAsync(cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "GetHeatSinkTemperatureAsync failed for {Uri}", deviceUri); }
        }

        var stepInterval = TimeSpan.FromSeconds(30);
        var stepSize = 2.0;
        var stallThreshold = 1.0;
        var maxSteps = 30;

        for (var i = 0; i < maxSteps && !cancellationToken.IsCancellationRequested; i++)
        {
            double current;
            try { current = await camera.GetCCDTemperatureAsync(cancellationToken); }
            catch { break; }

            if (current >= target - stallThreshold) break;

            var nextSetpoint = Math.Min(current + stepSize, target);
            try { await camera.SetSetCCDTemperatureAsync(nextSetpoint, cancellationToken); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SetSetCCDTemperatureAsync failed mid-ramp for {Uri}", deviceUri);
                break;
            }

            await timeProvider.SleepAsync(stepInterval, cancellationToken);
        }

        try { await camera.SetCoolerOnAsync(false, cancellationToken); }
        catch (Exception ex) { logger.LogWarning(ex, "SetCoolerOnAsync(false) failed for {Uri}", deviceUri); }
        hub.SetCoolerIntent(deviceUri, CoolerIntent.Off);

        await timeProvider.SleepAsync(TimeSpan.FromSeconds(2), cancellationToken);

        if (disconnectAfter)
        {
            await hub.DisconnectAsync(deviceUri, force, cancellationToken);
        }
    }
}
