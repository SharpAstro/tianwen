using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.SOFA;
using Astap.Lib.Devices;
using Astap.Lib.Imaging;
using Astap.Lib.Stat;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using static Astap.Lib.CollectionHelper;

namespace Astap.Lib.Sequencing;

public class Session
{
    private readonly IExternal _external;
    private readonly IReadOnlyList<Observation> _observations;
    private int _activeObservation;

    public Session(
        Setup setup,
        IExternal external,
        Observation observation,
        params Observation[] observations
    )
        : this(setup, external, ConcatToReadOnlyList(observation, observations))
    {
        // calls below
    }

    public Session(
        Setup setup,
        IExternal external,
        IReadOnlyList<Observation> observations
    )
    {
        _external = external;
        Setup = setup;
        _observations = observations.Count > 0 ? observations : throw new ArgumentException("Need at least one observation", nameof(observations));
        _activeObservation = -1; // -1 means we have not started imaging yet
    }

    public Setup Setup { get; }

    public Observation? CurrentObservation => _activeObservation is int active and >= 0 && active < _observations.Count ? _observations[active] : null;

    void Sleep(TimeSpan duration) => _external.Sleep(duration);
    void LogInfo(string info) => _external.LogInfo(info);
    void LogError(string info) => _external.LogError(info);

    public void Run()
    {
        const int MAX_FAILSAFE = 1000;

        var guider = Setup.Guider;
        var mount = Setup.Mount;
        var scopes = Setup.Telescopes.Count;

        try
        {
            var active = Interlocked.Increment(ref _activeObservation);
            // run initialisation code
            if (active == 0)
            {
                guider.Connected = true;

                for (var i = 0; i < scopes; i++)
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
                CoolDownCameras();
            }
            else if (active >= _observations.Count)
            {
                return;
            }

            Setup.Guider.Driver.ConnectEquipment();

            Observation? observation;
            while ((observation = CurrentObservation) is not null)
            {
                var frameNumbers = new int[scopes];
                if (mount.Driver.CanSetTracking)
                {
                    mount.Driver.TrackingSpeed = TrackingSpeed.Sidereal; // TODO: Support different tracking speed
                    mount.Driver.Tracking = true;
                }

                LogInfo($"Stop guiding to start slewing mount to target {observation}");
                guider.Driver.StopCapture();

                Transform transform;
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
                    transform.SetJ2000(observation.Target.RA, observation.Target.Dec);
                }
                else
                {
                    LogError($"Could not determine UTC time from mount {mount.Device}, skipping target {observation}");

                    continue;
                }

                var equSys = mount.Driver.EquatorialSystem;
                var (raMount, decMount) = equSys switch
                {
                    EquatorialCoordinateType.J2000 => (transform.RAJ2000, transform.DecJ2000),
                    EquatorialCoordinateType.Topocentric => (transform.RAApparent, transform.DECApparent),
                    _ => throw new InvalidOperationException($"{equSys} is not supported!")
                };

                // skip target if slew is not successful
                if (!mount.Driver.SlewAsync(raMount, decMount))
                {
                    LogError($"Failed to slew to target {observation}, skipping.");

                    continue;
                }

                int failsafeCounter = 0;
                try
                {
                    while (mount.Driver.IsSlewing && failsafeCounter++ < MAX_FAILSAFE)
                    {
                        Sleep(TimeSpan.FromSeconds(1));
                    }

                    if (mount.Driver.IsSlewing || failsafeCounter == MAX_FAILSAFE)
                    {
                        LogError($"Failsafe activated when slewing to {observation}, skipping.");
                        Interlocked.Increment(ref _activeObservation);
                        continue;
                    }
                }
                catch (Exception e)
                {
                    LogError($"Skipping target as slewing to target {observation} failed: {e.Message}");
                    Interlocked.Increment(ref _activeObservation);
                    continue;
                }

                LogInfo($"Finished slewing mount to target {observation}");

                bool guidingSuccess = false;
                int startGuidingTries = 0;
                while (!guidingSuccess && startGuidingTries++ < 2)
                {
                    try
                    {
                        var settlePix = 0.3 + (startGuidingTries * 0.2);
                        var settleTime = 15 + (startGuidingTries * 5);
                        var settleTimeout = 50 + (startGuidingTries * 10);

                        LogInfo($"Start guiding start guiding using {guider.Device}, settle pixels: {settlePix}, settle time: {settleTime}s, timeout: {settleTimeout}s");
                        guider.Driver.Guide(settlePix, settleTime, settleTimeout);

                        failsafeCounter = 0;
                        while (guider.Driver.IsSettling() && failsafeCounter++ < MAX_FAILSAFE)
                        {
                            Sleep(TimeSpan.FromSeconds(10));
                        }

                        guidingSuccess = failsafeCounter < MAX_FAILSAFE && guider.Driver.IsGuiding();
                    }
                    catch (Exception e)
                    {
                        LogError($"Start guiding try #{startGuidingTries} exception while checking if {guider.Device} is guiding: {e.Message}");
                        guidingSuccess = false;
                    }

                    Sleep(TimeSpan.FromMinutes(5) + (startGuidingTries * TimeSpan.FromMinutes(3)));
                }

                if (!guidingSuccess)
                {
                    LogError($"Skipping target {observation} as starting guiding failed after trying twice");
                    Interlocked.Increment(ref _activeObservation);
                    continue;
                }

                if (!mount.Driver.TryGetUTCDate(out var expStartTime))
                {
                    LogError($"Failed to connect to mount {mount.Device}, aborting.");
                    break;
                }

                var subExposuresInSeconds = new int[scopes];
                for (var i = 0; i < scopes; i++)
                {
                    var camera = Setup.Telescopes[i].Camera;

                    // TODO per camera exposure calculation, i.e. via f/ratio
                    var subExposure = observation.SubExposure;
                    subExposuresInSeconds[i] = (int)Math.Ceiling(subExposure.TotalSeconds);
                    camera.Driver.StartExposure(subExposure, true);
                }

                var maxSubExposureSec = subExposuresInSeconds.Max();
                var tickGCD = StatisticsHelper.GCD(subExposuresInSeconds);
                var tickSec = TimeSpan.FromSeconds(tickGCD);
                var ticksPerMaxSubExposure = maxSubExposureSec / tickGCD;

                var imagesReady = new bool[scopes];
                int tick = 0;
                do
                {
                    for (var i = 0; i < scopes; i++)
                    {
                        var camera = Setup.Telescopes[i].Camera;
                        // wait 5 seconds if we are already over the expected max exposure time
                        Sleep(tick < ticksPerMaxSubExposure ? tickSec : TimeSpan.FromSeconds(5));

                        if (!imagesReady[i] && camera.Driver.ImageReady is true && camera.Driver.Image is { Width: > 0, Height: > 0 } image)
                        {
                            imagesReady[i] = true;
                            WriteImageToFitsFile(image, observation, expStartTime, frameNumbers[i]++);
                        }
                    }
                }
                while (!imagesReady.All(x => x) && tick++ < MAX_FAILSAFE); // TODO ensure HA sign is not reversed
            } // end observation loop
        }
        catch (Exception e)
        {
            LogError($"Unrecoverable error {e.Message} occured, aborting session");
        }
        finally
        {
            CoolUpCameras();
        }
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

    internal void CoolDownCameras()
    {
        for (var i = 0; i < Setup.Telescopes.Count; i++)
        {
            var camera = Setup.Telescopes[i].Camera;
        }
    }

    internal void CoolUpCameras()
    {
        for (var i = 0; i < Setup.Telescopes.Count; i++)
        {
            var camera = Setup.Telescopes[i].Camera;
        }
    }

    internal static string GetSafeFileName(string name, char replace = '_')
    {
        char[] invalids = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalids.Contains(c) ? replace : c).ToArray());
    }

    internal void WriteImageToFitsFile(Image image, in Observation observation, DateTime subExpStartTime, int frameNumber)
    {
        var target = observation.Target;
        var outputFolder = _external.OutputFolder;
        var targetFolder = GetSafeFileName(target.Name);
        var frameFolder = Directory.CreateDirectory(Path.Combine(outputFolder, targetFolder)).FullName;
        var fitsFileName = GetSafeFileName($"frame_{subExpStartTime:o}_{frameNumber}.fits");

        LogInfo($"Writing FITS file {targetFolder}/{fitsFileName}");
        image.WriteToFitsFile(Path.Combine(frameFolder, fitsFileName));
    }
}
