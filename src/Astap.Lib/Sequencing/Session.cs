using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.Focus;
using Astap.Lib.Astrometry.PlateSolve;
using Astap.Lib.Astrometry.SOFA;
using Astap.Lib.Devices;
using Astap.Lib.Devices.Guider;
using Astap.Lib.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using static Astap.Lib.CollectionHelper;
using static Astap.Lib.Stat.StatisticsHelper;

namespace Astap.Lib.Sequencing;

public class Session
{
    private readonly IImageAnalyser _analyser;
    private readonly IPlateSolver _plateSolver;
    private readonly IExternal _external;
    private readonly IReadOnlyList<Observation> _observations;
    private readonly ConcurrentQueue<GuiderEventArgs> _guiderEvents = new();
    private int _activeObservation;

    public Session(
        Setup setup,
        in SessionConfiguration sessionConfiguration,
        IImageAnalyser analyser,
        IPlateSolver plateSolver,
        IExternal external,
        Observation observation,
        params Observation[] observations
    )
        : this(setup, sessionConfiguration, analyser, plateSolver, external, ConcatToReadOnlyList(observation, observations))
    {
        // calls below
    }

    public Session(
        Setup setup,
        in SessionConfiguration sessionConfiguration,
        IImageAnalyser analyser,
        IPlateSolver plateSolver,
        IExternal external,
        IReadOnlyList<Observation> observations
    )
    {
        Setup = setup;
        Configuration = sessionConfiguration;
        _analyser = analyser;
        _plateSolver = plateSolver;
        _external = external;
        _observations = observations.Count > 0 ? observations : throw new ArgumentException("Need at least one observation", nameof(observations));
        _activeObservation = -1; // -1 means we have not started imaging yet
    }

    public Setup Setup { get; }

    public SessionConfiguration Configuration { get; }

    public Observation? CurrentObservation => _activeObservation is int active and >= 0 && active < _observations.Count ? _observations[active] : null;

    const int MAX_FAILSAFE = 1000;
    const int SETTLE_TIMEOUT_FACTOR = 3;

    public void Run(CancellationToken cancellationToken)
        => Run(Setup,
            Configuration,
            _guiderEvents,
            () => CurrentObservation,
            () => Interlocked.Increment(ref _activeObservation),
            _analyser,
            _plateSolver,
            _external,
            cancellationToken
        );

    internal static void Run(
        Setup setup,
        SessionConfiguration configuration,
        ConcurrentQueue<GuiderEventArgs> guiderEvents,
        Func<Observation?> currentObservation,
        Func<int> nextObservation,
        IImageAnalyser analyser,
        IPlateSolver plateSolver,
        IExternal external,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var active = nextObservation();
            // run initialisation code
            if (active == 0)
            {
                if (!Initialisation(setup, guiderEvents, external, cancellationToken))
                {
                    return;
                }
            }
            else if (currentObservation() is null)
            {
                return;
            }

            // TODO wait until 25 before astro dark to start cooling down without loosing time
            CoolCamerasToSetpoint(setup, configuration.SetpointCCDTemperature, configuration.CooldownRampInterval, 80, CoolDirection.Down, setpointIsCCDTemp: false, external, cancellationToken);

            // TODO wait until 5 min to astro dark, and/or implement IExternal.IsPolarAligned

            if (!InitialRoughFocus(setup, analyser, plateSolver, external, cancellationToken))
            {
                external.LogError("Failed to focus cameras (first time), aborting session.");
                return;
            }
            // TODO: Slew near meridian (opposite of pole), CalibrateGuider();

            ObservationLoop(setup, configuration, currentObservation, nextObservation, external, cancellationToken);
        }
        catch (Exception e)
        {
            external.LogException(e, "in main run loop, unrecoverable, aborting session.");
        }
        finally
        {
            Finalise(setup, configuration, external, cancellationToken);
        }
    }

    internal static bool InitialRoughFocus(Setup setup, IImageAnalyser analyser, IPlateSolver plateSolver, IExternal external, CancellationToken cancellationToken)
    {
        var mount = setup.Mount;
        var guider = setup.Guider;
        var distMeridian = TimeSpan.FromMinutes(15);

        TurnOnTracking(mount);

        external.LogInfo($"Slew mount {mount.Device.DisplayName} near zenith for focusing.");

        // coordinates not quite accurate but good enough for this purpose.
        if (!SlewToZenith(mount, distMeridian, external, cancellationToken))
        {
            return false;
        }

        const int guiderLoopTimeoutSec = 10;
        var solveTask = PlateSolveGuiderImageAsync(guider, mount.Driver.RightAscension, mount.Driver.Declination, guiderLoopTimeoutSec, plateSolver, external, cancellationToken);

        var plateSolveWaitTime = TimeSpan.Zero;
        while (!solveTask.IsCompleted && !cancellationToken.IsCancellationRequested && plateSolveWaitTime.TotalSeconds < guiderLoopTimeoutSec)
        {
            var spinWait = TimeSpan.FromMilliseconds(100);
            plateSolveWaitTime += spinWait;
            external.Sleep(spinWait);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            external.LogWarning($"Cancellation requested, abort setting up guider \"{guider.Driver}\" and quit imaging loop.");
            return false;
        }
        else if (solveTask.IsCompletedSuccessfully && solveTask.Result is var (solvedRa, solvedDec))
        {
            if (Catch(() => mount.Driver.SyncRaDecJ2000(solvedRa, solvedDec), external))
            {
                external.LogInfo($"Syncing mount {mount.Device.DisplayName} to ({solvedRa}, {solvedDec}) in {mount.Driver.EquatorialSystem} succeeded");
            }
            else
            {
                external.LogWarning($"Syncing mount {mount.Device.DisplayName} to ({solvedRa}, {solvedDec}) in {mount.Driver.EquatorialSystem} failed");
            }
        }
        else if (solveTask.IsFaulted || solveTask.IsCanceled)
        {
            external.LogWarning($"Failed to plate solve guider \"{guider.Driver}\" captured frame due to: {solveTask.Exception?.Message}");
        }

        var count = setup.Telescopes.Count;
        for (var i = 0; i < count; i++)
        {
            setup.Telescopes[i].Camera.Driver.StartExposure(TimeSpan.FromSeconds(1), true);
        }

        var expTimesSec = new int[count];
        var hasRoughFocus = new bool[count];
        Array.Fill(expTimesSec, 1);

        var sw = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < count; i++)
            {
                var camDriver = setup.Telescopes[i].Camera.Driver;

                if (camDriver.ImageReady is true && camDriver.Image is { Width: > 0, Height: > 0 } image)
                {
                    var stars = analyser.FindStars(image, snrMin: 15);

                    if (stars.Count < 15)
                    {
                        expTimesSec[i]++;

                        if (sw.Elapsed + TimeSpan.FromSeconds(count * 5 + expTimesSec[i]) < distMeridian)
                        {
                            camDriver.StartExposure(TimeSpan.FromSeconds(expTimesSec[i]), true);
                        }
                    }
                    else
                    {
                         hasRoughFocus[i] = true;
                    }
                }
            }

            if (sw.Elapsed > distMeridian)
            {
                sw.Reset();
                if (!SlewToZenith(mount, distMeridian, external, cancellationToken))
                {
                    return false;
                }
                sw.Start();
            }

            if (hasRoughFocus.Any(v => v))
            {
                return true;
            }

            external.Sleep(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    internal static Task<(double ra, double dec)?> PlateSolveGuiderImageAsync(
        Guider guider,
        double raJ2000,
        double decJ2000,
        uint guiderLoopTimeoutSec,
        IPlateSolver plateSolver,
        IExternal external,
        CancellationToken cancellationToken
    )
    {
        if (Catch(() => guider.Driver.Loop(guiderLoopTimeoutSec, external.Sleep), external))
        {
            if (guider.Driver.SaveImage(Path.Combine(external.OutputFolder, "Guider")) is { Length: > 0 } file)
            {
                if (!guider.Driver.TryGetImageDim(out var dim))
                {
                    external.LogWarning($"Failed to obtain image dimensions of \"{guider.Driver}\" camera, will use blind search.");
                }

                return plateSolver.SolveFileAsync(
                    file,
                    dim,
                    searchOrigin: (raJ2000, decJ2000),
                    searchRadius: 7,
                    cancellationToken: cancellationToken
                );
            }
            else
            {
                external.LogWarning($"Failed to obtain image from guider \"{guider.Driver}\"");
                return Task.FromResult<(double, double)?>(null);
            }
        }
        else
        {
            external.LogWarning($"Failed to start guider \"{guider.Driver}\" capture loop after {guiderLoopTimeoutSec}s");
            return Task.FromResult<(double, double)?>(null);
        }
    }

    internal static bool SlewToZenith(Mount mount, TimeSpan distMeridian, IExternal external, CancellationToken cancellationToken)
    {
        if (mount.Driver.CanSlew && mount.Driver.SlewHourAngleDecAsync((TimeSpan.FromHours(12) - distMeridian).TotalHours, mount.Driver.SiteLatitude))
        {
            while (mount.Driver.IsSlewing && !cancellationToken.IsCancellationRequested)
            {
                external.Sleep(TimeSpan.FromSeconds(1));
            }

            return !cancellationToken.IsCancellationRequested;
        }

        return false;
    }

    internal static void Finalise(Setup setup, SessionConfiguration configuration, IExternal external, CancellationToken cancellationToken)
    {
        external.LogInfo("Executing session run finaliser: Stop tracking, disconnect guider, close covers, cool to ambient temp, turn off cooler, park scope.");

        var mount = setup.Mount;
        var guider = setup.Guider;

        var maybeCoversClosed = null as bool?;
        var maybeCooledCamerasToAmbient = null as bool?;
        var trackingStopped = Catch(() => mount.Driver.CanSetTracking && !(mount.Driver.Tracking = false), external);

        if (trackingStopped)
        {
            maybeCoversClosed ??= Catch(CloseCovers, external);
            maybeCooledCamerasToAmbient ??= Catch(CoolCamerasToAmbient, external);
        }

        var guiderStopped = Catch(() => !(guider.Driver.Connected = false), external);

        var parkInitiated = Catch(() => mount.Driver.CanPark && mount.Driver.Park(), external);

        var parkCompleted = parkInitiated && Catch(() =>
        {
            int i = 0;
            while (!mount.Driver.AtPark && i++ < MAX_FAILSAFE)
            {
                external.Sleep(TimeSpan.FromMilliseconds(100));
            }

            return mount.Driver.AtPark;
        }, external);

        if (parkCompleted)
        {
            maybeCoversClosed ??= Catch(CloseCovers, external);
            maybeCooledCamerasToAmbient ??= Catch(CoolCamerasToAmbient, external);
        }

        var coversClosed = maybeCoversClosed ??= Catch(CloseCovers, external);
        var cooledCamerasToAmbient = maybeCooledCamerasToAmbient ??= Catch(CoolCamerasToAmbient, external);

        var shutdownReport = new Dictionary<string, bool>
        {
            ["Covers closed"] = coversClosed,
            ["Stop tracking"] = trackingStopped,
            ["Stop guider"] = guiderStopped,
            ["Park initiated"] = parkInitiated,
            ["Park completed"] = parkCompleted,
            ["Cooled up cameras"] = cooledCamerasToAmbient
        };

        for (var i = 0; i < setup.Telescopes.Count; i++)
        {
            var camDriver = setup.Telescopes[i].Camera.Driver;
            if (Catch(() => camDriver.CanGetCoolerOn, external))
            {
                shutdownReport[$"Camera #{(i + 1)} Cooler Off"] = Catch(() => !camDriver.CoolerOn || !(camDriver.CoolerOn = false), external);
            }
            if (Catch(() => camDriver.CanGetCoolerPower, external))
            {
                shutdownReport[$"Camera #{(i + 1)} Cooler Power <= 0.1"] = Catch(() => camDriver.CoolerPower is <= 0.1, external);
            }
            if (Catch(() => camDriver.CanGetHeatsinkTemperature, external))
            {
                shutdownReport[$"Camera #{(i + 1)} Temp near ambient"] = Catch(() => Math.Abs(camDriver.CCDTemperature - camDriver.HeatSinkTemperature) < 1d, external);
            }
        }

        if (shutdownReport.Values.Any(v => !v))
        {
            external.LogError($"Partially failed shut-down of session: {string.Join(", ", shutdownReport.Select(p => p.Key + ": " + (p.Value ? "success" : "fail")))}");
        }
        else
        {
            external.LogInfo("Shutdown complete, session ended. Please turn off mount and camera cooler power.");
        }

        bool CoolCamerasToAmbient() => Session.CoolCamerasToAmbient(setup, configuration.CoolupRampInterval, external);

        bool CloseCovers() => MoveTelescopeCoversToSate(setup, CoverStatus.Closed, external, cancellationToken);
    }

    /// <summary>
    /// Does one-time (per session) initialisation, e.g. connecting, unparking
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>True if initialisation was successful.</returns>
    internal static bool Initialisation(Setup setup, ConcurrentQueue<GuiderEventArgs> guiderEvents, IExternal external, CancellationToken cancellationToken)
    {
        var mount = setup.Mount;
        var guider = setup.Guider;

        setup.Mount.Connected = true;
        guider.Connected = true;

        if (mount.Driver.AtPark && (!mount.Driver.CanUnpark || !mount.Driver.Unpark()))
        {
            external.LogError($"Mount {mount.Device.DisplayName} is parked but cannot be unparked. Aborting.");
            return false;
        }

        for (var i = 0; i < setup.Telescopes.Count; i++)
        {
            var telescope = setup.Telescopes[i];
            var camera = telescope.Camera;
            camera.Connected = true;

            // copy over denormalised properties if required
            camera.Driver.Telescope ??= telescope.Name;
            if (camera.Driver.FocalLength is <= 0)
            {
                camera.Driver.FocalLength = telescope.FocalLength;
            }
        }

        if (!CoolCamerasToSensorTemp(setup, TimeSpan.FromSeconds(10), external, cancellationToken))
        {
            external.LogError("Failed to set camera cooler setpoint to current CCD temperature, aborting session.");
            return false;
        }

        if (MoveTelescopeCoversToSate(setup, CoverStatus.Open, external, CancellationToken.None))
        {
            external.LogInfo("All covers opened, and calibrator turned off.");
        }
        else
        {
            external.LogError("Openening telescope covers failed, aborting session.");
            return false;
        }

        guider.Driver.UnhandledEvent += (_, e) => guiderEvents.Enqueue(e);
        guider.Driver.GuidingErrorEvent +=  (_, e) => guiderEvents.Enqueue(e);
        guider.Driver.ConnectEquipment();

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return true;
    }

    internal static void ObservationLoop(
        Setup setup,
        SessionConfiguration configuration,
        Func<Observation?> currentObservation,
        Func<int> nextObservation,
        IExternal external,
        CancellationToken cancellationToken
    )
    {
        var guider = setup.Guider;
        var mount = setup.Mount;

        Observation? observation;
        while ((observation = currentObservation()) is not null && !cancellationToken.IsCancellationRequested)
        {
            TurnOnTracking(mount);

            external.LogInfo($"Stop guiding to start slewing mount to target {observation}.");
            guider.Driver.StopCapture();

            // skip target if slew is not successful
            var az = double.NaN;
            var alt = double.NaN;
            if (!mount.Driver.TryGetTransform(out var transform)
                || !mount.Driver.TryTransformJ2000ToMountNative(transform, observation.Target.RA, observation.Target.Dec, updateTime: false, out var raMount, out var decMount, out az, out alt)
                || double.IsNaN(alt)
                || alt < configuration.MinHeightAboveHorizon
                || !mount.Driver.SlewRaDecAsync(raMount, decMount)
            )
            {
                external.LogError($"Failed to slew {mount.Device.DisplayName} to target {observation} az={az:0.00} alt={alt:0.00}, skipping.");
                nextObservation();
                continue;
            }

            double hourAngleAtSlewTime;
            int failsafeCounter = 0;
            try
            {
                while (mount.Driver.IsSlewing && failsafeCounter++ < MAX_FAILSAFE && !cancellationToken.IsCancellationRequested)
                {
                    external.Sleep(TimeSpan.FromSeconds(1));
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    external.LogWarning($"Cancellation requested, abort slewing to target {observation} and quit imaging loop.");
                    break;
                }

                if (mount.Driver.IsSlewing || failsafeCounter == MAX_FAILSAFE)
                {
                    throw new InvalidOperationException($"Failsafe activated when slewing {mount.Device.DisplayName} to {observation}.");
                }

                if (double.IsNaN(hourAngleAtSlewTime = mount.Driver.HourAngle))
                {
                    throw new InvalidOperationException($"Could not obtain hour angle after slewing {mount.Device.DisplayName} to {observation}.");
                }

                external.LogInfo($"Finished slewing mount {mount.Device.DisplayName} to target {observation}.");
            }
            catch (Exception e)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    external.LogException(e, $"while slewing to target {observation} failed and cancellation requested, aborting.");
                    break;
                }
                else
                {
                    external.LogException(e, $"while slewing to target {observation} failed, skipping.");
                    nextObservation();

                    continue;
                }
            }

            var guidingSuccess = StartGuidingLoop(guider, configuration, external, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                external.LogWarning($"Cancellation requested, abort setting up guider \"{guider.Driver}\" and quit imaging loop.");
                break;
            }
            else if (!guidingSuccess)
            {
                external.LogError($"Skipping target {observation} as starting guider \"{guider.Driver}\" failed after trying twice.");
                nextObservation();
                continue;
            }

            ImagingLoop(setup, configuration, observation, hourAngleAtSlewTime, external, cancellationToken);
        } // end observation loop
    }

    internal static bool StartGuidingLoop(Guider guider, SessionConfiguration configuration, IExternal external, CancellationToken cancellationToken)
    {
        bool guidingSuccess = false;
        int startGuidingTries = 0;

        while (!guidingSuccess && ++startGuidingTries <= configuration.GuidingTries && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var settlePix = 0.3 + (startGuidingTries * 0.2);
                var settleTime = 15 + (startGuidingTries * 5);
                var settleTimeout = settleTime * SETTLE_TIMEOUT_FACTOR;

                external.LogInfo($"Start guiding using {guider.Device.DeviceId}, settle pixels: {settlePix}, settle time: {settleTime}s, timeout: {settleTimeout}s.");
                guider.Driver.Guide(settlePix, settleTime, settleTimeout);

                var failsafeCounter = 0;
                while (guider.Driver.IsSettling() && failsafeCounter++ < MAX_FAILSAFE && !cancellationToken.IsCancellationRequested)
                {
                    external.Sleep(TimeSpan.FromSeconds(10));
                }

                guidingSuccess = failsafeCounter < MAX_FAILSAFE && guider.Driver.IsGuiding();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                else if (!guidingSuccess)
                {
                    external.Sleep(TimeSpan.FromMinutes(startGuidingTries));
                }
            }
            catch (Exception e)
            {
                external.LogException(e, $"while on try #{startGuidingTries} checking if {guider.Device.DeviceId} is guiding.");
                guidingSuccess = false;
            }
        }

        return guidingSuccess;
    }

    internal static void ImagingLoop(Setup setup, SessionConfiguration configuration, in Observation observation, double hourAngleAtSlewTime, IExternal external, CancellationToken cancellationToken)
    {
        var guider = setup.Guider;
        var mount = setup.Mount;
        var scopes = setup.Telescopes.Count;
        var frameNumbers = new int[scopes];
        var subExposuresSec = new int[scopes];

        for (var i = 0; i < scopes; i++)
        {
            var camera = setup.Telescopes[i].Camera;

            // TODO per camera exposure calculation, i.e. via f/ratio
            var subExposure = observation.SubExposure;
            subExposuresSec[i] = (int)Math.Ceiling(subExposure.TotalSeconds);
        }

        var maxSubExposureSec = subExposuresSec.Max();
        var tickGCD = GCD(subExposuresSec);
        var tickLCM = LCM(tickGCD, subExposuresSec);
        var tickSec = TimeSpan.FromSeconds(tickGCD);
        var ticksPerMaxSubExposure = maxSubExposureSec / tickGCD;
        var expStartTimes = new DateTime[scopes];
        var expTicks = new int[scopes];
        var ditherRound = 0;

        var overslept = TimeSpan.Zero;
        var imageWriteQueue = new Queue<(Image image, Observation observation, DateTime expStartTime, int frameNumber)>();
        while (!cancellationToken.IsCancellationRequested
            && mount.Connected
            && Catch(() => mount.Driver.Tracking, external)
            && guider.Connected
            && Catch(guider.Driver.IsGuiding, external)
            && IsOnSamePierSide()
            && mount.Driver.TryGetUTCDate(out var expStartTime)
        )
        {
            for (var i = 0; i < scopes; i++)
            {
                var camDriver = setup.Telescopes[i].Camera.Driver;
                switch (camDriver.CameraState)
                {
                    case CameraState.Idle:
                        var subExposureSec = subExposuresSec[i];
                        var frameExpTime = TimeSpan.FromSeconds(subExposureSec);
                        camDriver.StartExposure(frameExpTime, true);
                        expStartTimes[i] = expStartTime;
                        expTicks[i] = (int)(subExposureSec / tickGCD);
                        var frameNo = ++frameNumbers[i];

                        external.LogInfo($"Camera #{(i + 1)} {camDriver.Name} starting {frameExpTime} exposure of frame #{frameNo}.");
                        break;
                }
            }

            var elapsed = WriteQueuedImagesToFitsFiles();
            var tickMinusElapsed = tickSec - elapsed - overslept;
            // clear overslept
            overslept = TimeSpan.Zero;
            if (cancellationToken.IsCancellationRequested)
            {
                external.LogWarning("Cancellation rquested, abort image acquisition and quit imaging loop");
                break;
            }
            else if (tickMinusElapsed > TimeSpan.Zero)
            {
                external.Sleep(tickMinusElapsed);
            }

            var imageFetchSuccess = new bool[scopes];
            for (var i = 0; i < scopes && !cancellationToken.IsCancellationRequested; i++)
            {
                var tick = --expTicks[i];

                var camDriver = setup.Telescopes[i].Camera.Driver;
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
                            external.LogInfo($"Camera #{(i + 1)} {camDriver.Name} finished {frameExpTime} exposure of frame #{frameNo}");

                            imageWriteQueue.Enqueue((image, observation, expStartTime, frameNo));
                            break;
                        }
                        else
                        {
                            var spinDuration = TimeSpan.FromMilliseconds(100);
                            overslept += spinDuration;
                            external.Sleep(spinDuration);
                        }
                    }
                    while (overslept < (tickSec / 5)
                        && camDriver.CameraState is not CameraState.Error and not CameraState.NotConnected
                        && !cancellationToken.IsCancellationRequested
                    );

                    if (!imageFetchSuccess[i])
                    {
                        external.LogError($"Failed fetching camera #{(i + 1)} {camDriver.Name} {frameExpTime} exposure of frame #{frameNo}, camera state: {camDriver.CameraState}");
                    }
                }
            }

            var allimageFetchSuccess = Array.TrueForAll(imageFetchSuccess, x => x);
            var shouldDither = ++ditherRound % configuration.DitherEveryNFrame == 0;
            if (!IsOnSamePierSide())
            {
                // write all images as the loop is ending here
                WriteQueuedImagesToFitsFiles();

                // TODO stop exposures (if we can, and if there are any)

                if (observation.AcrossMeridian)
                {
                    // TODO, stop guiding flip, resync, verify and restart guiding
                    throw new InvalidOperationException("Observing across meridian is not yet supported");
                }
                else
                {

                }
            }
            else if (allimageFetchSuccess && shouldDither)
            {
                var ditherPixel = configuration.DitherPixel;
                var settlePixel = configuration.SettlePixel;
                var settleTime = configuration.SettleTime;
                var settleTimeout = (settleTime * SETTLE_TIMEOUT_FACTOR);

                external.LogInfo($"Start dithering pixel={ditherPixel} settlePixel={settlePixel} settleTime={settleTime}, timeout={settleTimeout}");

                guider.Driver.Dither(ditherPixel, settlePixel, settleTime.TotalSeconds, settleTimeout.TotalSeconds);

                elapsed = WriteQueuedImagesToFitsFiles();

                for (var i = 0; i < SETTLE_TIMEOUT_FACTOR; i++)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        SleepWithOvertime(settleTime, elapsed);
                    }
                    else
                    {
                        break;
                    }
                    if (guider.Driver.TryGetSettleProgress(out var settleProgress) && settleProgress is { Done: false })
                    {
                        if (settleProgress.Error is { Length: > 0 } error)
                        {
                            external.LogError($"Settling after dithering failed with: {error}");
                        }
                        else
                        {
                            external.LogInfo($"Settle still in progress: settle pixel={settleProgress.SettlePx} dist={settleProgress.Distance}");
                        }
                    }
                    else
                    {
                        if (settleProgress?.Error is { Length: > 0 } error)
                        {
                            external.LogError($"Settling after dithering failed with: {error} pixel={settleProgress.SettlePx} dist={settleProgress.Distance}");
                        }
                        else if (settleProgress is not null)
                        {
                            external.LogInfo($"Settle finished: settle pixel={settleProgress.SettlePx} pixel={settleProgress.SettlePx} dist={settleProgress.Distance}");
                        }
                        break;
                    }
                }
            }
            else if (allimageFetchSuccess)
            {
                external.LogInfo($"Skipping dithering ({ditherRound}/{configuration.DitherEveryNFrame} frame)");
            }
        } // end imaging loop

        bool IsOnSamePierSide()
        {
            var pierSide = Catch(() => mount.Driver.SideOfPier, external, PierSide.Unknown);
            var currentHourAngle = Catch(() => mount.Driver.HourAngle, external, double.NaN);
            return pierSide == mount.Driver.ExpectedSideOfPier
                && !double.IsNaN(currentHourAngle)
                && (pierSide != PierSide.Unknown || Math.Sign(hourAngleAtSlewTime) == Math.Sign(currentHourAngle));
        }

        TimeSpan WriteQueuedImagesToFitsFiles()
        {
            var stopWatch = Stopwatch.StartNew();
            while (imageWriteQueue.TryDequeue(out var imageWrite))
            {
                try
                {
                    WriteImageToFitsFile(imageWrite.image, imageWrite.observation, imageWrite.expStartTime, imageWrite.frameNumber, external);
                }
                catch (Exception ex)
                {
                    external.LogException(ex, $"while saving frame #{imageWrite.frameNumber} taken at {imageWrite.expStartTime:o} by {imageWrite.image.ImageMeta.Instrument}");
                }
            }
            var elapsed = stopWatch.Elapsed;
            stopWatch.Stop();
            return elapsed;
        }

        void SleepWithOvertime(TimeSpan sleep, TimeSpan extra)
        {
            var adjustedTime = sleep - extra;
            if (adjustedTime >= TimeSpan.Zero)
            {
                overslept = TimeSpan.Zero;
                external.Sleep(adjustedTime);
            }
            else
            {
                overslept = adjustedTime.Negate();
            }
        }
    }

    internal static void TurnOnTracking(Mount mount, TrackingSpeed speed = TrackingSpeed.Sidereal)
    {
        if (mount.Driver.CanSetTracking && (mount.Driver.TrackingSpeed != speed || !mount.Driver.Tracking))
        {
            mount.Driver.TrackingSpeed = speed;
            mount.Driver.Tracking = true;
        }
    }

    internal static bool MoveTelescopeCoversToSate(Setup setup, CoverStatus finalState, IExternal external, CancellationToken cancellationToken)
    {
        var count = setup.Telescopes.Count;

        var commandSuccess = new bool[count];

        for (var i = 0; i < count; i++)
        {
            if (setup.Telescopes[i].Cover is { } cover)
            {
                cover.Connected = true;

                if (cover.Driver.CoverState is CoverStatus.NotPresent || cover.Driver.CalibratorOff())
                {
                    external.LogInfo($"Calibrator {cover.Device.DisplayName} of telescope {i + 1} is off.");

                    commandSuccess[i] = cover.Driver.CoverState == finalState || finalState switch
                    {
                        CoverStatus.Closed => cover.Driver.Close(),
                        CoverStatus.Open => cover.Driver.Open(),
                        _ => true
                    };
                }
                else
                {
                    commandSuccess[i] = false;
                }
            }
            else
            {
                commandSuccess[i] = true;
            }
        }

        var finalStateReached = new bool[count];
        for (var i = 0; i < count; i++)
        {
            if (setup.Telescopes[i].Cover is { } cover)
            {
                int failSafe = 0;
                CoverStatus cs;
                while (!(finalStateReached[i] = (cs = cover.Driver.CoverState) == finalState) && cs is CoverStatus.Moving or CoverStatus.Unknown && !cancellationToken.IsCancellationRequested && ++failSafe < MAX_FAILSAFE)
                {
                    external.LogInfo($"Cover {cover.Device.DisplayName} of telescope {i + 1} is still {cs} while reaching {finalState}, waiting.");
                    external.Sleep(TimeSpan.FromSeconds(3));
                }

                finalStateReached[i] |= cover.Driver.CoverState == finalState;
            }
        }

        return Array.TrueForAll(finalStateReached, x => x);
    }

    /// <summary>
    /// Idea is that we keep cooler on but only on the currently reached temperature, so we have less cases to manage in the imaging loop.
    /// Assumes that power is switched on.
    /// </summary>
    /// <param name="setup">Setup contains all telescopes (with cameras)</param>
    /// <param name="rampTime">Interval between temperature checks</param>
    /// <param name="external">Used for <see cref="IExternal.Sleep(TimeSpan)"/> and logging.</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal static bool CoolCamerasToSensorTemp(Setup setup, TimeSpan rampTime, IExternal external, CancellationToken cancellationToken)
        => CoolCamerasToSetpoint(setup, null, rampTime, 0.1, CoolDirection.Up, setpointIsCCDTemp: true, external, cancellationToken);


    /// <summary>
    /// Attention: Cannot be cancelled (as it would possibly destroy the cameras)
    /// </summary>
    /// <param name="setup">Setup contains all telescopes (with cameras)</param>
    /// <param name="rampTime">Interval between temperature checks</param>
    /// <param name="external">Used for <see cref="IExternal.Sleep(TimeSpan)"/> and logging.</param>
    /// <returns>True if setpoint temperature was reached.</returns>
    internal static bool CoolCamerasToAmbient(Setup setup, TimeSpan rampTime, IExternal external)
        => CoolCamerasToSetpoint(setup, null, rampTime, 0.1, CoolDirection.Up, setpointIsCCDTemp: false, external, CancellationToken.None);

    /// <summary>
    /// Assumes that power is on (c.f. <see cref="CoolCamerasToSensorTemp"/>).
    /// </summary>
    /// <param name="maybeSetpointTemp">Desired degrees Celcius setpoint temperature, if null then ambient temp is chosen</param>
    /// <param name="rampInterval">interval to wait until further adjusting setpoint.</param>
    internal static bool CoolCamerasToSetpoint(
        Setup setup,
        int? maybeSetpointTemp,
        TimeSpan rampInterval,
        double thresPower,
        CoolDirection direction,
        bool setpointIsCCDTemp,
        IExternal external,
        CancellationToken cancellationToken
    )
    {
        var count = setup.Telescopes.Count;
        var isRamping = new bool[count];
        var thresholdReachedConsecutiveCounts = new int[count];

        var accSleep = TimeSpan.Zero;
        do
        {
            for (var i = 0; i < setup.Telescopes.Count; i++)
            {
                var camera = setup.Telescopes[i].Camera;
                if (camera.Connected
                    && camera.Driver.CanSetCCDTemperature
                    && camera.Driver.CanGetCoolerOn
                    && camera.Driver.CanGetHeatsinkTemperature
                    && camera.Driver.CCDTemperature is double ccdTemp and >= -40 and <= 50
                    && camera.Driver.HeatSinkTemperature is double heatSinkTemp and >= -40 and <= 50
                )
                {
                    var coolerPower = CoolerPower(camera);
                    var setpointTemp = maybeSetpointTemp ?? Math.Round(setpointIsCCDTemp ? Math.Min(ccdTemp, heatSinkTemp) : heatSinkTemp);

                    if (direction.NeedsFurtherRamping(ccdTemp, setpointTemp)
                        && (double.IsNaN(coolerPower) || !camera.Driver.CoolerOn || !direction.ThresholdPowerReached(coolerPower, thresPower))
                    )
                    {
                        var actualSetpointTemp = camera.Driver.SetCCDTemperature = direction.SetpointTemp(ccdTemp, setpointTemp);

                        string coolerPrev;
                        if (IsCoolerOn(camera))
                        {
                            coolerPrev = "";
                        }
                        else
                        {
                            coolerPrev = "off -> ";
                            camera.Driver.CoolerOn = true;
                        }

                        isRamping[i] = true;
                        thresholdReachedConsecutiveCounts[i] = 0;

                        external.LogInfo($"Camera {(i + 1)} setpoint temperature {setpointTemp:0.00} °C not yet reached, " +
                            $"cooling {direction.ToString().ToLowerInvariant()} stepwise, currently at {actualSetpointTemp:0.00} °C. " +
                            $"Heatsink={heatSinkTemp:0.00} °C, CCD={ccdTemp:0.00} °C, Power={CoolerPower(camera):0.00}%, Cooler={coolerPrev}{(IsCoolerOn(camera) ? "on" : "off")}.");
                    }
                    else if (++thresholdReachedConsecutiveCounts[i] < 2)
                    {
                        isRamping[i] = true;

                        external.LogInfo($"Camera {(i + 1)} setpoint temperature {setpointTemp:0.00} °C or {thresPower:0.00} % power reached. "
                            + $"Heatsink={heatSinkTemp:0.00} °C, CCD={ccdTemp:0.00} °C, Power={CoolerPower(camera):0.00}%, Cooler={(IsCoolerOn(camera) ? "on" : "off")}.");
                    }
                    else
                    {
                        isRamping[i] = false;

                        external.LogInfo($"Camera {(i + 1)} setpoint temperature {setpointTemp:0.00} °C or {thresPower:0.00} % power reached twice in a row. "
                            + $"Heatsink={heatSinkTemp:0.00} °C, CCD={ccdTemp:0.00} °C, Power={CoolerPower(camera):0.00}%, Cooler={(IsCoolerOn(camera) ? "on" : "off")}.");
                    }
                }
                else
                {
                    isRamping[i] = false;

                    var setpointTemp = (maybeSetpointTemp.HasValue ? $"{maybeSetpointTemp.Value:0.00} °C" : "ambient");
                    external.LogWarning($"Skipping camera {(i + 1)} setpoint temperature {setpointTemp} as we cannot get the current CCD temperature or cooling is not supported. Cooler is {(IsCoolerOn(camera) ? "on" : "off")}.");
                }
            }

            accSleep += rampInterval;
            if (cancellationToken.IsCancellationRequested)
            {
                external.LogWarning("Cancellation requested, quiting cooldown loop");
                break;
            }
            else
            {
                external.Sleep(rampInterval);
            }
        } while (isRamping.Any(p => p) && accSleep < rampInterval * 100 && !cancellationToken.IsCancellationRequested);

        return !isRamping.Any(p => p);

        bool IsCoolerOn(Camera camera) => Catch(() => camera.Driver.CanGetCoolerOn && camera.Driver.CoolerOn, external);

        double CoolerPower(Camera camera) => Catch(() => camera.Driver.CanGetCoolerPower ? camera.Driver.CoolerPower : double.NaN, external, double.NaN);
    }

    internal static string GetSafeFileName(string name, char replace = '_')
    {
        char[] invalids = Path.GetInvalidFileNameChars();
        if (invalids.Contains(replace))
        {
            throw new ArgumentException($"Cannot use '{replace}' as the replacing character as it is itself not file system safe.", nameof(replace));
        }
        return new string(name.Select(c => invalids.Contains(c) ? replace : c).ToArray());
    }

    internal static void WriteImageToFitsFile(Image image, in Observation observation, DateTime subExpStartTime, int frameNumber, IExternal external)
    {
        var target = observation.Target;
        var outputFolder = external.OutputFolder;
        var targetFolder = GetSafeFileName(target.Name);
        // TODO make order of target/date configurable
        var dateFolder = GetSafeFileName(subExpStartTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo));
        var frameFolder = Directory.CreateDirectory(Path.Combine(outputFolder, targetFolder, dateFolder)).FullName;
        var fitsFileName = GetSafeFileName($"frame_{subExpStartTime:o}_{frameNumber}.fits");

        external.LogInfo($"Writing FITS file {targetFolder}/{fitsFileName}");
        image.WriteToFitsFile(Path.Combine(frameFolder, fitsFileName));
    }

    internal static T Catch<T>(Func<T> func, IExternal external, T @default = default)
        where T : struct
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            external.LogException(ex, $"while executing: {func.Method.Name}");
            return @default;
        }
    }
}
