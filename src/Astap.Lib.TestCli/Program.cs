using Astap.Lib.Devices.Ascom;
using Astap.Lib.Devices;
using Astap.Lib.Devices.Guider;
using Astap.Lib.Sequencing;


var cts = new CancellationTokenSource();
Console.CancelKeyPress += Console_CancelKeyPress;

void Console_CancelKeyPress(object? sender, ConsoleCancelEventArgs e)
{
    e.Cancel = true;
    cts.Cancel();
}

const string Camera = nameof(Camera);

var argIdx = 0;
var mountDeviceId = args.Length > 0 ? args[argIdx++] : "ASCOM.DeviceHub.Telescope";
var cameraDeviceId = args.Length > 1 ? args[argIdx++] : "ASCOM.Simulator.Camera";
var expDuration = TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[argIdx++]) : 10);
var outputFolder = Directory.CreateDirectory(args.Length > 3 ? args[argIdx++] : "C:/Temp/Astap.Lib.TestCli").FullName;
var external = new ConsoleOutput(outputFolder);

var observations = new List<Observation>
{
    new Observation(new Target(85.205782 / 15, -2.5, "Horsehead Nebula"), DateTimeOffset.Now, TimeSpan.FromMinutes(100), false, TimeSpan.FromSeconds(20))
};

var guiderDevice = new GuiderDevice("PHD2", "localhost/1", "");

using var profile = new AscomProfile();
var allCameras = profile.RegisteredDevices(DeviceBase.CameraType);
var cameraDevice = allCameras.FirstOrDefault(e => e.DeviceId == cameraDeviceId);

var allMounts = profile.RegisteredDevices(DeviceBase.TelescopeType);
var mountDevice = allMounts.FirstOrDefault(e => e.DeviceId == mountDeviceId);

Mount mount;
if (mountDevice is not null)
{
    mount = new Mount(mountDevice)
    {
        Connected = true
    };

    external.LogInfo($"Connected to mount {mountDevice}");
}
else
{
    external.LogError($"Could not connect to mount {mountDevice?.ToString()}");
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
        external.LogError($"Connected to {guider.Device.DeviceType} guider profile {activeProfileName}");
    }
    else
    {
        external.LogError($"Connected to {guider.Device.DeviceType} guider at {guider.Device.DeviceId}");
    }
}
else
{
    external.LogError("Could not connect to guider");
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

var setup = new Setup(mount, guider, new Telescope("Sim Scope", 250, camera, null, null, null, null));

var session = new Session(setup, external, observations);

session.Run(cts.Token);

return 0;

class ConsoleOutput : IExternal
{
    public ConsoleOutput(string outputFolder)
    {
        OutputFolder=outputFolder;
    }

    public string OutputFolder { get; init; }

    public void LogError(string error) => Console.Error.WriteLine(error);

    public void LogInfo(string info) => Console.WriteLine(info);

    public void Sleep(TimeSpan duration) => Thread.Sleep(duration);
}