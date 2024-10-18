using Astap.Lib.Astrometry.Focus;
using Astap.Lib.Astrometry.PlateSolve;
using Astap.Lib.Devices;
using Astap.Lib.Devices.Guider;
using Astap.Lib.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using static Astap.Lib.Stat.StatisticsHelper;

namespace Astap.Lib.Sequencing;

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
                    return;
                }
            }
            else if (CurrentObservation is null)
            {
                return;
            }

            // TODO wait until 25 min before astro dark to start cooling down without loosing time
            CoolCamerasToSetpoint( Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, SetupointDirection.Down, cancellationToken);

            // TODO wait until 5 min to astro dark, and/or implement IExternal.IsPolarAligned

            if (!InitialRoughFocus(cancellationToken))
            {
                External.LogError("Failed to focus cameras (first time), aborting session.");
                return;
            }
            // TODO: Slew near meridian (opposite of pole), CalibrateGuider();

            ObservationLoop(cancellationToken);
        }
        catch (Exception e)
        {
            External.LogException(e, "in main run loop, unrecoverable, aborting session.");
        }
        finally
        {
            Finalise(cancellationToken);
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

        mount.EnsureTracking();

        External.LogInfo($"Slew mount {mount.Device.DisplayName} near zenith to verify that we have rough focus.");

        // coordinates not quite accurate but good enough for this purpose.
        if (!mount.Driver.SlewToZenith(distMeridian, cancellationToken))
        {
            return false;
        }

        var slewTime = MountUtcNow;
        
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
                if (!mount.Driver.SlewToZenith(distMeridian, cancellationToken))
                {
                    return false;
                }
                slewTime = MountUtcNow;
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
        var solveTask = guider.Driver.PlateSolveGuiderImageAsync(mount.Driver.RightAscension, mount.Driver.Declination, plateSolveTimeout, PlateSolver, External, 10, cancellationToken);

        var plateSolveWaitTime = TimeSpan.Zero;
        while (!solveTask.IsCompleted && !cancellationToken.IsCancellationRequested && plateSolveWaitTime < timeoutAfter)
        {
            var spinWait = TimeSpan.FromMilliseconds(100);
            plateSolveWaitTime += spinWait;
            External.Sleep(spinWait);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            External.LogWarning($"Cancellation requested, abort setting up guider \"{guider.Driver}\" and quit imaging loop.");
            return false;
        }
        
        if (solveTask.IsCompletedSuccessfully && solveTask.Result is var (solvedRa, solvedDec))
        {
            External.LogInfo($"Guider \"{guider.Driver}\" is in focus and camera image plate solve succeeded with ({solvedRa}, {solvedDec})");
            return true;
        }
        else if (solveTask.IsFaulted || solveTask.IsCanceled)
        {
            External.LogWarning($"Failed to plate solve guider \"{guider.Driver}\" captured frame due to: {solveTask.Exception?.Message}");
        }
        else
        {
            External.LogWarning($"Failed to plate solve guider \"{guider.Driver}\" without a specific reason (probably not enough stars detected)");
        }

        return false;
    }

    internal void Finalise(CancellationToken cancellationToken)
    {
        External.LogInfo("Executing session run finaliser: Stop guiding, stop tracking, disconnect guider, close covers, cool to ambient temp, turn off cooler, park scope.");

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

        var parkInitiated = Catch(() => mount.Driver.CanPark && mount.Driver.Park());

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
            External.LogError($"Partially failed shut-down of session: {string.Join(", ", shutdownReport.Select(p => p.Key + ": " + (p.Value ? "success" : "fail")))}");
        }
        else
        {
            External.LogInfo("Shutdown complete, session ended. Please turn off mount and camera cooler power.");
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

        if (mount.Driver.AtPark && (!mount.Driver.CanUnpark || !mount.Driver.Unpark()))
        {
            External.LogError($"Mount {mount.Device.DisplayName} is parked but cannot be unparked. Aborting.");
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
            External.LogError("Failed to set camera cooler setpoint to current CCD temperature, aborting session.");
            return false;
        }

        if (MoveTelescopeCoversToState(CoverStatus.Open, CancellationToken.None))
        {
            External.LogInfo("All covers opened, and calibrator turned off.");
        }
        else
        {
            External.LogError("Openening telescope covers failed, aborting session.");
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

        Observation? observation;
        while ((observation = CurrentObservation) is not null && !cancellationToken.IsCancellationRequested)
        {
            mount.EnsureTracking();

            External.LogInfo($"Stop guiding to start slewing mount to target {observation}.");
            guider.Driver.StopCapture(TimeSpan.FromSeconds(15));

            var (postCondition, hourAngleAtSlewTime) = mount.Driver.SlewToTarget(Configuration.MinHeightAboveHorizon, observation.Target, cancellationToken);
            if (postCondition is SlewPostCondition.SkipToNext)
            {
                _ = AdvanceObservation();
                continue;
            }
            else if (postCondition is SlewPostCondition.Cancelled or SlewPostCondition.Abort)
            {
                break;
            }

            var guidingSuccess = guider.Driver.StartGuidingLoop(Configuration.GuidingTries, External, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                External.LogWarning($"Cancellation requested, abort setting up guider \"{guider.Driver}\" and quit imaging loop.");
                break;
            }
            else if (!guidingSuccess)
            {
                External.LogError($"Skipping target {observation} as starting guider \"{guider.Driver}\" failed after trying twice.");
                _ = AdvanceObservation();
                continue;
            }

            var imageLoopStart = MountUtcNow;
            if (!ImagingLoop(observation, hourAngleAtSlewTime, cancellationToken))
            {
                External.LogError($"Imaging loop for {observation} did not complete successfully, total runtime: {MountUtcNow - imageLoopStart:c}");
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
        var tickSec = TimeSpan.FromSeconds(tickGCD);
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

                    External.LogInfo($"Camera #{i + 1} {camerDriver.Name} starting {frameExpTime} exposure of frame #{frameNo}.");
                }
            }

            var elapsed = WriteQueuedImagesToFitsFiles();
            var tickMinusElapsed = tickSec - elapsed - overslept;
            // clear overslept
            overslept = TimeSpan.Zero;
            if (cancellationToken.IsCancellationRequested)
            {
                External.LogWarning("Cancellation rquested, all images in queue written to disk, abort image acquisition and quit imaging loop");
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
                    var frameNo = frameNumbers[i];
                    var frameExpTime = TimeSpan.FromSeconds(subExposuresSec[i]);
                    do // wait for image loop
                    {
                        if (camDriver.ImageReady is true && camDriver.Image is { Width: > 0, Height: > 0 } image)
                        {
                            imageFetchSuccess[i] = true;
                            External.LogInfo($"Camera #{i + 1} {camDriver.Name} finished {frameExpTime} exposure of frame #{frameNo}");

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
                    while (overslept < (tickSec / 5)
                        && camDriver.CameraState is not CameraState.Error and not CameraState.NotConnected
                        && !cancellationToken.IsCancellationRequested
                    );

                    if (!imageFetchSuccess[i])
                    {
                        External.LogError($"Failed fetching camera #{(i + 1)} {camDriver.Name} {frameExpTime} exposure of frame #{frameNo}, camera state: {camDriver.CameraState}");
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
                    if (guider.Driver.DitherWait(Configuration.DitherPixel, Configuration.SettlePixel, Configuration.SettleTime, WriteQueuedImagesToFitsFiles, External, cancellationToken))
                    {
                        External.LogInfo($"Dithering using \"{guider.Driver}\" succeeded.");
                    }
                    else
                    {
                        External.LogError($"Dithering using \"{guider.Driver}\" failed, aborting.");
                        return false;
                    }
                }
                else
                {
                    External.LogInfo($"Skipping dithering ({ditherRound % Configuration.DitherEveryNthFrame}/{Configuration.DitherEveryNthFrame} frame)");
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
                    External.LogException(ex, $"while saving frame #{imageWrite.frameNumber} taken at {imageWrite.expStartTime:o} by {imageWrite.image.ImageMeta.Instrument}");
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
                        External.LogError($"Failed to {(shouldOpen ? "open" : "close")} cover of telescope {(i + 1)}.");
                    }
                }
                else if (!calibratorActionCompleted)
                {
                    External.LogError($"Failed to turn off calibrator of telescope {(i + 1)}, current state {cover.Driver.CalibratorState}");
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
                    External.LogInfo($"Cover {cover.Device.DisplayName} of telescope {i + 1} is still {cs} while reaching {finalCoverState}, waiting.");
                    External.Sleep(TimeSpan.FromSeconds(3));
                }

                var finalCoverStateAfterMoving = cover.Driver.CoverState;
                finalCoverStateReached[i] |= finalCoverStateAfterMoving == finalCoverState;

                if (!finalCoverStateReached[i])
                {
                    External.LogError($"Failed to {(shouldOpen ? "open" : "close")} cover of telescope {(i + 1)} after moving, current state {finalCoverStateAfterMoving}");
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
                External.LogWarning("Cancellation requested, quiting cooldown loop");
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

        External.LogInfo($"Writing FITS file {frameFolder}/{fitsFileName}");
        image.WriteToFitsFile(Path.Combine(frameFolder, fitsFileName));
    }

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
}
