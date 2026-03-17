using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Devices;
using static TianWen.Lib.Astrometry.CoordinateUtils;

namespace TianWen.Lib.Sequencing;

internal partial record Session
{
    internal async ValueTask CalibrateGuiderAsync(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;

        // TODO: maybe slew slightly above/below 0 declination to avoid trees, etc.
        // slew half an hour east of meridian, plate solve and slew closer
        var dec = 0;
        await mount.Driver.BeginSlewHourAngleDecAsync(TimeSpan.FromMinutes(30).TotalHours, dec, cancellationToken).ConfigureAwait(false);

        if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to slew mount {mount} to guider calibration position (near meridian, {DegreesToDMS(dec)} declination)");
        }

        // TODO: plate solve and sync and reslew

        var guider = Setup.Guider;

        if (!await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Failed to start guider loop of guider {guider.Driver}");
        }
    }

    internal async ValueTask Finalise(CancellationToken cancellationToken)
    {
        External.AppLogger.LogInformation("Executing session run finaliser: Stop guiding, stop tracking, disconnect guider, close covers, cool to ambient temp, turn off cooler, park scope.");

        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var maybeCoversClosed = null as bool?;
        var maybeCooledCamerasToAmbient = null as bool?;

        var guiderStopped = await CatchAsync(async cancellationToken =>
        {
            await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return !await guider.Driver.IsGuidingAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        var trackingStopped = await CatchAsync(async cancellationToken => mount.Driver.CanSetTracking && !await mount.Driver.IsTrackingAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

        if (trackingStopped)
        {
            maybeCoversClosed ??= await CatchAsync(CloseCoversAsync, cancellationToken).ConfigureAwait(false);
            maybeCooledCamerasToAmbient ??= await CatchAsync(TurnOffCameraCoolingAsync, cancellationToken).ConfigureAwait(false);
        }

        var guiderDisconnected = await CatchAsync(guider.Driver.DisconnectAsync, cancellationToken).ConfigureAwait(false);
        bool parkInitiated = Catch(() => mount.Driver.CanPark) && await CatchAsync(mount.Driver.ParkAsync, cancellationToken).ConfigureAwait(false);

        var parkCompleted = parkInitiated && await CatchAsync(async cancellationToken =>
        {
            int i = 0;
            while (!await mount.Driver.AtParkAsync(cancellationToken) && i++ < IDeviceDriver.MAX_FAILSAFE)
            {
                await External.SleepAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            return await mount.Driver.AtParkAsync(cancellationToken);
        }, cancellationToken);

        if (parkCompleted)
        {
            maybeCoversClosed ??= await CatchAsync(CloseCoversAsync, cancellationToken).ConfigureAwait(false);
            maybeCooledCamerasToAmbient ??= await CatchAsync(TurnOffCameraCoolingAsync, cancellationToken).ConfigureAwait(false);
        }

        var coversClosed = maybeCoversClosed ??= await CatchAsync(CloseCoversAsync, cancellationToken).ConfigureAwait(false);
        var cooledCamerasToAmbient = maybeCooledCamerasToAmbient ??= await CatchAsync(TurnOffCameraCoolingAsync, cancellationToken).ConfigureAwait(false);

        var mountDisconnected = await CatchAsync(mount.Driver.DisconnectAsync, cancellationToken).ConfigureAwait(false);

        var shutdownReport = new Dictionary<string, bool>
        {
            ["Covers closed"] = coversClosed,
            ["Tracking stopped"] = trackingStopped,
            ["Guider stopped"] = guiderStopped,
            ["Park initiated"] = parkInitiated,
            ["Park completed"] = parkCompleted,
            ["Camera cooler at ambient"] = cooledCamerasToAmbient,
            ["Guider disconnected"] = guiderDisconnected,
            ["Mount disconnected"] = mountDisconnected
        };

        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            var camDriver = Setup.Telescopes[i].Camera.Driver;
            if (Catch(() => camDriver.CanGetCoolerOn))
            {
                shutdownReport[$"Camera #{(i + 1)} Cooler Off"] = await CatchAsync(async ct =>
                {
                    if (await camDriver.GetCoolerOnAsync(ct))
                    {
                        await camDriver.SetCoolerOnAsync(false, ct);
                    }
                    return !await camDriver.GetCoolerOnAsync(ct);
                }, cancellationToken);
            }
            if (Catch(() => camDriver.CanGetCoolerPower))
            {
                shutdownReport[$"Camera #{(i + 1)} Cooler Power <= 0.1"] = await CatchAsync(async ct => await camDriver.GetCoolerPowerAsync(ct) is <= 0.1, cancellationToken);
            }
            if (Catch(() => camDriver.CanGetHeatsinkTemperature))
            {
                shutdownReport[$"Camera #{(i + 1)} Temp near ambient"] = await CatchAsync(async ct => Math.Abs(await camDriver.GetCCDTemperatureAsync(ct) - await camDriver.GetHeatSinkTemperatureAsync(ct)) < 1d, cancellationToken);
            }
        }

        if (shutdownReport.Values.Any(v => !v))
        {
            External.AppLogger.LogError("Partially failed shut-down of session: {@ShutdownReport}", shutdownReport.Select(p => p.Key + ": " + (p.Value ? "success" : "fail")));
        }
        else
        {
            External.AppLogger.LogInformation("Shutdown complete, session ended. Please turn off mount and camera cooler power.");
        }

        ValueTask<bool> CloseCoversAsync(CancellationToken cancellationToken) => MoveTelescopeCoversToStateAsync(CoverStatus.Closed, cancellationToken);

        ValueTask<bool> TurnOffCameraCoolingAsync(CancellationToken cancellationToken) => CoolCamerasToAmbientAsync(Configuration.WarmupRampInterval);
    }

    /// <summary>
    /// Does one-time (per session) initialisation, e.g. connecting, unparking
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>True if initialisation was successful.</returns>
    internal async ValueTask<bool> InitialisationAsync(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        await mount.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await guider.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);

        if (await mount.Driver.AtParkAsync(cancellationToken)
            && (!mount.Driver.CanUnpark || !await CatchAsync(mount.Driver.UnparkAsync, cancellationToken).ConfigureAwait(false)))
        {
            External.AppLogger.LogError("Mount {Mount} is parked but cannot be unparked. Aborting.", mount);

            return false;
        }

        // try set the time to our time if supported
        await mount.Driver.SetUTCDateAsync(External.TimeProvider.GetUtcNow().UtcDateTime, cancellationToken);

        for (var i = 0; i < Setup.Telescopes.Length; i++)
        {
            var telescope = Setup.Telescopes[i];
            var camera = telescope.Camera;
            await camera.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);

            // copy over denormalised properties if required
            camera.Driver.Telescope ??= telescope.Name;
            if (camera.Driver.FocalLength is <= 0)
            {
                camera.Driver.FocalLength = telescope.FocalLength;
            }
            camera.Driver.Latitude ??= await mount.Driver.GetSiteLatitudeAsync(cancellationToken);
            camera.Driver.Longitude ??= await mount.Driver.GetSiteLongitudeAsync(cancellationToken);
        }

        if (!await CoolCamerasToSensorTempAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
        {
            External.AppLogger.LogError("Failed to set camera cooler setpoint to current CCD temperature, aborting session.");
            return false;
        }

        if (await MoveTelescopeCoversToStateAsync(CoverStatus.Open, CancellationToken.None))
        {
            External.AppLogger.LogInformation("All covers opened, and calibrator turned off.");
        }
        else
        {
            External.AppLogger.LogError("Openening telescope covers failed, aborting session.");
            return false;
        }

        guider.Driver.GuiderStateChangedEvent += (_, e) => _guiderEvents.Enqueue(e);
        guider.Driver.GuidingErrorEvent +=  (_, e) => _guiderEvents.Enqueue(e);
        await guider.Driver.ConnectEquipmentAsync(cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return true;
    }



}
