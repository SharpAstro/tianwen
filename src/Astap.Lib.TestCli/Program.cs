using Astap.Lib.Devices.Ascom;
using Astap.Lib.Devices;
using Astap.Lib.Devices.Guider;
using Astap.Lib.Sequencing;
using static Astap.Lib.Astrometry.CoordinateUtils;
using Astap.Lib.Imaging;

const string Camera = nameof(Camera);

var argIdx = 0;
var mountDeviceId = args.Length > 0 ? args[argIdx++] : "ASCOM.DeviceHub.Telescope";
var cameraDeviceId = args.Length > 1 ? args[argIdx++] : "ASCOM.Simulator.Camera";
var expDuration = TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[argIdx++]) : 10);
var outputFolder = Directory.CreateDirectory(args.Length > 3 ? args[argIdx++] : "C:/Temp/AstroPics").FullName;

var targets = new List<Target>();

for (var i = argIdx; i < args.Length; i++)
{

}

if (targets.Count == 0)
{
    targets.Add(new Target(85.205782 / 15, -2.5, "Horsehead Nebula"));
}

var currentTarget = 0;

var guiderDevice = new GuiderDevice("PHD2", "localhost/1", "");

using var profile = new AscomProfile();
var allCameras = profile.RegisteredDevices(DeviceBase.CameraType);
var cameraDevice = allCameras.FirstOrDefault(e => e.DeviceId == cameraDeviceId);

var allMounts = profile.RegisteredDevices(DeviceBase.TelescopeType);
var mountDevice = allMounts.FirstOrDefault(e => e.DeviceId == mountDeviceId);

return SimpleImagingSession(mountDevice, cameraDevice, guiderDevice, targets, Thread.Sleep, Console.WriteLine, Console.Error.WriteLine);

int SimpleImagingSession(DeviceBase? mouuntDevice, DeviceBase? cameraDevice, GuiderDevice guiderDevice, IReadOnlyList<Target> targets, Action<TimeSpan> sleepFunc, Action<string> infoLogFunc, Action<string> errorLogFunc)
{
    const int MAX_FAILSAFE = 1000;

    Mount mount;
    if (mountDevice is not null)
    {
        mount = new Mount(mountDevice)
        {
            Connected = true
        };

        errorLogFunc($"Connected to mount {mountDevice}");
    }
    else
    {
        errorLogFunc($"Could not connect to mount {mountDevice?.ToString()}");
        return 1;
    }

    Guider guider;
    if (guiderDevice is not null)
    {
        guider = new Guider(guiderDevice)
        {
            Connected = true
        };

        if (guider.Driver.TryGetActiveProfileName(out var activeProfileName))
        {
            errorLogFunc($"Connected to {guider.Device.DeviceType} guider profile {activeProfileName}");
        }
        else
        {
            errorLogFunc($"Connected to {guider.Device.DeviceType} guider at {guider.Device.DeviceId}");
        }
    }
    else
    {
        errorLogFunc($"Could not connect to guider {guiderDevice?.ToString()}");
        return 1;
    }

    if (cameraDevice is not null)
    {
        using (guider)
        using (mount)
        using (var camera = new Camera(cameraDevice))
        {
            try
            {
                guider.Driver.ConnectEquipment();
                camera.Connected = true;

                while (currentTarget < targets.Count)
                {
                    var frameNumber = 0;
                    if (mount.Driver.CanSetTracking)
                    {
                        mount.Driver.TrackingSpeed = TrackingSpeed.Sidereal; // TODO: Support different tracking speed
                        mount.Driver.Tracking = true;
                    }
                    var target = targets[currentTarget];

                    infoLogFunc($"Stop guiding to start slewing mount to target {target}");
                    guider.Driver.StopCapture();
                    // skip target if slew is not successful
                    if (!mount.Driver.SlewAsync(target.RA, target.Dec))
                    {
                        errorLogFunc($"Failed to slew to target {target}, skipping.");
                        currentTarget++;
                        continue;
                    }

                    int failsafeCounter = 0;
                    try
                    {
                        while (mount.Driver.IsSlewing && failsafeCounter++ < MAX_FAILSAFE)
                        {
                            sleepFunc(TimeSpan.FromSeconds(1));
                        }

                        if (mount.Driver.IsSlewing || failsafeCounter == MAX_FAILSAFE)
                        {
                            errorLogFunc($"Failsafe activated when slewing to {target}, skipping.");
                            currentTarget++;
                            continue;
                        }
                    }
                    catch (Exception e)
                    {
                        errorLogFunc($"Skipping target as slewing to target {target} failed: {e.Message}");
                        currentTarget++;
                        continue;
                    }

                    infoLogFunc($"Finished slewing mount to target {target}");

                    bool guidingSuccess = false;
                    int startGuidingTries = 0;
                    while (!guidingSuccess && startGuidingTries++ < 2)
                    {
                        try
                        {
                            var settlePix = 0.3 + (startGuidingTries * 0.2);
                            var settleTime = 15 + (startGuidingTries * 5);
                            var settleTimeout = 50 + (startGuidingTries * 10);

                            infoLogFunc($"Start guiding start guiding using {guider.Device}, settle pixels: {settlePix}, settle time: {settleTime}s, timeout: {settleTimeout}s");
                            guider.Driver.Guide(settlePix, settleTime, settleTimeout);

                            failsafeCounter = 0;
                            while (guider.Driver.IsSettling() && failsafeCounter++ < MAX_FAILSAFE)
                            {
                                sleepFunc(TimeSpan.FromSeconds(1));
                            }

                            guidingSuccess = failsafeCounter < MAX_FAILSAFE && guider.Driver.IsGuiding();
                        }
                        catch (Exception e)
                        {
                            errorLogFunc($"Start guiding try #{startGuidingTries} exception while checking if {guider.Device} is guiding: {e.Message}");
                            guidingSuccess = false;
                        }
                    }

                    if (!guidingSuccess)
                    {
                        errorLogFunc($"Skipping target {target} as starting guiding failed after trying twice");
                        currentTarget++;
                        continue;
                    }

                    DateTime expStartTime;
                    if (mount.Connected is true && mount.Driver.UTCDate is DateTime utc)
                    {
                        expStartTime = utc;
                    }
                    else
                    {
                        errorLogFunc($"Failed to connect to mount {mount.Device}, aborting.");
                        currentTarget++;
                        break;
                    }

                    camera.Driver.StartExposure(expDuration, true);

                    bool isImageReady = WaitForImageReady(camera, expDuration, sleepFunc);

                    if (!isImageReady)
                    {
                        break;
                    }

                    var image = camera.Driver.Image;

                    if (image is not null)
                    {
                        WriteImageToFitsFile(image, target, expStartTime, outputFolder, frameNumber, infoLogFunc);

                        frameNumber++;
                    }
                } // end exposure loop
            }
            catch (Exception e)
            {
                errorLogFunc($"Unrecoverable error {e.Message} occured, aborting session");
            }
        } // end using drivers

        return 0;
    }
    else
    {
        guider.Dispose();
        errorLogFunc($"Failed to instantiate camera {cameraDeviceId}");
        return 1;
    }
}

static string GetSafeFileName(string name, char replace = '_')
{
    char[] invalids = Path.GetInvalidFileNameChars();
    return new string(name.Select(c => invalids.Contains(c) ? replace : c).ToArray());
}

static void WriteImageToFitsFile(Image image, in Target target, DateTime expStartTime, string outputFolder, int frameNumber, Action<string> infoLogFunc)
{
    var targetFolder = GetSafeFileName(target.Name);
    var frameFolder = Directory.CreateDirectory(Path.Combine(outputFolder, targetFolder)).FullName;
    var fitsFileName = GetSafeFileName($"frame_{expStartTime:o}_{frameNumber}.fits");

    infoLogFunc($"Writing FITS file {targetFolder}/{fitsFileName}");
    image.WriteToFitsFile(Path.Combine(frameFolder, fitsFileName));
}

static bool WaitForImageReady(Camera camera, TimeSpan expDuration, Action<TimeSpan> sleepFunc)
{
    sleepFunc(expDuration);

    bool isImageReady = false;
    for (var i = 0; i < 10; i++)
    {
        if (isImageReady = camera.Driver.ImageReady is true)
        {
            break;
        }
        else
        {
            sleepFunc(TimeSpan.FromMilliseconds(100 + i * 100));
        }
    }

    return isImageReady;
}