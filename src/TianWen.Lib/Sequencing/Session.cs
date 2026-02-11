using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Sequencing;

internal record Session(
    Setup Setup,
    in SessionConfiguration Configuration,
    IPlateSolver PlateSolver,
    IExternal External,
    IReadOnlyList<Observation> PlannedObservations
) : ISession
{
    const int UNINITIALIZED_OBSERVATION_INDEX = -1;

    private readonly ConcurrentQueue<GuiderEventArgs> _guiderEvents = [];
    private int _activeObservation = UNINITIALIZED_OBSERVATION_INDEX;

    public Observation? ActiveObservation => _activeObservation is int active and >= 0 && active < PlannedObservations.Count ? PlannedObservations[active] : null;

    private int AdvanceObservation() => Interlocked.Increment(ref _activeObservation);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var active = AdvanceObservation();
            // run initialisation code
            if (active == 0)
            {
                if (!await InitialisationAsync(cancellationToken))
                {
                    External.AppLogger.LogError("Initialization failed, aborting session.");
                    return;
                }
            }
            else if (ActiveObservation is null)
            {
                External.AppLogger.LogInformation("Session complete, finished {ObservationCount} observations, finalizing.", _activeObservation);
                return;
            }

            await WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(cancellationToken).ConfigureAwait(false);

            await CoolCamerasToSetpointAsync(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken).ConfigureAwait(false);

            // TODO wait until 5 min to astro dark, and/or implement IExternal.IsPolarAligned

            if (!await InitialRoughFocusAsync(cancellationToken))
            {
                External.AppLogger.LogError("Failed to focus cameras (first time), aborting session.");
                return;
            }

            await CalibrateGuiderAsync(cancellationToken).ConfigureAwait(false);

            await ObservationLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            External.AppLogger.LogError(e, "Exception while in main run loop, unrecoverable, aborting session.");
        }
        finally
        {
            await Finalise(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rough focus in this context is defined as: at least 15 stars can be detected by plate-solving when doing a short, high-gain exposure.
    /// Assumes that zenith is visible, which should hopefully be the default for most setups.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>true iff all cameras have at least rough focus.</returns>
    internal async ValueTask<bool> InitialRoughFocusAsync(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var distMeridian = TimeSpan.FromMinutes(15);

        if (!await mount.Driver.EnsureTrackingAsync(cancellationToken: cancellationToken))
        {
            External.AppLogger.LogError("Failed to enable tracking of {Mount}.", mount);

            return false;
        }

        External.AppLogger.LogInformation("Slew mount {Mount} near zenith to verify that we have rough focus.", mount);

        // coordinates not quite accurate at this point (we have not plate-solved yet) but good enough for this purpose.
        await mount.Driver.BeginSlewToZenithAsync(distMeridian, cancellationToken).ConfigureAwait(false);
        var slewTime = await GetMountUtcNowAsync(cancellationToken);

        if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
        {
            External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

            return false;
        }
        
        if (!await GuiderFocusLoopAsync(TimeSpan.FromMinutes(1), cancellationToken))
        {
            return false;
        }

        var count = Setup.Telescopes.Count;
        var origGain = new short[count];
        for (var i = 0; i < count; i++)
        {
            var camDriver = Setup.Telescopes[i].Camera.Driver;

            if (camDriver.UsesGainValue)
            {
                origGain[i] = camDriver.Gain;

                // set high gain
                camDriver.Gain = (short)MathF.Truncate((camDriver.GainMin + camDriver.GainMin) * 0.75f);
            }
            else
            {
                origGain[i] = short.MinValue;
            }

            camDriver.StartExposure(TimeSpan.FromSeconds(1));
        }

        var expTimesSec = new int[count];
        var hasRoughFocus = new bool[count];
        Array.Fill(expTimesSec, 1);

        while (!cancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < count; i++)
            {
                var camDriver = Setup.Telescopes[i].Camera.Driver;

                if (camDriver.ImageReady is true && camDriver.Image is { Width: > 0, Height: > 0 } image)
                {
                    var stars = await image.FindStarsAsync(0, snrMin: 15, cancellationToken: cancellationToken);

                    if (stars.Count < 15)
                    {
                        expTimesSec[i]++;

                        if (await GetMountUtcNowAsync(cancellationToken) - slewTime + TimeSpan.FromSeconds(count * 5 + expTimesSec[i]) < distMeridian)
                        {
                            camDriver.StartExposure(TimeSpan.FromSeconds(expTimesSec[i]));
                        }
                    }
                    else
                    {
                        if (camDriver.UsesGainValue && origGain[i] is >= 0)
                        {
                            camDriver.Gain = origGain[i];
                        }

                        hasRoughFocus[i] = true;
                    }
                }
            }

            // slew back to start position
            if (await GetMountUtcNowAsync(cancellationToken) - slewTime > distMeridian)
            {
                await mount.Driver.BeginSlewToZenithAsync(distMeridian, cancellationToken).ConfigureAwait(false);
                
                slewTime = await GetMountUtcNowAsync(cancellationToken);

                if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
                {
                    External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

                    return false;
                }
            }

            if (hasRoughFocus.All(v => v))
            {
                return true;
            }

            External.Sleep(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    private async ValueTask<bool> GuiderFocusLoopAsync(TimeSpan timeoutAfter, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var plateSolveTimeout = timeoutAfter > TimeSpan.FromSeconds(5) ? timeoutAfter - TimeSpan.FromSeconds(3) : timeoutAfter;

        var wcs = await guider.Driver.PlateSolveGuiderImageAsync(PlateSolver, 
            await mount.Driver.GetRightAscensionAsync(cancellationToken),
            await mount.Driver.GetDeclinationAsync(cancellationToken),
            plateSolveTimeout,
            10d,
            cancellationToken
        );

        if (wcs is var (solvedRa, solvedDec))
        {
            External.AppLogger.LogInformation("Guider \"{GuiderName}\" is in focus and camera image plate solve succeeded with ({SolvedRa}, {SolvedDec})",
                guider.Driver, solvedRa, solvedDec);
            return true;
        }
        else
        {
            External.AppLogger.LogWarning("Failed to plate solve guider \"{GuiderName}\" without a specific reason (probably not enough stars detected)",
                guider.Driver);
        }

        return false;
    }

    internal async ValueTask CalibrateGuiderAsync(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;

        // TODO: maybe slew slightly above/below 0 declination to avoid trees, etc.
        // slew half an hour to meridian, plate solve and slew closer
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

        for (var i = 0; i < Setup.Telescopes.Count; i++)
        {
            var camDriver = Setup.Telescopes[i].Camera.Driver;
            if (Catch(() => camDriver.CanGetCoolerOn))
            {
                shutdownReport[$"Camera #{(i + 1)} Cooler Off"] = Catch(() => !camDriver.CoolerOn || !(camDriver.CoolerOn = false));
            }
            if (Catch(() => camDriver.CanGetCoolerPower))
            {
                shutdownReport[$"Camera #{(i + 1)} Cooler Power <= 0.1"] = Catch(() => camDriver.CoolerPower is <= 0.1);
            }
            if (Catch(() => camDriver.CanGetHeatsinkTemperature))
            {
                shutdownReport[$"Camera #{(i + 1)} Temp near ambient"] = Catch(() => Math.Abs(camDriver.CCDTemperature - camDriver.HeatSinkTemperature) < 1d);
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

        for (var i = 0; i < Setup.Telescopes.Count; i++)
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

    internal async ValueTask ObservationLoopAsync(CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var sessionStartTime = await GetMountUtcNowAsync(cancellationToken);
        var sessionEndTime = await SessionEndTimeAsync(sessionStartTime, cancellationToken);

        Observation? observation;
        while ((observation = ActiveObservation) is not null
            && await GetMountUtcNowAsync(cancellationToken) < sessionEndTime
            && !cancellationToken.IsCancellationRequested
        )
        {
            if (!await mount.Driver.EnsureTrackingAsync(cancellationToken: cancellationToken))
            {
                External.AppLogger.LogError("Failed to enable tracking of {Mount}.", mount);
                return;
            }

            External.AppLogger.LogInformation("Stop guiding to start slewing mount to target {Observation}.", observation);
            await guider.Driver.StopCaptureAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);

            double hourAngleAtSlewTime;
            try
            {
                (var postCondition, hourAngleAtSlewTime) = await mount.Driver.BeginSlewToTargetAsync(observation.Target, Configuration.MinHeightAboveHorizon, cancellationToken).ConfigureAwait(false);
                if (postCondition is SlewPostCondition.SlewNotPossible)
                {
                    _ = AdvanceObservation();
                    continue;
                }
                else if (postCondition is SlewPostCondition.TargetBelowHorizonLimit)
                {
                    // TODO: wait until target rises again instead of skipping
                    continue;
                }
                else if (postCondition is SlewPostCondition.Slewing)
                {
                    if (!await mount.Driver.WaitForSlewCompleteAsync(cancellationToken).ConfigureAwait(false))
                    {
                        External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

                        throw new InvalidOperationException($"Failed to complete slewing of mount {mount} while slewing to {observation.Target}");
                    }

                    // TODO: Plate solve and re-slew
                }
                else
                {
                    throw new InvalidOperationException($"Unknown post condition {postCondition} after slewing to target {observation.Target}");
                }
            }
            catch (Exception ex)
            {
                External.AppLogger.LogError(ex, "Error while slewing to {Observation}, retrying", observation);

                // todo: if next observation is not yet risen, we need to wait and retry
                continue;
            }

            var guidingSuccess = await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit observation loop.", guider.Driver);
                break;
            }
            else if (!guidingSuccess)
            {
                External.AppLogger.LogError("Skipping target {Observation} as starting guider \"{GuiderName}\" failed after trying {GuiderTries} times.", observation, guider.Driver, Configuration.GuidingTries);
                
                // todo: if next observation is not yet risen, we need to wait and retry
                _ = AdvanceObservation();
                continue;
            }

            var imageLoopStart = await GetMountUtcNowAsync(cancellationToken);
            var imageLoopResult = await ImagingLoopAsync(observation, hourAngleAtSlewTime, cancellationToken).ConfigureAwait(false);
            if (imageLoopResult is ImageLoopNextAction.AdvanceToNextObservation)
            {
                _ = AdvanceObservation();
                continue;
            }
            else if (imageLoopResult is ImageLoopNextAction.RepeatCurrentObservation)
            {
                // todo maybe wait a bit for better weather/tree out of the way, etc.
                continue;
            }
            else
            {
                External.AppLogger.LogError("Imaging loop for {Observation} did not complete successfully, total runtime: {TotalRuntime:c}", observation, await GetMountUtcNowAsync(cancellationToken) - imageLoopStart);
                break;
            }
        } // end observation loop
    }

    /// <summary>
    /// Imaging loop for one observation, handles exposing frames + dithering, handles meridian flip.
    /// </summary>
    /// <param name="observation">Observation to image.</param>
    /// <param name="hourAngleAtSlewTime">provide hour angle current as of start of session, used to calculate meridian flip.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>loop result</returns>
    /// <exception cref="InvalidOperationException"></exception>
    internal async ValueTask<ImageLoopNextAction> ImagingLoopAsync(Observation observation, double hourAngleAtSlewTime, CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var scopes = Setup.Telescopes.Count;
        var frameNumbers = new int[scopes];
        var subExposuresSec = new int[scopes];

        for (var i = 0; i < scopes; i++)
        {
            var camera = Setup.Telescopes[i].Camera;

            camera.Driver.Target = observation.Target;

            // TODO per camera exposure calculation, e.g. via f/ratio
            var subExposure = observation.SubExposure;
            subExposuresSec[i] = (int)Math.Ceiling(subExposure.TotalSeconds);
        }

        var maxSubExposureSec = subExposuresSec.Max();
        var tickGCD = GCD(subExposuresSec);
        var tickLCM = LCM(tickGCD, subExposuresSec);
        var tickDuration = TimeSpan.FromSeconds(tickGCD);
        var ticksPerMaxSubExposure = maxSubExposureSec / tickGCD;
        var expStartTimes = new DateTimeOffset[scopes];
        var expTicks = new int[scopes];
        var ditherRound = 0;

        var overslept = TimeSpan.Zero;
        var imageWriteQueue = new Queue<QueuedImageWrite>();
        ImageLoopNextAction? next = null;

        while (!cancellationToken.IsCancellationRequested
            && mount.Driver.Connected
            && await CatchAsync(mount.Driver.IsTrackingAsync, cancellationToken)
        )
        {
            if (!await CatchAsync(guider.Driver.IsGuidingAsync, cancellationToken).ConfigureAwait(false))
            {
                var guiderRestartedSuccess =
                    await CatchAsync(guider.Driver.ConnectAsync, cancellationToken) &&
                    await guider.Driver.StartGuidingLoopAsync(Configuration.GuidingTries, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    External.AppLogger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit imaging loop for observation {Observation}.", guider.Driver, observation);
                    next = ImageLoopNextAction.BreakObservationLoop;
                    break;
                }
                else if (!guiderRestartedSuccess)
                {
                    External.AppLogger.LogError("Reschedule target {Observation} as starting guider \"{GuiderName}\" failed after trying {GuiderTries} times.", observation, guider.Driver, Configuration.GuidingTries);
                    next = ImageLoopNextAction.RepeatCurrentObservation;
                    break;
                }
            }

            for (var i = 0; i < scopes; i++)
            {
                var telescope = Setup.Telescopes[i];
                var camerDriver = telescope.Camera.Driver;
                if (camerDriver.CameraState is CameraState.Idle)
                {
                    // set denormalized parameters so that the image driver can write proper headers in the image file
                    camerDriver.FocusPosition = Catch(() => telescope.Focuser?.Driver is { Connected: true } focuserDriver ? focuserDriver.Position : -1, -1);
                    camerDriver.Filter = Catch(() => telescope.FilterWheel?.Driver is { Connected: true } filterWheelDriver ? filterWheelDriver.CurrentFilter : Filter.Unknown, Filter.Unknown);

                    var subExposureSec = subExposuresSec[i];
                    var frameExpTime = TimeSpan.FromSeconds(subExposureSec);
                    expStartTimes[i] = camerDriver.StartExposure(frameExpTime);
                    expTicks[i] = (int)(subExposureSec / tickGCD);
                    var frameNo = ++frameNumbers[i];

                    External.AppLogger.LogInformation("Camera #{CameraNumber} {CamerName} starting {ExposureStartTime} exposure of frame #{FrameNo}.",
                        i + 1, camerDriver.Name, frameExpTime, frameNo);
                }
            }

            var elapsed = await WriteQueuedImagesToFitsFilesAsync().ConfigureAwait(false);
            var tickMinusElapsed = tickDuration - elapsed - overslept;
            // clear overslept
            overslept = TimeSpan.Zero;
            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, all images in queue written to disk, abort image acquisition and quit imaging loop");
                next = ImageLoopNextAction.BreakObservationLoop;
                break;
            }
            else if (tickMinusElapsed > TimeSpan.Zero)
            {
                External.Sleep(tickMinusElapsed);
            }

            var imageFetchSuccess = new BitVector32(scopes);
            for (var i = 0; i < scopes && !cancellationToken.IsCancellationRequested; i++)
            {
                var tick = --expTicks[i];

                var camDriver = Setup.Telescopes[i].Camera.Driver;
                imageFetchSuccess[i] = false;
                if (tick <= 0)
                {
                    var frameExpTime = TimeSpan.FromSeconds(subExposuresSec[i]);
                    var frameNo = frameNumbers[i];
                    do // wait for image loop
                    {
                        if (camDriver.ImageReady is true && camDriver.Image is { Width: > 0, Height: > 0 } image)
                        {
                            imageFetchSuccess[i] = true;
                            External.AppLogger.LogInformation("Camera #{CameraNumber} {CameraName} finished {ExposureStartTime} exposure of frame #{FrameNo}",
                                i + 1, camDriver.Name, frameExpTime, frameNo);

                            imageWriteQueue.Enqueue(new QueuedImageWrite(image, observation, expStartTimes[i], frameNo));
                            break;
                        }
                        else
                        {
                            var spinDuration = TimeSpan.FromMilliseconds(100);
                            overslept += spinDuration;

                            External.Sleep(spinDuration);
                        }
                    }
                    while (overslept < (tickDuration / 5)
                        && camDriver.CameraState is not CameraState.Error and not CameraState.NotConnected
                        && !cancellationToken.IsCancellationRequested
                    );

                    if (!imageFetchSuccess[i])
                    {
                        External.AppLogger.LogError("Failed fetching camera #{CameraNumber)} {CameraName} {ExposureStartTime} exposure of frame #{FrameNo}, camera state: {CameraState}",
                            i + 1, camDriver.Name, frameExpTime, frameNo, camDriver.CameraState);
                    }
                }
            }

            var fetchImagesSuccessAll = imageFetchSuccess.AllSet(scopes);
            if (!await mount.Driver.IsOnSamePierSideAsync(hourAngleAtSlewTime, cancellationToken))
            {
                // write all images as the loop is ending here
                _ = await WriteQueuedImagesToFitsFilesAsync();

                // TODO stop exposures (if we can, and if there are any)

                if (observation.AcrossMeridian)
                {
                    // TODO, stop guiding flip, resync, verify and restart guiding
                    throw new NotSupportedException("Observing across meridian is not yet supported");
                }
                else
                {
                    // finished this target
                    break;
                }
            }
            else if (fetchImagesSuccessAll)
            {
                var shouldDither = (++ditherRound % Configuration.DitherEveryNthFrame) == 0;
                if (shouldDither)
                {
                    if (await guider.Driver.DitherWaitAsync(Configuration.DitherPixel, Configuration.SettlePixel, Configuration.SettleTime, WriteQueuedImagesToFitsFilesAsync, cancellationToken).ConfigureAwait(false))
                    {
                        External.AppLogger.LogInformation("Dithering using \"{GuiderName}\" succeeded.", guider.Driver);
                    }
                    else
                    {
                        External.AppLogger.LogWarning("Dithering using \"{GuiderName}\" failed.", guider.Driver);
                    }
                }
                else
                {
                    External.AppLogger.LogDebug("Skipping dithering ({DitheringRound}/{DitherEveryNthFrame} frame)",
                        ditherRound % Configuration.DitherEveryNthFrame, Configuration.DitherEveryNthFrame);
                }
            }
        } // end imaging loop

        if (imageWriteQueue.TryPeek(out _))
        {
            // write all images as the loop is ending here
            _ = await WriteQueuedImagesToFitsFilesAsync();
        }

        return next ?? ImageLoopNextAction.AdvanceToNextObservation;

        async ValueTask<TimeSpan> WriteQueuedImagesToFitsFilesAsync()
        {
            var writeQueueStart = await GetMountUtcNowAsync(cancellationToken);
            while (imageWriteQueue.TryDequeue(out var imageWrite))
            {
                try
                {
                    await WriteImageToFitsFileAsync(imageWrite);
                }
                catch (Exception ex)
                {
                    External.AppLogger.LogError(ex, "Exception while saving frame #{FrameNumber} taken at {ExposureStartTime:o} by {Instrument}",
                        imageWrite.FrameNumber, imageWrite.ExpStartTime, imageWrite.Image.ImageMeta.Instrument);
                }
            }
            
            return await GetMountUtcNowAsync(cancellationToken) - writeQueueStart;
        }
    }

    /// <summary>
    /// Closes or opens telescope covers (if any). Also turns of a present calibrator when opening cover.
    /// </summary>
    /// <param name="finalCoverState">One of <see cref="CoverStatus.Open"/> or <see cref="CoverStatus.Closed"/></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    internal async ValueTask<bool> MoveTelescopeCoversToStateAsync(CoverStatus finalCoverState, CancellationToken cancellationToken)
    {
        var scopes = Setup.Telescopes.Count;

        var finalCoverStateReached = new bool[scopes];
        var coversToWait = new List<int>();
        var shouldOpen = finalCoverState is CoverStatus.Open;

        for (var i = 0; i < scopes; i++)
        {
            if (Setup.Telescopes[i].Cover is { } cover)
            {
                await cover.Driver.ConnectAsync(cancellationToken).ConfigureAwait(false);

                bool calibratorActionCompleted;
                if (cover.Driver.CoverState is CoverStatus.NotPresent)
                {
                    calibratorActionCompleted = true;
                    finalCoverStateReached[i] = true;
                }
                else if (finalCoverState is CoverStatus.Open)
                {
                    calibratorActionCompleted = await cover.Driver.TurnOffCalibratorAndWaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (finalCoverState is CoverStatus.Closed)
                {
                    calibratorActionCompleted = true;
                }
                else
                {
                    throw new ArgumentException($"Invalid final cover state {finalCoverState}, can only be open or closed", nameof(finalCoverState));
                }

                if (calibratorActionCompleted && !finalCoverStateReached[i])
                {
                    if (shouldOpen)
                    {
                        await cover.Driver.BeginOpen(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await cover.Driver.BeginClose(cancellationToken).ConfigureAwait(false);
                    }

                    coversToWait.Add(i);
                }
                else if (!calibratorActionCompleted)
                {
                    External.AppLogger.LogError("Failed to turn off calibrator of telescope {TelescopeNumber}, current state {CalibratorState}", i+1, cover.Driver.CalibratorState);
                }
            }
            else
            {
                finalCoverStateReached[i] = true;
            }
        }

        foreach (var i in coversToWait)
        {
            if (Setup.Telescopes[i].Cover is { } cover)
            {
                int failSafe = 0;
                CoverStatus cs;
                while ((finalCoverStateReached[i] = (cs = cover.Driver.CoverState) == finalCoverState) is false
                    && cs is CoverStatus.Moving or CoverStatus.Unknown
                    && !cancellationToken.IsCancellationRequested
                    && ++failSafe < IDeviceDriver.MAX_FAILSAFE
                )
                {
                    External.AppLogger.LogInformation("Cover {Cover} of telescope {TelescopeNumber} is still {CurrentState} while reaching {FinalCoverState}, waiting.",
                        cover, i + 1, cs, finalCoverState);
                    External.Sleep(TimeSpan.FromSeconds(3));
                }

                var finalCoverStateAfterMoving = cover.Driver.CoverState;
                finalCoverStateReached[i] |= finalCoverStateAfterMoving == finalCoverState;

                if (!finalCoverStateReached[i])
                {
                    External.AppLogger.LogError("Failed to {CoverAction} cover of telescope {TelescopeNumber} after moving, current state {CurrentCoverState}",
                        shouldOpen ? "open" : "close",  i + 1, finalCoverStateAfterMoving);
                }
            }
        }

        return finalCoverStateReached.All(x => x);
    }

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
                coolingStates[i] = camera.Driver.CoolToSetpoint(desiredSetpointTemp, thresPower, direction, coolingStates[i]);
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

    internal ValueTask WriteImageToFitsFileAsync(QueuedImageWrite imageWrite)
    {
        var targetName = imageWrite.Observation.Target.Name;
        var dateFolderUtc = imageWrite.ExpStartTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);

        // TODO: make configurable, add frame type
        var meta = imageWrite.Image.ImageMeta;
        var frameFolder = External.CreateSubDirectoryInOutputFolder(
            targetName,
            dateFolderUtc,
            meta.Filter.Name,
            meta.FrameType.ToString()
        ).FullName;
        var fitsFileName = External.GetSafeFileName($"frame_{imageWrite.ExpStartTime:o}_{imageWrite.FrameNumber:000000}.fits");
        var fitsFilePath = Path.Combine(frameFolder, fitsFileName);

        External.AppLogger.LogInformation("Writing FITS file {FitsFilePath}", fitsFilePath);
        return External.WriteFitsFileAsync(imageWrite.Image, fitsFilePath);
    }

    internal bool Catch(Action action) => External.Catch(action);

    internal T Catch<T>(Func<T> func, T @default = default) where T : struct => External.Catch(func, @default);
    internal ValueTask<bool> CatchAsync(Func<CancellationToken, ValueTask> asyncFunc, CancellationToken cancellationToken)
        => External.CatchAsync(asyncFunc, cancellationToken);
    internal Task<bool> CatchAsync(Func<CancellationToken, Task> asyncFunc, CancellationToken cancellationToken)
        => External.CatchAsync(asyncFunc, cancellationToken);


    internal ValueTask<T> CatchAsync<T>(Func<CancellationToken, ValueTask<T>> asyncFunc, CancellationToken cancellationToken, T @default = default) where T : struct
        => External.CatchAsync(asyncFunc, cancellationToken, @default);

    internal async ValueTask<DateTime> GetMountUtcNowAsync(CancellationToken cancellationToken)
        => await Setup.Mount.Driver.TryGetUTCDateFromMountAsync(cancellationToken) ?? External.TimeProvider.GetUtcNow().UtcDateTime;

    internal async ValueTask WaitUntilTenMinutesBeforeAmateurAstroTwilightEndsAsync(CancellationToken cancellationToken)
    {
        if (await Setup.Mount.Driver.TryGetTransformAsync(cancellationToken) is not { } transform)
        {
            throw new InvalidOperationException("Failed to retrieve time transformation from mount");
        }

        var (_, _, set) = transform.EventTimes(Astrometry.SOFA.EventType.AmateurAstronomicalTwilight);
        if (set is { Count: 1 })
        {
            var now = External.TimeProvider.GetUtcNow().UtcDateTime;
            var localNow = new DateTimeOffset(now, transform.SiteTimeZone);
            var utcDayStart = now - now.TimeOfDay;
            var localAstroTwilightSet = new DateTimeOffset(utcDayStart, transform.SiteTimeZone) + set[0];
            var local10MinBeforeAstroTwilightSet = localAstroTwilightSet - TimeSpan.FromMinutes(10);
            var diff = local10MinBeforeAstroTwilightSet - now;

            if (diff > TimeSpan.Zero)
            {
                External.AppLogger.LogInformation("Current time {CurrentTimeLocal}, twilight ends {AmateurTwilightEndsLocal}, which is in {Diff}",
                    localNow, localAstroTwilightSet, diff);
                await External.SleepAsync(diff, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                External.AppLogger.LogWarning("Current time {CurrentTimeLocal}, twilight ends {AmateurTwilightEndsLocal}, ended {Diff} ago",
                    localNow, localAstroTwilightSet, -diff);
            }
        }
        else
        {
            throw new InvalidOperationException($"Failed to retrieve astro event time for {transform.DateTime}");
        }
    }

    internal async ValueTask<DateTime> SessionEndTimeAsync(DateTime startTime, CancellationToken cancellationToken)
    {
        if (await Setup.Mount.Driver.TryGetTransformAsync(cancellationToken) is not { } transform)
        {
            throw new InvalidOperationException("Failed to retrieve time transformation from mount");
        }

        // advance one day
        var nowPlusOneDay = transform.DateTime = startTime.AddDays(1);
        var (_, rise, _) = transform.EventTimes(Astrometry.SOFA.EventType.AstronomicalTwilight);

        if (rise is { Count: 1 })
        {
            var tomorrowStartOfDay = nowPlusOneDay - nowPlusOneDay.TimeOfDay;
            return (new DateTimeOffset(tomorrowStartOfDay, transform.SiteTimeZone) + rise[0]).UtcDateTime;
        }
        else
        {
            throw new InvalidOperationException($"Failed to retrieve astro event time for {transform.DateTime}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Setup.DisposeAsync();

        GC.SuppressFinalize(this);
    }
}