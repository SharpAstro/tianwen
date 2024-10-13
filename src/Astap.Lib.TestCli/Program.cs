using Astap.Lib.Astrometry.Catalogs;
using Astap.Lib.Astrometry.Focus;
using Astap.Lib.Astrometry.PlateSolve;
using Astap.Lib.Devices;
using Astap.Lib.Devices.Ascom;
using Astap.Lib.Devices.Guider;
using Astap.Lib.Sequencing;
using Pastel;
using ZWOptical.SDK;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += Console_CancelKeyPress;

void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
{
    e.Cancel = true;
    cts.Cancel();
}

var argIdx = 0;
var mountDeviceId = args.Length > argIdx ? args[argIdx++] : "ASCOM.DeviceHub.Telescope";
var cameraDeviceId = args.Length > argIdx ? args[argIdx++] : "ASCOM.Simulator.Camera";
var coverDeviceId = args.Length > argIdx ? args[argIdx++] : "ASCOM.Simulator.CoverCalibrator";
var focuserDeviceId = args.Length > argIdx ? args[argIdx++] : "ASCOM.Simulator.Focuser";
var expDuration = TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[argIdx++]) : 10);
var outputFolder = Directory.CreateDirectory(args.Length > 3 ? args[argIdx++] : Path.Combine(Directory.GetCurrentDirectory(), "Astap.Lib.TestCli", "Light")).FullName;
var external = new ConsoleOutput(outputFolder);


external.LogInfo($"EAF SDK Version: {EAFFocuser1_6.EAFGetSDKVersion()}");
external.LogInfo($"EFW SDK Version: {EFW1_7.EFWGetSDKVersion()}");
external.LogInfo($"ASI SDK Version: {ASICamera2.ASIGetSDKVersion()}");

var observations = new List<Observation>
{
    // new Observation(new Target(19.5, -20, "Mercury", CatalogIndex.Mercury), DateTimeOffset.Now, TimeSpan.FromMinutes(100), false, TimeSpan.FromSeconds(20)),
    new Observation(new Target(5.5877777777777773, -5.389444444444444, "Orion Nebula", CatalogUtils.TryGetCleanedUpCatalogName("M42", out var catIdx) ? catIdx : null), DateTimeOffset.Now, TimeSpan.FromMinutes(100), false, TimeSpan.FromSeconds(20))
};

var guiderDevice = new GuiderDevice(DeviceType.PHD2, "localhost/1", "");

using var profile = new AscomProfile();
var allCameras = profile.RegisteredDevices(DeviceType.Camera);
var cameraDevice = allCameras.FirstOrDefault(e => e.DeviceId == cameraDeviceId);

var allMounts = profile.RegisteredDevices(DeviceType.Telescope);
var mountDevice = allMounts.FirstOrDefault(e => e.DeviceId == mountDeviceId);

var allCovers = profile.RegisteredDevices(DeviceType.CoverCalibrator);
var coverDevice = allCovers.FirstOrDefault(e => e.DeviceId == coverDeviceId);

var allFocusers = profile.RegisteredDevices(DeviceType.Focuser);
var focuserDevice = allFocusers.FirstOrDefault(e => e.DeviceId == focuserDeviceId);

Mount mount;
if (mountDevice is not null)
{
    mount = new Mount(mountDevice);
    external.LogInfo($"Found mount {mountDevice.DisplayName}, using {mount.Driver.DriverInfo ?? mount.Driver.GetType().Name}");
}
else
{
    external.LogError($"Could not connect to mount {mountDevice?.ToString()}");
    return 1;
}

Guider guider;
if (guiderDevice is not null)
{
    guider = new Guider(guiderDevice);

    if (guider.Driver.TryGetActiveProfileName(out var activeProfileName))
    {
        external.LogInfo($"Connected to {guider.Device.DeviceType} guider profile {activeProfileName}");
    }
    else
    {
        external.LogInfo($"Connected to {guider.Device.DeviceType} guider at {guider.Device.DeviceId}");
    }
}
else
{
    external.LogError("No guider was specified, aborting.");
    return 1;
}

Camera camera;
if (cameraDevice is not null)
{
    camera = new Camera(cameraDevice);
}
else
{
    external.LogError("Could not connect to camera");
    return 1;
}

Cover? cover;
if (coverDevice is not null)
{
    cover = new Cover(coverDevice);
}
else
{
    cover = null;
}

Focuser? focuser;
if (focuserDevice is not null)
{
    focuser = new Focuser(focuserDevice);
}
else
{
    focuser = null;
}

using var setup = new Setup(
    mount,
    guider,
    new GuiderFocuser(),
    new Telescope("Sim Scope", 250, camera, cover, focuser, new FocusDirection(false, true), null, null)
);

var sessionConfiguration = new SessionConfiguration(
    SetpointCCDTemperature: new SetpointTemp(10, SetpointTempKind.Normal),
    CooldownRampInterval: TimeSpan.FromSeconds(20),
    CoolupRampInterval: TimeSpan.FromSeconds(30),
    MinHeightAboveHorizon: 15,
    DitherPixel: 30d,
    SettlePixel: 0.3d,
    DitherEveryNthFrame: 3,
    SettleTime: TimeSpan.FromSeconds(30),
    GuidingTries: 3
);

// TODO: implement DI
var analyser = new ImageAnalyser();
var plateSolver = new CombinedPlateSolver(new AstapPlateSolver(), new AstrometryNetPlateSolverMultiPlatform(), new AstrometryNetPlateSolverUnix());
if (!await plateSolver.CheckSupportAsync(cts.Token))
{
    external.LogError("No proper plate solver configured, aborting!");
}

var session = new Session(setup, sessionConfiguration, analyser, plateSolver, external, observations);

session.Run(cts.Token);

return 0;

class ConsoleOutput(string outputFolder) : IExternal
{
    public TimeProvider TimeProvider => TimeProvider.System;

    public string OutputFolder { get; } = outputFolder;

    public void LogError(string error) => Console.Error.WriteLine($"[{DateTime.Now:o}] {error.Pastel(ConsoleColor.Red)}");

    public void LogWarning(string warning) => Console.WriteLine($"[{DateTime.Now:o}] {warning.Pastel(ConsoleColor.Yellow)}");

    public void LogInfo(string info) => Console.WriteLine($"[{DateTime.Now:o}] {info.Pastel(ConsoleColor.White)}");

    public void LogException(Exception exception, string extra) => LogError($"{exception.Message} {extra}");

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}