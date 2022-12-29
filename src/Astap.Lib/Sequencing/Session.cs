using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.SOFA;
using Astap.Lib.Devices;
using Astap.Lib.Devices.Guider;
using Astap.Lib.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using static Astap.Lib.CollectionHelper;
using static Astap.Lib.Stat.StatisticsHelper;

namespace Astap.Lib.Sequencing;

public class Session
{
    private readonly IExternal _external;
    private readonly IReadOnlyList<Observation> _observations;
    private readonly ConcurrentQueue<GuiderEventArgs> _guiderEvents = new();
    private int _activeObservation;

    public Session(
        Setup setup,
        in SessionConfiguration sessionConfiguration,
        IExternal external,
        Observation observation,
        params Observation[] observations
    )
        : this(setup, sessionConfiguration, external, ConcatToReadOnlyList(observation, observations))
    {
        // calls below
    }

    public Session(
        Setup setup,
        in SessionConfiguration sessionConfiguration,
        IExternal external,
        IReadOnlyList<Observation> observations
    )
    {
        Setup = setup;
        Configuration = sessionConfiguration;
        _external = external;
        _observations = observations.Count > 0 ? observations : throw new ArgumentException("Need at least one observation", nameof(observations));
        _activeObservation = -1; // -1 means we have not started imaging yet
    }

    public Setup Setup { get; }

    public SessionConfiguration Configuration { get; }

    public Observation? CurrentObservation => _activeObservation is int active and >= 0 && active < _observations.Count ? _observations[active] : null;

    void Sleep(TimeSpan duration) => _external.Sleep(duration);
    void LogInfo(string info) => _external.LogInfo(info);
    void LogError(string info) => _external.LogError(info);

    const int MAX_FAILSAFE = 1000;
    const int SETTLE_TIMEOUT_FACTOR = 3;

    public void Run(CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;

        try
        {
            var active = Interlocked.Increment(ref _activeObservation);
            // run initialisation code
            if (active == 0)
            {
                if (!Initialisation(cancellationToken))
                {
                    return;
                }
            }
            else if (active >= _observations.Count)
            {
                return;
            }

            // TODO wait until 20 before astro dark to start cooling down without loosing time
            CoolCamerasToSetpoint(Configuration.SetpointCCDTemperature, Configuration.CooldownRampInterval, 80, CoolDirection.Down, cancellationToken);

            ObservationLoop(cancellationToken);
        }
        catch (Exception e)
        {
            LogError($"Unrecoverable error {e.Message} occured, aborting session");
        }
        finally
        {
            LogInfo("Executing session run finaliser: Stop tracking, disconnect guider, cool up, turn off cooler, park scope.");

            var stopTracking = Catch(() => mount.Driver.CanSetTracking && !(mount.Driver.Tracking = false));
            var stopGuider = Catch(() => !(guider.Driver.Connected = false));
            var initiatePark = Catch(() => mount.Driver.CanPark && mount.Driver.Park());
            var completePark = initiatePark && Catch(() =>
            {
                int i = 0;
                while (mount.Driver.IsSlewing && i++ < MAX_FAILSAFE)
                {
                    Sleep(TimeSpan.FromMilliseconds(100));
                }

                return mount.Driver.AtPark;
            });
            var cooledUpCameras = Catch(() => CoolCamerasToSetpoint(null, Configuration.CoolupRampInterval, 0.1, CoolDirection.Up, CancellationToken.None));

            var shutdownReport = new Dictionary<string, bool>
            {
                ["Stop tracking"] = stopTracking,
                ["Stop guider"] = stopGuider,
                ["Park initiated"] = initiatePark,
                ["Park completed"] = completePark,
                ["Cooled up cameras"] = cooledUpCameras
            };

            for (var i = 0; i < Setup.Telescopes.Count; i++)
            {
                var telescope = Setup.Telescopes[i];
                shutdownReport[$"Camera #{(i + 1)} Cooler Off"] = Catch(() => telescope.Camera.Driver.CanGetCoolerOn && !(telescope.Camera.Driver.CoolerOn = false));
            }

            if (shutdownReport.Values.Any(v => !v))
            {
                LogError($"Partially failed shut-down of session: {string.Join(", ", shutdownReport.Select(p => p.Key + ": " + (p.Value ? "success" : "fail")))}");
            }
        }
    }

    /// <summary>
    /// Does one-time (per session) initialisation, e.g. connecting, unparking
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>True if initialisation was successful.</returns>
    internal bool Initialisation(CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;

        mount.Connected = true;
        guider.Connected = true;

        if (mount.Driver.AtPark && (!mount.Driver.CanUnpark || !mount.Driver.Unpark()))
        {
            LogError($"Mount {mount.Device.DisplayName} is parked but cannot be unparked. Aborting.");
            return false;
        }

        for (var i = 0; i < Setup.Telescopes.Count; i++)
        {
            var telescope = Setup.Telescopes[i];
            var camera = telescope.Camera;
            camera.Connected = true;

            // copy over denormalised properties if required
            camera.Driver.Telescope ??= telescope.Name;
            if (camera.Driver.FocalLength is <= 0)
            {
                camera.Driver.FocalLength = telescope.FocalLength;
            }
        }

        Setup.Guider.Driver.UnhandledEvent += GuiderUnhandledEvent;
        Setup.Guider.Driver.GuidingErrorEvent += GuidingErrorEvent;
        Setup.Guider.Driver.ConnectEquipment();

        CoolCamerasToSensorTemp();

        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        // wait for cameras to settle (QHY dodgy power value, Simulator cam starts on 100% power, ...)
        Sleep(TimeSpan.FromSeconds(30));

        return true;
    }

    private void GuidingErrorEvent(object? sender, GuidingErrorEventArgs e)
    {
        _guiderEvents.Enqueue(e);
    }

    private void GuiderUnhandledEvent(object? sender, Devices.Guider.UnhandledEventArgs e)
    {
        _guiderEvents.Enqueue(e);
    }

    internal void ObservationLoop(CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;

        Observation? observation;
        while ((observation = CurrentObservation) is not null && !cancellationToken.IsCancellationRequested)
        {
            if (mount.Driver.CanSetTracking)
            {
                mount.Driver.TrackingSpeed = TrackingSpeed.Sidereal; // TODO: Support different tracking speed
                mount.Driver.Tracking = true;
            }

            LogInfo($"Stop guiding to start slewing mount to target {observation}");
            guider.Driver.StopCapture();

            // skip target if slew is not successful
            var az = double.NaN;
            var alt = double.NaN;
            if (!TryGetTransformOfObservation(mount, observation, out var transform)
                || !TryTransformJ2000ToMountNative(mount, transform, observation, out var raMount, out var decMount, out az, out alt)
                || double.IsNaN(alt)
                || alt < Configuration.MinHeightAboveHorizon
                || !mount.Driver.SlewAsync(raMount, decMount))
            {
                LogError($"Failed to slew {mount.Device.DisplayName} to target {observation} az={az:0.00} alt={alt:0.00}, skipping.");
                Interlocked.Increment(ref _activeObservation);
                continue;
            }

            double hourAngleAtSlewTime;
            int failsafeCounter = 0;
            try
            {
                while (mount.Driver.IsSlewing && failsafeCounter++ < MAX_FAILSAFE && !cancellationToken.IsCancellationRequested)
                {
                    Sleep(TimeSpan.FromSeconds(1));
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    LogError($"Cancellation requested, abort slewing to target {observation} and quit imaging loop");
                    break;
                }

                if (mount.Driver.IsSlewing || failsafeCounter == MAX_FAILSAFE)
                {
                    throw new InvalidOperationException($"Failsafe activated when slewing {mount.Device.DisplayName} to {observation}");
                }

                if (double.IsNaN(hourAngleAtSlewTime = mount.Driver.HourAngle))
                {
                    throw new InvalidOperationException($"Could not obtain hour angle after slewing {mount.Device.DisplayName} to {observation}");
                }

                LogInfo($"Finished slewing mount {mount.Device.DisplayName} to target {observation}");
            }
            catch (Exception e)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                else
                {
                    LogError($"Skipping target as slewing to target {observation} failed: {e.Message}");
                    Interlocked.Increment(ref _activeObservation);

                    continue;
                }
            }

            var guidingSuccess = StartGuidingLoop(guider, cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                LogError($"Cancellation requested, abort setting up guiding and quit imaging loop");
                break;
            }
            else if (!guidingSuccess)
            {
                LogError($"Skipping target {observation} as starting guiding failed after trying twice");
                Interlocked.Increment(ref _activeObservation);
                continue;
            }

            ImagingLoop(observation, hourAngleAtSlewTime, cancellationToken);
        } // end observation loop
    }

    internal bool StartGuidingLoop(Guider guider, CancellationToken cancellationToken)
    {
        bool guidingSuccess = false;
        int startGuidingTries = 0;
        while (!guidingSuccess && startGuidingTries++ < 2 && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var settlePix = 0.3 + (startGuidingTries * 0.2);
                var settleTime = 15 + (startGuidingTries * 5);
                var settleTimeout = settleTime * SETTLE_TIMEOUT_FACTOR;

                LogInfo($"Start guiding using {guider.Device.DeviceId}, settle pixels: {settlePix}, settle time: {settleTime}s, timeout: {settleTimeout}s");
                guider.Driver.Guide(settlePix, settleTime, settleTimeout);

                var failsafeCounter = 0;
                while (guider.Driver.IsSettling() && failsafeCounter++ < MAX_FAILSAFE && !cancellationToken.IsCancellationRequested)
                {
                    Sleep(TimeSpan.FromSeconds(10));
                }

                guidingSuccess = failsafeCounter < MAX_FAILSAFE && guider.Driver.IsGuiding();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                else if (!guidingSuccess)
                {
                    Sleep(TimeSpan.FromMinutes(startGuidingTries));
                }
            }
            catch (Exception e)
            {
                LogError($"Start guiding try #{startGuidingTries} exception while checking if {guider.Device.DeviceId} is guiding: {e.Message}");
                guidingSuccess = false;
            }
        }

        return guidingSuccess;
    }

    internal void ImagingLoop(in Observation observation, double hourAngleAtSlewTime, CancellationToken cancellationToken)
    {
        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var scopes = Setup.Telescopes.Count;
        var frameNumbers = new int[scopes];
        var subExposuresSec = new int[scopes];

        for (var i = 0; i < scopes; i++)
        {
            var camera = Setup.Telescopes[i].Camera;

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

        var overslept = TimeSpan.Zero;
        var imageWriteQueue = new Queue<(Image image, Observation observation, DateTime expStartTime, int frameNumber)>();
        while (!cancellationToken.IsCancellationRequested
            && mount.Connected
            && Catch(() => mount.Driver.Tracking)
            && guider.Connected
            && Catch(guider.Driver.IsGuiding)
            && IsOnSamePierSide()
            && mount.Driver.TryGetUTCDate(out var expStartTime)
        )
        {
            for (var i = 0; i < scopes; i++)
            {
                var cameraDriver = Setup.Telescopes[i].Camera.Driver;
                switch (cameraDriver.CameraState)
                {
                    case CameraState.Idle:
                        var subExposureSec = subExposuresSec[i];
                        var frameExpTime = TimeSpan.FromSeconds(subExposureSec);
                        cameraDriver.StartExposure(frameExpTime, true);
                        expStartTimes[i] = expStartTime;
                        expTicks[i] = (int)(subExposureSec / tickGCD);
                        var frameNo = ++frameNumbers[i];

                        LogInfo($"Camera #{(i + 1)} {cameraDriver.Name} starting {frameExpTime} exposure of frame #{frameNo}");
                        break;
                }
            }

            var elapsed = WriteQueuedImagesToFitsFiles();
            var tickMinusElapsed = tickSec - elapsed - overslept;
            // clear overslept
            overslept = TimeSpan.Zero;
            if (cancellationToken.IsCancellationRequested)
            {
                LogError("Cancellation rquested, abort image acquisition and quit imaging loop");
                break;
            }
            else if (tickMinusElapsed > TimeSpan.Zero)
            {
                Sleep(tickMinusElapsed);
            }

            var imageFetchSuccess = new bool[scopes];
            for (var i = 0; i < scopes && !cancellationToken.IsCancellationRequested; i++)
            {
                var tick = --expTicks[i];

                var camera = Setup.Telescopes[i].Camera;
                imageFetchSuccess[i] = false;
                if (tick <= 0)
                {
                    var frameNo = frameNumbers[i];
                    var frameExpTime = TimeSpan.FromSeconds(subExposuresSec[i]);
                    do // wait for image loop
                    {
                        if (camera.Driver.ImageReady is true && camera.Driver.Image is { Width: > 0, Height: > 0 } image)
                        {
                            imageFetchSuccess[i] = true;
                            LogInfo($"Camera #{(i + 1)} {camera.Driver.Name} finished {frameExpTime} exposure of frame #{frameNo}");

                            imageWriteQueue.Enqueue((image, observation, expStartTime, frameNo));
                            break;
                        }
                        else
                        {
                            var spinDuration = TimeSpan.FromMilliseconds(100);
                            overslept += spinDuration;
                            Sleep(spinDuration);
                        }
                    }
                    while (overslept < (tickSec / 5)
                        && camera.Driver.CameraState is not CameraState.Error or CameraState.NotConnected
                        && !cancellationToken.IsCancellationRequested
                    );

                    if (!imageFetchSuccess[i])
                    {
                        LogError($"Failed fetching camera #{(i + 1)} {camera.Driver.Name} {frameExpTime} exposure of frame #{frameNo}, camera state: {camera.Driver.CameraState}");
                    }
                }
            }

            if (!IsOnSamePierSide())
            {
                if (observation.AcrossMeridian)
                {
                    // TODO, stop guiding flip, resync, verify and restart guiding
                    throw new InvalidOperationException("Observing across meridian is not yet supported");
                }
            }
            else if (Array.TrueForAll(imageFetchSuccess, x => x))
            {
                var ditherPixel = Configuration.DitherPixel;
                var settlePixel = Configuration.SettlePixel;
                var settleTime = Configuration.SettleTime;
                var settleTimeout = (settleTime * SETTLE_TIMEOUT_FACTOR);

                LogInfo($"Start dithering pixel={ditherPixel} settlePixel={settlePixel} settleTime={settleTime}, timeout={settleTimeout}");

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
                            LogError($"Settling after dithering failed with: {error}");
                        }
                        else
                        {
                            LogInfo($"Settle still in progress: settle pixel={settleProgress.SettlePx} dist={settleProgress.Distance}");
                        }
                    }
                    else
                    {
                        if (settleProgress?.Error is { Length: > 0 } error)
                        {
                            LogError($"Settling after dithering failed with: {error} pixel={settleProgress.SettlePx} dist={settleProgress.Distance}");
                        }
                        else if (settleProgress is not null)
                        {
                            LogInfo($"Settle finished: settle pixel={settleProgress.SettlePx} pixel={settleProgress.SettlePx} dist={settleProgress.Distance}");
                        }
                        break;
                    }    
                }
            }
        } // end imaging loop

        bool IsOnSamePierSide()
        {
            var pierSide = Catch(() => mount.Driver.SideOfPier, PierSide.Unknown);
            var currentHourAngle = Catch(() => mount.Driver.HourAngle, double.NaN);
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
                    WriteImageToFitsFile(imageWrite.image, imageWrite.observation, imageWrite.expStartTime, imageWrite.frameNumber);
                }
                catch (Exception ex)
                {
                    LogError($"Failed to save frame #{imageWrite.frameNumber} taken at {imageWrite.expStartTime:o} by {imageWrite.image.ImageMeta.Instrument} due to error: {ex.Message}");
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
                Sleep(adjustedTime);
            }
            else
            {
                overslept = adjustedTime.Negate();
            }
        }
    }

    internal static bool TryGetTransformOfObservation(Mount mount, in Observation observation, [NotNullWhen(true)] out Transform? transform)
    {
        if (mount.Driver.TryGetUTCDate(out var utc))
        {
            transform = new Transform
            {
                SiteElevation = mount.Driver.SiteElevation,
                SiteLatitude = mount.Driver.SiteLatitude,
                SiteLongitude = mount.Driver.SiteLongitude,
                SitePressure = 1010, // TODO standard atmosphere
                SiteTemperature = 10, // TODO check either online or if compatible devices connected
                DateTimeOffset = new DateTimeOffset(utc, observation.Start.Offset),
                Refraction = true // TODO assumes that driver does not support/do refraction
            };

            return true;
        }

        transform = null;
        return false;
    }

    /// <summary>
    /// Not reentrant if using a shared <paramref name="transform"/>.
    /// </summary>
    /// <param name="mount"></param>
    /// <param name="transform"></param>
    /// <param name="observation"></param>
    /// <param name="raMount"></param>
    /// <param name="decMount"></param>
    /// <returns>true if transform was successful.</returns>
    internal static bool TryTransformJ2000ToMountNative(Mount mount, Transform transform, in Observation observation, out double raMount, out double decMount, out double az, out double alt)
    {
        if (mount.Driver.TryGetUTCDate(out var utc))
        {
            transform.DateTimeOffset = new DateTimeOffset(utc, observation.Start.Offset);
            transform.SetJ2000(observation.Target.RA, observation.Target.Dec);
            transform.Refresh();

            var equSys = mount.Driver.EquatorialSystem;
            (raMount, decMount) = equSys switch
            {
                EquatorialCoordinateType.J2000 => (transform.RAJ2000, transform.DecJ2000),
                EquatorialCoordinateType.Topocentric => (transform.RAApparent, transform.DECApparent),
                _ => (double.NaN, double.NaN)
            };
            az = transform.AzimuthTopocentric;
            alt = transform.ElevationTopocentric;
        }
        else
        {
            raMount = double.NaN;
            decMount = double.NaN;
            az = double.NaN;
            alt = double.NaN;
        }

        return !double.IsNaN(raMount) && !double.IsNaN(decMount) && !double.IsNaN(az) && !double.IsNaN(alt);
    }

    internal void OpenTelescopeCovers()
    {
        var covers =
            from telescope in Setup.Telescopes
            let cover = telescope.Cover
            where cover is not null
            select cover;

        foreach (var cover in covers)
        {
            cover.Connected = true;

            cover.Driver.Brightness = 0;
            cover.Driver.Open();
        }
    }

    /// <summary>
    /// Idea is that we keep cooler on but only on the currently reached temperature, so we have less cases to manage in the imaging loop.
    /// Assumes that power is switched on.
    /// </summary>
    internal void CoolCamerasToSensorTemp()
    {
        for (var i = 0; i < Setup.Telescopes.Count; i++)
        {
            var camera = Setup.Telescopes[i].Camera;
            if (camera.Connected
                && camera.Driver.CanGetCoolerOn
                && camera.Driver.CanSetCCDTemperature
                && camera.Driver.CCDTemperature is double ccdTemp and >= -40 and <= 50
                && camera.Driver.HeatSinkTemperature is double heatSinkTemp and >= -40 and <= 50)
            {
                camera.Driver.CoolerOn = true;
                var actualSetpointTemp = camera.Driver.SetCCDTemperature = Math.Truncate(Math.Min(ccdTemp, heatSinkTemp));
                var coolerPower = camera.Driver.CanGetCoolerPower ? camera.Driver.CoolerPower : double.NaN;

                LogInfo($"Camera #{(i + 1)} setpoint temperature set to {actualSetpointTemp:0.00} °C, Heatsink {heatSinkTemp:0.00} °C, Power={coolerPower:0.00}%");
            }
        }
    }

    /// <summary>
    /// Assumes that power is on (c.f. <see cref="CoolCamerasToSensorTemp"/>).
    /// </summary>
    /// <param name="maybeSetpointTemp">Desired degrees Celcius setpoint temperature, if null then ambient temp is chosen</param>
    /// <param name="rampInterval">interval to wait until further adjusting setpoint.</param>
    internal bool CoolCamerasToSetpoint(int? maybeSetpointTemp, TimeSpan rampInterval, double thresPower, CoolDirection direction, CancellationToken cancellationToken = default)
    {
        var count = Setup.Telescopes.Count;
        var isRamping = new bool[count];
        var thresholdReachedConsecutiveCounts = new int[count];

        var accSleep = TimeSpan.Zero;
        do
        {
            for (var i = 0; i < Setup.Telescopes.Count; i++)
            {
                var camera = Setup.Telescopes[i].Camera;
                if (camera.Connected
                    && camera.Driver.CanSetCCDTemperature
                    && camera.Driver.CanGetCoolerOn
                    && camera.Driver.CoolerOn
                    && camera.Driver.CCDTemperature is double ccdTemp
                    && !double.IsNaN(ccdTemp)
                    && camera.Driver.HeatSinkTemperature is double heatSinkTemp
                    && !double.IsNaN(heatSinkTemp)
                )
                {
                    var coolerPower = camera.Driver.CanGetCoolerPower ? camera.Driver.CoolerPower : double.NaN;
                    var setpointTemp = maybeSetpointTemp ?? (!double.IsNaN(heatSinkTemp) ? Math.Truncate(heatSinkTemp) : direction.DefaultSetpointTemp());

                    if (direction.NeedsFurtherRamping(ccdTemp, setpointTemp)
                        && (double.IsNaN(coolerPower) || !direction.ThresholdPowerReached(coolerPower, thresPower))
                    )
                    {
                        var actualSetpointTemp = camera.Driver.SetCCDTemperature = direction.SetpointTemp(ccdTemp, setpointTemp);
                        isRamping[i] = true;
                        thresholdReachedConsecutiveCounts[i] = 0;

                        LogInfo($"Camera {(i + 1)} setpoint temperature {setpointTemp:0.00} °C not yet reached, " +
                            $"cooling {direction.ToString().ToLowerInvariant()} stepwise, currently at {actualSetpointTemp:0.00} °C. " +
                            $" Heatsink={heatSinkTemp:0.00} °C, CCD={ccdTemp:0.00} °C, Power={coolerPower:0.00}%");
                    }
                    else if (++thresholdReachedConsecutiveCounts[i] < 2)
                    {
                        isRamping[i] = true;
                        
                        LogInfo($"Camera {(i + 1)} setpoint temperature {setpointTemp:0.00} °C or {thresPower:0.00} % power reached. Heatsink={heatSinkTemp:0.00} °C, CCD={ccdTemp:0.00} °C, Power={coolerPower:0.00}%");
                    }
                    else
                    {
                        isRamping[i] = false;

                        LogInfo($"Camera {(i + 1)} setpoint temperature {setpointTemp:0.00} °C or {thresPower:0.00} % power reached twice in a row. Heatsink={heatSinkTemp:0.00} °C, CCD={ccdTemp:0.00} °C, Power={coolerPower:0.00}%");
                    }
                }
                else
                {
                    isRamping[i] = false;

                    var coolerOnOff = Catch(() => camera.Driver.CanGetCoolerOn && camera.Driver.CoolerOn);
                    LogInfo($"Skipping camera {(i + 1)} setpoint temperature {maybeSetpointTemp?.ToString("0.00") + " °C" ?? "ambient"} as we cannot get the current CCD temperature or cooling is not supported. Power is {(coolerOnOff ? "on" : "off")}");
                }
            }

            accSleep += rampInterval;
            if (cancellationToken.IsCancellationRequested)
            {
                LogInfo("Cancellation requested, quiting cooldown loop");
                break;
            }
            else
            {
                Sleep(rampInterval);
            }
        } while (isRamping.Any(p => p) && accSleep < rampInterval * 100 && !cancellationToken.IsCancellationRequested);

        return !isRamping.Any(p => p);
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

    internal void WriteImageToFitsFile(Image image, in Observation observation, DateTime subExpStartTime, int frameNumber)
    {
        var target = observation.Target;
        var outputFolder = _external.OutputFolder;
        var targetFolder = GetSafeFileName(target.Name);
        // TODO make order of target/date configurable
        var dateFolder = GetSafeFileName(subExpStartTime.ToString("yyyy-MM-dd", DateTimeFormatInfo.InvariantInfo));
        var frameFolder = Directory.CreateDirectory(Path.Combine(outputFolder, targetFolder, dateFolder)).FullName;
        var fitsFileName = GetSafeFileName($"frame_{subExpStartTime:o}_{frameNumber}.fits");

        LogInfo($"Writing FITS file {targetFolder}/{fitsFileName}");
        image.WriteToFitsFile(Path.Combine(frameFolder, fitsFileName));
    }

    internal T Catch<T>(Func<T> func, T @default = default)
        where T : struct
    {
        try
        {
            return func();
        }
        catch (Exception ex)
        {
            LogError($"Error {ex.Message} while executing: {func.Method.Name}");
            return @default;
        }
    }
}
