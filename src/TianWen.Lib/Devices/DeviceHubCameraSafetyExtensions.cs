using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Sequencing;

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
        /// Out-of-session warm-up and disconnect for a single camera. Warms it through
        /// <see cref="CameraCoolingRamp"/>, the session's own warm-up (toward the heat sink, or +25 °C, a step every
        /// <see cref="CameraCoolingRamp.StepInterval"/>, ended early once the cooler no longer holds the sensor down),
        /// then turns the cooler off and disconnects. Non-camera devices disconnect directly.
        /// </summary>
        /// <param name="force">Disconnect even if a run owns the camera. Reserved for the shutdown path;
        /// see <see cref="IDeviceHub.DisconnectAsync"/>.</param>
        public ValueTask WarmAndDisconnectAsync(Uri deviceUri, ITimeProvider timeProvider, ILogger logger, bool force, CancellationToken cancellationToken)
            => WarmCameraAsync(hub, deviceUri, timeProvider, logger, disconnectAfter: true, force, heldBy: null, cancellationToken);

        /// <summary>
        /// The warm-up ramp and cooler off without disconnecting, so the camera stays available to cool
        /// again. The same condensation reasoning as <see cref="WarmAndDisconnectAsync"/>.
        /// </summary>
        public ValueTask WarmAndCoolerOffAsync(Uri deviceUri, ITimeProvider timeProvider, ILogger logger, CancellationToken cancellationToken)
            => WarmCameraAsync(hub, deviceUri, timeProvider, logger, disconnectAfter: false, force: false, heldBy: null, cancellationToken);

        /// <summary>
        /// <see cref="WarmAndCoolerOffAsync(Uri, ITimeProvider, ILogger, CancellationToken)"/> by the run that holds the camera,
        /// as a dark library asked to warm when finished does: <paramref name="heldBy"/> is its lease's
        /// <see cref="DeviceLease.OwnerLabel"/>, and a camera held by anyone else is still refused.
        /// </summary>
        public ValueTask WarmAndCoolerOffAsync(Uri deviceUri, ITimeProvider timeProvider, ILogger logger, string heldBy, CancellationToken cancellationToken)
            => WarmCameraAsync(hub, deviceUri, timeProvider, logger, disconnectAfter: false, force: false, heldBy, cancellationToken);

        /// <summary>
        /// Cools the camera at <paramref name="cameraUri"/> to <paramref name="setpointC"/> (rounded to a whole degree)
        /// through the session's own ramp (<see cref="CameraCoolingRamp"/>), never as a jump, and records it as the
        /// camera's intent. For a host re-establishing a cooler with no session to do it: a node after the node before it
        /// crashed (the crash journal, P1 of docs/plans/hardware-in-the-server.md).
        /// </summary>
        /// <returns>True when the setpoint was reached; false when it was not, or the camera is not connected.</returns>
        public ValueTask<bool> CoolToSetpointAsync(Uri cameraUri, double setpointC, double maxRateCPerMinute, ITimeProvider timeProvider, ILogger logger,
            CancellationToken cancellationToken)
            => hub.CoolToSetpointAsync(cameraUri, setpointC, maxRateCPerMinute, timeProvider, logger, readAmbient: true, cancellationToken);

        /// <summary>
        /// <see cref="CoolToSetpointAsync(Uri, double, double, ITimeProvider, ILogger, CancellationToken)"/>, with the
        /// sensor read as ambient before the first step (<paramref name="readAmbient"/>) or not. A camera whose cooler is
        /// off is sampled for about a minute first and the reading recorded (<see cref="IDeviceHub.SetPreCoolReading"/>);
        /// a node cooling a camera back after a crash passes false, since that sensor is no longer at rest and the
        /// reading the journal held is the one that counts.
        /// </summary>
        public async ValueTask<bool> CoolToSetpointAsync(Uri cameraUri, double setpointC, double maxRateCPerMinute, ITimeProvider timeProvider, ILogger logger,
            bool readAmbient, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<ICameraDriver>(cameraUri, out var camera))
            {
                return false;
            }

            var target = CameraCoolingRamp.TargetOf(setpointC);
            hub.SetCoolerIntent(cameraUri, CoolerIntent.CoolTo(target));
            return await CameraCoolingRamp.RunAsync([camera], new SetpointTemp(target, SetpointTempKind.Normal), maxRateCPerMinute,
                CameraCoolingRamp.CoolDownPowerThreshold, SetupointDirection.Down, timeProvider, logger, afterStep: null,
                readAmbient ? (_, reading) => { hub.SetPreCoolReading(cameraUri, reading); return ValueTask.CompletedTask; } : null, cancellationToken);
        }

        /// <summary>
        /// Switches the camera's cooler off at once, with no warm-up, and records that as its intent, so a node that
        /// crashes afterwards does not re-cool a camera someone switched off. The immediate counterpart to
        /// <see cref="WarmAndCoolerOffAsync"/>, which ramps first for a sensor that is cold.
        /// </summary>
        /// <returns>False when the camera is not connected or cannot switch its cooler.</returns>
        public async ValueTask<bool> CoolerOffAsync(Uri cameraUri, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<ICameraDriver>(cameraUri, out var camera) || !camera.CanSetCoolerOn)
            {
                return false;
            }

            await camera.SetCoolerOnAsync(false, cancellationToken);
            hub.SetCoolerIntent(cameraUri, CoolerIntent.Off);
            return true;
        }

        /// <summary>
        /// Records, as the camera's <see cref="CoolerIntent"/>, what an IMMEDIATE command left its cooler doing:
        /// cooling to its setpoint, or off. For a surface that commands the cooler directly, a device plane or a
        /// compatibility shim, rather than through a ramp, whose TARGET is the intent instead. A camera that cannot
        /// say whether its cooler is on, or names no setpoint, records nothing: an intent guessed is worse than none.
        /// </summary>
        /// <remarks>
        /// Never fails the command it follows: that has succeeded by the time this reads the cooler back, so a read that
        /// fails is logged and the camera keeps the intent it had. It once turned a successful Alpaca <c>cooleron</c>
        /// into a fault the client would retry.
        /// </remarks>
        public async ValueTask RecordCommandedCoolerAsync(Uri cameraUri, ILogger logger, CancellationToken cancellationToken)
        {
            if (!hub.TryGetConnectedDriver<ICameraDriver>(cameraUri, out var camera) || !camera.CanGetCoolerOn)
            {
                return;
            }

            try
            {
                if (!await camera.GetCoolerOnAsync(cancellationToken))
                {
                    hub.SetCoolerIntent(cameraUri, CoolerIntent.Off);
                }
                else if (camera.CanSetCCDTemperature && await camera.GetSetCCDTemperatureAsync(cancellationToken) is var setpoint && double.IsFinite(setpoint))
                {
                    hub.SetCoolerIntent(cameraUri, CoolerIntent.CoolTo(setpoint));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not read back the cooler of {Uri} after a command; it keeps the intent it had", cameraUri);
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

    // Warms through CameraCoolingRamp (the session's own warm-up), then turns the cooler off and, when asked, disconnects.
    private static async ValueTask WarmCameraAsync(
        IDeviceHub hub, Uri deviceUri, ITimeProvider timeProvider, ILogger logger, bool disconnectAfter, bool force, string? heldBy,
        CancellationToken cancellationToken)
    {
        // A camera a run holds is the run's to warm (its Finalise does, a dark library through heldBy). Refused BEFORE
        // the ramp: the ramp used to run first, and only the disconnect at its end was refused, so a run's cooled camera
        // warmed under it (P0c item 2 of docs/plans/hardware-in-the-server.md). The shutdown path forces past, and
        // nothing else.
        if (!force && hub.TryGetLease(deviceUri, out var lease) && !string.Equals(lease.OwnerLabel, heldBy, StringComparison.Ordinal))
        {
            throw new DeviceLeasedException(lease);
        }

        if (!hub.TryGetConnectedDriver<ICameraDriver>(deviceUri, out var camera))
        {
            if (disconnectAfter) await hub.DisconnectAsync(deviceUri, force, cancellationToken);
            return;
        }

        // The ramp's one action is a setpoint, so a camera that takes none (an uncooled planetary camera) has nothing
        // to warm. It used to ramp anyway: a DAL camera without a cooler ignores the setpoint, so the loop waited out
        // its 15 minute cap on a sensor below the +25 °C fallback, and quitting after a planetary capture with an
        // ASI462MC held the window for all of it (the ZWO live check, 2026-09-28).
        if (!camera.CanSetCCDTemperature)
        {
            logger.LogInformation("Camera {Uri} has no cooler; nothing to warm", deviceUri);
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

        // The session's own warm-up, ONE ramp for every host: toward the heat sink (or CameraReading's +25 C fallback),
        // a step every CameraCoolingRamp.StepInterval, ended by the cooler no longer holding the sensor down.
        try
        {
            await CameraCoolingRamp.RunAsync([camera], new SetpointTemp(sbyte.MinValue, SetpointTempKind.Ambient),
                new SessionConfiguration().WarmupMaxRate, CameraCoolingRamp.WarmUpPowerThreshold, SetupointDirection.Up,
                timeProvider, logger, afterStep: null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Warm-up ramp failed mid-ramp for {Uri}", deviceUri);
        }

        // A cancelled warm-up stops where it is: the ramp returns normally on a cancelled token, so it is checked here.
        // The cooler stays on and the intent stays Warm, so a cold sensor is never cut off and a crash journal re-warms it.
        cancellationToken.ThrowIfCancellationRequested();

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
