using Astap.Lib.Astrometry;
using Astap.Lib.Astrometry.SOFA;
using Astap.Lib.Devices;
using Astap.Lib.Imaging;
using System;
using System.Collections.Generic;
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
    {
        _external = external;
        Setup = setup;
        _observations = ConcatToReadOnlyList(observation, observations);
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

        try
        {

            var active = Interlocked.Increment(ref _activeObservation);
            // run initialisation code
            if (active == 0)
            {
                guider.Connected = true;

                for (var i = 0; i < Setup.Telescopes.Count; i++)
                {
                    var camera = Setup.Telescopes[i].Camera;
                    camera.Connected = true;
                }
            }
            else if (active >= _observations.Count)
            {
                return;
            }

            Setup.Guider.Driver.ConnectEquipment();

            Observation? observation;
            while ((observation = CurrentObservation) is not null)
            {
                var frameNumber = 0;
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
                            Sleep(TimeSpan.FromSeconds(1));
                        }

                        guidingSuccess = failsafeCounter < MAX_FAILSAFE && guider.Driver.IsGuiding();
                    }
                    catch (Exception e)
                    {
                        LogError($"Start guiding try #{startGuidingTries} exception while checking if {guider.Device} is guiding: {e.Message}");
                        guidingSuccess = false;
                    }
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
                    Interlocked.Increment(ref _activeObservation);
                    break;
                }

                camera.Driver.StartExposure(expDuration, true);

                bool isImageReady = WaitForImageReady(camera, expDuration, Sleep);

                if (!isImageReady)
                {
                    break;
                }

                var image = camera.Driver.Image;

                if (image is not null)
                {
                    WriteImageToFitsFile(image, observation, expStartTime, frameNumber);

                    frameNumber++;
                }
            } // end exposure loop
        }
        catch (Exception e)
        {
            errorLogFunc($"Unrecoverable error {e.Message} occured, aborting session");
        }
    }

    public void OpenTelescopeCovers()
    {
        foreach (var telescope in Setup.Telescopes)
        {
            if (telescope.Cover is Cover cover)
            {
                cover.Connected = true;

                cover.Driver.Brightness = 0;
                cover.Driver.Open();
            }
        }
    }

    public void TurnOnCooledCameras()
    {

    }

    public void TurnOffCooledCameras()
    {

    }

    internal static string GetSafeFileName(string name, char replace = '_')
    {
        char[] invalids = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalids.Contains(c) ? replace : c).ToArray());
    }

    internal void WriteImageToFitsFile(Image image, in Target target, DateTime expStartTime, int frameNumber)
    {
        var outputFolder = _external.OutputFolder;
        var targetFolder = GetSafeFileName(target.Name);
        var frameFolder = Directory.CreateDirectory(Path.Combine(outputFolder, targetFolder)).FullName;
        var fitsFileName = GetSafeFileName($"frame_{expStartTime:o}_{frameNumber}.fits");

        LogInfo($"Writing FITS file {targetFolder}/{fitsFileName}");
        image.WriteToFitsFile(Path.Combine(frameFolder, fitsFileName));
    }

    internal bool WaitForImageReady(Camera camera, TimeSpan expDuration)
    {
        Sleep(expDuration);

        bool isImageReady = false;
        for (var i = 0; i < 10; i++)
        {
            if (isImageReady = camera.Driver.ImageReady is true)
            {
                break;
            }
            else
            {
                Sleep(TimeSpan.FromMilliseconds(100 + i * 100));
            }
        }

        return isImageReady;
    }
}
