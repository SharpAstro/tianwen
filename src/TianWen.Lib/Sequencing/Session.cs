using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using TianWen.Lib.Astrometry.Focus;
using TianWen.Lib.Astrometry.PlateSolve;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Imaging;
using static TianWen.Lib.Astrometry.CoordinateUtils;
using static TianWen.Lib.Stat.StatisticsHelper;

namespace TianWen.Lib.Sequencing;

public record Session(
    Setup Setup,
    in SessionConfiguration Configuration,
    IImageAnalyser Analyser,
    IPlateSolver PlateSolver,
    IExternal External,
    IReadOnlyList<Observation> Observations
)
{
    private readonly ConcurrentQueue<GuiderEventArgs> _guiderEvents = [];
    private int _activeObservation = -1;

    public Observation? CurrentObservation => _activeObservation is int active and >= 0 && active < Observations.Count ? Observations[active] : null;

    private int AdvanceObservation() => Interlocked.Increment(ref _activeObservation);

    public void Run(CancellationToken cancellationToken)
    {
        try
        {
            var active = AdvanceObservation();
            // run initialisation code
            if (active == 0)
            {
                if (!Initialisation(cancellationToken))
                {
                    External.AppLogger.LogError("Initialization failed, aborting session.");
                    return;
                }
            }
            else if (CurrentObservation is null)
            {
                External.AppLogger.LogInformation("Session complete, finished {ObservationCount} observations, finalizing.", _activeObservation);
                return;
            }

            WaitUntilTenMinutesBeforeAmateurAstroTwilightEnds();

            CoolCamerasToSetpoint(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken);

            // TODO wait until 5 min to astro dark, and/or implement IExternal.IsPolarAligned

            if (!InitialRoughFocus(cancellationToken))
            {
                External.AppLogger.LogError("Failed to focus cameras (first time), aborting session.");
                return;
            }

            CalibrateGuider(cancellationToken);

            ObservationLoop(cancellationToken);
        }
        catch (Exception e)
        {
            External.AppLogger.LogError(e, "Exception while in main run loop, unrecoverable, aborting session.");
        }
        finally
        {
            Finalise();
        }
    }

    /// <summary>
    /// Rough focus in this context is defined as: at least 15 stars can be detected by plate-solving when doing a short, high-gain exposure.
    /// Assumes that zenith is visible, which should hopefully be the default for most setups.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>true iff all cameras have at least rough focus.</returns>
    internal bool InitialRoughFocus(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var distMeridian = TimeSpan.FromMinutes(15);

        if (!mount.Driver.EnsureTracking())
        {
            External.AppLogger.LogError("Failed to enable tracking of {Mount}.", mount);

            return false;
        }

        External.AppLogger.LogInformation("Slew mount {Mount} near zenith to verify that we have rough focus.", mount);

        // coordinates not quite accurate at this point (we have not plate-solved yet) but good enough for this purpose.
        mount.Driver.SlewToZenithAsync(distMeridian);
        var slewTime = MountUtcNow;

        if (!mount.Driver.WaitForSlewComplete(cancellationToken))
        {
            External.AppLogger.LogError("Failed to complete slewing of mount {Mount}", mount);

            return false;
        }
        
        if (!GuiderFocusLoop(TimeSpan.FromMinutes(1), cancellationToken))
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
                    var stars = Analyser.FindStars(image, snrMin: 15);

                    if (stars.Count < 15)
                    {
                        expTimesSec[i]++;

                        if (MountUtcNow - slewTime + TimeSpan.FromSeconds(count * 5 + expTimesSec[i]) < distMeridian)
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
            if (MountUtcNow - slewTime > distMeridian)
            {
                mount.Driver.SlewToZenithAsync(distMeridian);
                
                slewTime = MountUtcNow;

                if (!mount.Driver.WaitForSlewComplete(cancellationToken))
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

    private bool GuiderFocusLoop(TimeSpan timeoutAfter, CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var plateSolveTimeout = timeoutAfter > TimeSpan.FromSeconds(5) ? timeoutAfter - TimeSpan.FromSeconds(3) : timeoutAfter;
        var solveTask = guider.Driver.PlateSolveGuiderImageAsync(PlateSolver, mount.Driver.RightAscension, mount.Driver.Declination, plateSolveTimeout, 10, cancellationToken);

        var plateSolveWaitTime = TimeSpan.Zero;
        while (!solveTask.IsCompleted && !cancellationToken.IsCancellationRequested && plateSolveWaitTime < timeoutAfter)
        {
            var spinWait = TimeSpan.FromMilliseconds(100);
            plateSolveWaitTime += spinWait;
            External.Sleep(spinWait);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            External.AppLogger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit imaging loop.", guider.Driver);
            return false;
        }
        
        if (solveTask.IsCompletedSuccessfully && solveTask.Result is var (solvedRa, solvedDec))
        {
            External.AppLogger.LogInformation("Guider \"{GuiderName}\" is in focus and camera image plate solve succeeded with ({SolvedRa}, {SolvedDec})",
                guider.Driver, solvedRa, solvedDec);
            return true;
        }
        else if (solveTask.IsFaulted || solveTask.IsCanceled)
        {
            External.AppLogger.LogWarning(solveTask.Exception, "Failed to plate solve guider \"{GuiderName}\" captured frame", guider.Driver);
        }
        else
        {
            External.AppLogger.LogWarning("Failed to plate solve guider \"{GuiderName}\" without a specific reason (probably not enough stars detected)", guider.Driver);
        }

        return false;
    }

    internal void CalibrateGuider(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;

        // TODO: maybe slew slightly above/below 0 declination to avoid trees, etc.
        // slew half an hour to meridian, plate solve and slew closer
        var dec = 0;
        mount.Driver.SlewHourAngleDecAsync(TimeSpan.FromMinutes(30).TotalHours, dec);

        if (!mount.Driver.WaitForSlewComplete(cancellationToken))
        {
            throw new InvalidOperationException($"Failed to slew mount {mount} to guider calibration position (near meridian, {DegreesToDMS(dec)} declination)");
        }

        // TODO: plate solve and sync and reslew

        var guider = Setup.Guider;

        if (!guider.Driver.StartGuidingLoop(Configuration.GuidingTries, cancellationToken))
        {
            throw new InvalidOperationException($"Failed to start guider loop of guider {guider.Driver}");
        }
    }

    internal void Finalise()
    {
        External.AppLogger.LogInformation("Executing session run finaliser: Stop guiding, stop tracking, disconnect guider, close covers, cool to ambient temp, turn off cooler, park scope.");

        var mount = Setup.Mount;
        var guider = Setup.Guider;

        var maybeCoversClosed = null as bool?;
        var maybeCooledCamerasToAmbient = null as bool?;

        var guiderStopped = Catch(() =>
        {
            guider.Driver.StopCapture(TimeSpan.FromSeconds(15), sleep: External.Sleep);
            return !guider.Driver.IsGuiding();
        });

        var trackingStopped = Catch(() => mount.Driver.CanSetTracking && !(mount.Driver.Tracking = false));

        if (trackingStopped)
        {
            maybeCoversClosed ??= Catch(CloseCovers);
            maybeCooledCamerasToAmbient ??= Catch(TurnOffCameraCooling);
        }

        var guiderDisconnected = Catch(() => !(guider.Driver.Connected = false));

        bool parkInitiated = Catch(() => mount.Driver.CanPark) && Catch(mount.Driver.Park);

        var parkCompleted = parkInitiated && Catch(() =>
        {
            int i = 0;
            while (!mount.Driver.AtPark && i++ < IDeviceDriver.MAX_FAILSAFE)
            {
                External.Sleep(TimeSpan.FromMilliseconds(100));
            }

            return mount.Driver.AtPark;
        });

        if (parkCompleted)
        {
            maybeCoversClosed ??= Catch(CloseCovers);
            maybeCooledCamerasToAmbient ??= Catch(TurnOffCameraCooling);
        }

        var coversClosed = maybeCoversClosed ??= Catch(CloseCovers);
        var cooledCamerasToAmbient = maybeCooledCamerasToAmbient ??= Catch(TurnOffCameraCooling);

        var mountDisconnected = Catch(() => !(mount.Driver.Connected = false));

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

        bool CloseCovers() => MoveTelescopeCoversToState(CoverStatus.Closed, CancellationToken.None);

        bool TurnOffCameraCooling() => CoolCamerasToAmbient(Configuration.WarmupRampInterval);
    }

    /// <summary>
    /// Does one-time (per session) initialisation, e.g. connecting, unparking
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>True if initialisation was successful.</returns>
    internal bool Initialisation(CancellationToken cancellationToken)
    {
        var mount = Setup.Mount;
        var guider = Setup.Guider;

        mount.Driver.Connected = true;
        guider.Driver.Connected = true;

        if (mount.Driver.AtPark && (!mount.Driver.CanUnpark || !Catch(mount.Driver.Unpark)))
        {
            External.AppLogger.LogError("Mount {Mount} is parked but cannot be unparked. Aborting.", mount);

            return false;
        }

        // try set the time to our time if supported
        mount.Driver.UTCDate = External.TimeProvider.GetUtcNow().UtcDateTime;

        for (var i = 0; i < Setup.Telescopes.Count; i++)
        {
            var telescope = Setup.Telescopes[i];
            var camera = telescope.Camera;
            camera.Driver.Connected = true;

            // copy over denormalised properties if required
            camera.Driver.Telescope ??= telescope.Name;
            if (camera.Driver.FocalLength is <= 0)
            {
                camera.Driver.FocalLength = telescope.FocalLength;
            }
            camera.Driver.Latitude ??= mount.Driver.SiteLatitude;
            camera.Driver.Longitude ??= mount.Driver.SiteLongitude;
        }

        if (!CoolCamerasToSensorTemp(TimeSpan.FromSeconds(10), cancellationToken))
        {
            External.AppLogger.LogError("Failed to set camera cooler setpoint to current CCD temperature, aborting session.");
            return false;
        }

        if (MoveTelescopeCoversToState(CoverStatus.Open, CancellationToken.None))
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
        guider.Driver.ConnectEquipment();

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return true;
    }

    internal void ObservationLoop(CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var sessionStartTime = MountUtcNow;
        var sessionEndTime = SessionEndTime(sessionStartTime);

        Observation? observation;
        while ((observation = CurrentObservation) is not null
            && MountUtcNow < sessionEndTime
            && !cancellationToken.IsCancellationRequested
        )
        {
            if (!mount.Driver.EnsureTracking())
            {
                External.AppLogger.LogError("Failed to enable tracking of {Mount}.", mount);
                return;
            }

            External.AppLogger.LogInformation("Stop guiding to start slewing mount to target {Observation}.", observation);
            guider.Driver.StopCapture(TimeSpan.FromSeconds(15));

            double hourAngleAtSlewTime;
            try
            {
                (var postCondition, hourAngleAtSlewTime) = mount.Driver.SlewToTargetAsync(Configuration.MinHeightAboveHorizon, observation.Target);
                if (postCondition is SlewPostCondition.SkipToNext)
                {
                    _ = AdvanceObservation();
                    continue;
                }
                else if (postCondition is SlewPostCondition.Slewing)
                {
                    if (!mount.Driver.WaitForSlewComplete(cancellationToken))
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
                External.AppLogger.LogError(ex, "Error while slewing to {Observation}, advance to next target", observation);
                _ = AdvanceObservation();
                continue;
            }

            var guidingSuccess = guider.Driver.StartGuidingLoop(Configuration.GuidingTries, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation requested, abort setting up guider \"{GuiderName}\" and quit imaging loop.", guider.Driver);
                break;
            }
            else if (!guidingSuccess)
            {
                External.AppLogger.LogError("Skipping target {Observation} as starting guider \"{GuiderName}\" failed after trying twice.", observation, guider.Driver);
                _ = AdvanceObservation();
                continue;
            }

            var imageLoopStart = MountUtcNow;
            if (!ImagingLoop(observation, hourAngleAtSlewTime, cancellationToken))
            {
                External.AppLogger.LogError("Imaging loop for {Observation} did not complete successfully, total runtime: {TotalRuntime:c}", observation, MountUtcNow - imageLoopStart);
            }
        } // end observation loop
    }

    internal bool ImagingLoop(in Observation observation, double hourAngleAtSlewTime, CancellationToken cancellationToken)
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

            // TODO per camera exposure calculation, i.e. via f/ratio
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
        var imageWriteQueue = new Queue<(Image image, Observation observation, DateTimeOffset expStartTime, int frameNumber)>();

        while (!cancellationToken.IsCancellationRequested
            && mount.Driver.Connected
            && Catch(() => mount.Driver.Tracking)
            && guider.Driver.Connected
            && Catch(guider.Driver.IsGuiding)
            && mount.Driver.IsOnSamePierSide(hourAngleAtSlewTime)
        )
        {
            for (var i = 0; i < scopes; i++)
            {
                var telescope = Setup.Telescopes[i];
                var camerDriver = telescope.Camera.Driver;
                if (camerDriver.CameraState is CameraState.Idle)
                {
                    camerDriver.FocusPosition = telescope.Focuser?.Driver is { Connected: true } focuserDriver ? focuserDriver.Position : -1;
                    camerDriver.Filter = telescope.FilterWheel?.Driver?.CurrentFilter ?? Filter.None;

                    var subExposureSec = subExposuresSec[i];
                    var frameExpTime = TimeSpan.FromSeconds(subExposureSec);
                    expStartTimes[i] = camerDriver.StartExposure(frameExpTime);
                    expTicks[i] = (int)(subExposureSec / tickGCD);
                    var frameNo = ++frameNumbers[i];

                    External.AppLogger.LogInformation("Camera #{CameraNumber} {CamerName} starting {ExposureStartTime} exposure of frame #{FrameNo}.",
                        i + 1, camerDriver.Name, frameExpTime, frameNo);
                }
            }

            var elapsed = WriteQueuedImagesToFitsFiles();
            var tickMinusElapsed = tickDuration - elapsed - overslept;
            // clear overslept
            overslept = TimeSpan.Zero;
            if (cancellationToken.IsCancellationRequested)
            {
                External.AppLogger.LogWarning("Cancellation rquested, all images in queue written to disk, abort image acquisition and quit imaging loop");
                return false;
            }
            else if (tickMinusElapsed > TimeSpan.Zero)
            {
                External.Sleep(tickMinusElapsed);
            }

            var imageFetchSuccess = new bool[scopes];
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

                            imageWriteQueue.Enqueue((image, observation, expStartTimes[i], frameNo));
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

            var allimageFetchSuccess = imageFetchSuccess.All(x => x);
            if (!mount.Driver.IsOnSamePierSide(hourAngleAtSlewTime))
            {
                // write all images as the loop is ending here
                _ = WriteQueuedImagesToFitsFiles();

                // TODO stop exposures (if we can, and if there are any)

                if (observation.AcrossMeridian)
                {
                    // TODO, stop guiding flip, resync, verify and restart guiding
                    throw new InvalidOperationException("Observing across meridian is not yet supported");
                }
                else
                {
                    // finished this target
                    return true;
                }
            }
            else if (allimageFetchSuccess)
            {
                var shouldDither = (++ditherRound % Configuration.DitherEveryNthFrame) == 0;
                if (shouldDither)
                {
                    if (guider.Driver.DitherWait(Configuration.DitherPixel, Configuration.SettlePixel, Configuration.SettleTime, WriteQueuedImagesToFitsFiles, cancellationToken))
                    {
                        External.AppLogger.LogInformation("Dithering using \"{GuiderName}\" succeeded.", guider.Driver);
                    }
                    else
                    {
                        External.AppLogger.LogError("Dithering using \"{GuiderName}\" failed, aborting.", guider.Driver);
                        return false;
                    }
                }
                else
                {
                    External.AppLogger.LogInformation("Skipping dithering ({DitheringRound}/{DitherEveryNthFrame} frame)",
                        ditherRound % Configuration.DitherEveryNthFrame, Configuration.DitherEveryNthFrame);
                }
            }
        } // end imaging loop

        return !cancellationToken.IsCancellationRequested && !imageWriteQueue.TryPeek(out _);

        TimeSpan WriteQueuedImagesToFitsFiles()
        {
            var writeQueueStart = MountUtcNow;
            while (imageWriteQueue.TryDequeue(out var imageWrite))
            {
                try
                {
                    WriteImageToFitsFile(imageWrite.image, imageWrite.observation, imageWrite.expStartTime, imageWrite.frameNumber);
                }
                catch (Exception ex)
                {
                    External.AppLogger.LogError(ex, "Exception while saving frame #{FrameNumber} taken at {ExposureStartTime:o} by {Instrument}",
                        imageWrite.frameNumber, imageWrite.expStartTime, imageWrite.image.ImageMeta.Instrument);
                }
            }
            
            return MountUtcNow - writeQueueStart;
        }
    }

    /// <summary>
    /// Closes or opens telescope covers (if any). Also turns of a present calibrator when opening cover.
    /// </summary>
    /// <param name="finalCoverState">One of <see cref="CoverStatus.Open"/> or <see cref="CoverStatus.Closed"/></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    internal bool MoveTelescopeCoversToState(CoverStatus finalCoverState, CancellationToken cancellationToken)
    {
        var scopes = Setup.Telescopes.Count;

        var finalCoverStateReached = new bool[scopes];
        var coversToWait = new List<int>();
        var shouldOpen = finalCoverState is CoverStatus.Open;

        for (var i = 0; i < scopes; i++)
        {
            if (Setup.Telescopes[i].Cover is { } cover)
            {
                cover.Driver.Connected = true;

                bool calibratorActionCompleted;
                if (cover.Driver.CoverState is CoverStatus.NotPresent)
                {
                    calibratorActionCompleted = true;
                    finalCoverStateReached[i] = true;
                }
                else if (finalCoverState is CoverStatus.Open)
                {
                    calibratorActionCompleted = cover.Driver.TurnOffCalibratorAndWait(cancellationToken);
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
                    Func<bool> action = shouldOpen ? cover.Driver.Open : cover.Driver.Close;

                    if (action())
                    {
                        coversToWait.Add(i);
                    }
                    else
                    {
                        External.AppLogger.LogError("Failed to {FinalCoverState} cover of telescope {TelescopeNumber}.", shouldOpen ? "open" : "close", i + 1);
                    }
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
    internal bool CoolCamerasToSensorTemp(TimeSpan rampTime, CancellationToken cancellationToken)
        => CoolCamerasToSetpoint(new SetpointTemp(sbyte.MinValue, SetpointTempKind.CCD), rampTime, 0.1, SetupointDirection.Up, cancellationToken);


    /// <summary>
    /// Attention: Cannot be cancelled (as it would possibly destroy the cameras)
    /// </summary>
    /// <param name="rampTime">Interval between temperature checks</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal bool CoolCamerasToAmbient(TimeSpan rampTime)
        => CoolCamerasToSetpoint(new SetpointTemp(sbyte.MinValue, SetpointTempKind.Ambient), rampTime, 0.1, SetupointDirection.Up, CancellationToken.None);

    /// <summary>
    /// Assumes that power is on (c.f. <see cref="CoolCamerasToSensorTemp"/>).
    /// </summary>
    /// <param name="desiredSetpointTemp">Desired degrees Celcius setpoint temperature,
    /// if <paramref name="desiredSetpointTemp"/>'s <see cref="SetpointTemp.Kind"/> is <see cref="SetpointTempKind.CCD" /> then sensor temperature is chosen,
    /// if its <see cref="SetpointTempKind.Normal" /> then the temp value is chosen
    /// or else ambient temperature is chosen (if available)</param>
    /// <param name="rampInterval">interval to wait until further adjusting setpoint.</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal bool CoolCamerasToSetpoint(
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
                External.AppLogger.LogWarning("Cancellation requested, quiting cooldown loop");
                break;
            }
            else
            {
                External.Sleep(rampInterval);
            }
        } while (coolingStates.Any(state => state.IsRamping) && accSleep < rampInterval * 100 && !cancellationToken.IsCancellationRequested);

        return coolingStates.All(state => !(state.IsCoolable ?? false) || (state.TargetSetpointReached ?? false));
    }

    internal void WriteImageToFitsFile(Image image, in Observation observation, DateTimeOffset subExpStartTime, int frameNumber)
    {
        var targetName = observation.Target.Name;
        var dateFolderUtc = subExpStartTime.UtcDateTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo);

        // TODO: make configurable, add frame type
        var frameFolder = External.CreateSubDirectoryInOutputFolder(targetName, dateFolderUtc, image.ImageMeta.Filter.Name).FullName;
        var fitsFileName = External.GetSafeFileName($"frame_{subExpStartTime:o}_{frameNumber}.fits");
        var fitsFllFilePath = Path.Combine(frameFolder, fitsFileName);

        External.AppLogger.LogInformation("Writing FITS file {FitsFilePath}", fitsFllFilePath);
        External.WriteFitsFile(image, fitsFllFilePath);
    }

    internal bool Catch(Action action) => External.Catch(action);

    internal T Catch<T>(Func<T> func, T @default = default) where T : struct => External.Catch(func, @default);

    internal DateTime MountUtcNow
    {
        get
        {
            if (Setup.Mount.Driver.TryGetUTCDate(out var dateTime))
            {
                return dateTime;
            }

            return External.TimeProvider.GetUtcNow().UtcDateTime;
        }
    }

    internal void WaitUntilTenMinutesBeforeAmateurAstroTwilightEnds()
    {
        if (!Setup.Mount.Driver.TryGetTransform(out var transform))
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
                External.Sleep(diff);
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

    internal DateTime SessionEndTime(DateTime startTime)
    {
        if (!Setup.Mount.Driver.TryGetTransform(out var transform))
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
}