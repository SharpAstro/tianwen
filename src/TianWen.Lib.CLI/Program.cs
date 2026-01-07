using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pastel;
using System.CommandLine;
using System.Text;
using TianWen.Lib.CLI;
using TianWen.Lib.Extensions;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

Pastel.ConsoleExtensions.Enable();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
builder.Services
    .AddLogging(static builder => builder.AddSimpleConsole(
        static options =>
        {
            options.IncludeScopes = false;
            options.SingleLine = false;
        })
    )
    .AddExternal()
    .AddAstrometry()
    .AddZWO()
    .AddAscom()
    .AddMeade()
    .AddProfiles()
    .AddFake()
    .AddPHD2()
    .AddDevices()
    .AddSessionFactory()
    .AddSingleton<IConsoleHost, ConsoleHost>();

#if DEBUG
builder.Logging.SetMinimumLevel(LogLevel.Debug);
#else
builder.Logging.SetMinimumLevel(LogLevel.Warning);
#endif

using IHost host = builder.Build();

await host.StartAsync();

var services = host.Services;
var consoleHost = services.GetRequiredService<IConsoleHost>();

var selectedProfileOption = new Option<string?>("--active", "-a")
{
    Description = "Profile name or ID to use",
    Recursive = true
};

var rootCommand = new RootCommand
{
    Options = { selectedProfileOption },
    Subcommands =
    {
        new ProfileSubCommand(consoleHost, selectedProfileOption).Build()
    }
};

var parsedResult = rootCommand.Parse(args);
if (parsedResult.Errors.Count is 0)
{
    await parsedResult.InvokeAsync(cancellationToken: consoleHost.ApplicationLifetime.ApplicationStopped);
}
else
{
    foreach (var error in parsedResult.Errors)
    {
        Console.Error.WriteLine(error.Message.Pastel(ConsoleColor.Red));
    }
}

await host.StopAsync();

await host.WaitForShutdownAsync();
/*
var argIdx = 0;
var mountDeviceId = args.Length > argIdx ? args[argIdx++] : "ASCOM.DeviceHub.Telescope";
var cameraDeviceId = args.Length > argIdx ? args[argIdx++] : "ASCOM.Simulator.Camera";
var coverDeviceId = args.Length > argIdx ? args[argIdx++] : "ASCOM.Simulator.CoverCalibrator";
var focuserDeviceId = args.Length > argIdx ? args[argIdx++] : "ASCOM.Simulator.Focuser";
var expDuration = TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[argIdx++]) : 10);
var outputFolder = Directory.CreateDirectory(args.Length > 3 ? args[argIdx++] : Path.Combine(Directory.GetCurrentDirectory(), "TianWen.Lib.TestCli", "Light"));
IExternal external = new ConsoleOutput(outputFolder);


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
    mount = new Mount(mountDevice, external);
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
    guider = new Guider(guiderDevice, external);

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
    camera = new Camera(cameraDevice, external);
}
else
{
    external.LogError("Could not connect to camera");
    return 1;
}

Cover? cover;
if (coverDevice is not null)
{
    cover = new Cover(coverDevice, external);
}
else
{
    cover = null;
}

Focuser? focuser;
if (focuserDevice is not null)
{
    focuser = new Focuser(focuserDevice, external);
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
    WarmupRampInterval: TimeSpan.FromSeconds(30),
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
*/