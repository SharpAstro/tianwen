using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pastel;
using System.CommandLine;
using System.Text;
using TianWen.Lib.CLI;
using TianWen.Lib.Devices;
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

var selectedProfileOption = new Option<string?>(
    "--active",
    "-a"
)
{
    Description = "Profile name or ID to use",
    Recursive = true
};

var rootCommand = new RootCommand
{
    Options = { selectedProfileOption }
};
var profileSubCommand = new Command("profile", "Manage profiles");

var listProfilesCommand = new Command("list", "List all profiles");
listProfilesCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var selectedOptionValue = parseResult.GetValue(selectedProfileOption);
    var profiles = await consoleHost.ListProfilesAsync(cancellationToken);

    var selectedProfile = GetSelected(profiles, selectedOptionValue);

    foreach (var profile in profiles)
    {
        Console.WriteLine();
        if (profile.ProfileId == selectedProfile?.ProfileId)
        {
            Console.Write("* ".Pastel(ConsoleColor.White));
        }
        else
        {
            Console.Write("  ");
        }
        Console.WriteLine(profile.Detailed(consoleHost.DeviceUriRegistry));
    }
});

var profileNameOrIdArg = new Argument<string>("profileNameOrId") { Description = "Name or ID of the profile" };
var deleteProfileCommand = new Command("delete", "Delete a profile")
{
    Arguments = { profileNameOrIdArg }
};
deleteProfileCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileNameOrId = parseResult.GetRequiredValue(profileNameOrIdArg);
    
    var profiles = await consoleHost.ListProfilesAsync(cancellationToken);
    Profile profileToDelete;
    if (Guid.TryParse(profileNameOrId, out var profileId))
    {
        if (profiles.FirstOrDefault(p => p.ProfileId == profileId) is { } profile)
        {
            profileToDelete = profile;
        }
        else
        {
            Console.Error.WriteLine($"No profile found with ID '{profileId}'");
            return;
        }
    }
    else
    {
        var matchingProfiles = profiles.Where(p => string.Equals(p.DisplayName, profileNameOrId, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (matchingProfiles.Count is 1)
        {
            profileToDelete = matchingProfiles[0];
        }
        else if (matchingProfiles.Count > 1)
        {
            Console.Error.WriteLine($"Multiple profiles found with name '{profileNameOrId}':");
            foreach (var profile in matchingProfiles)
            {
                Console.Error.WriteLine($"- {profile.ProfileId}");
            }
            return;
        }
        else
        {
            Console.Error.WriteLine($"No profiles found with name '{profileNameOrId}'");
            return;
        }
    }

    profileToDelete.Delete(consoleHost.External);

    Console.WriteLine($"Deleted profile '{profileToDelete.DisplayName}' ({profileToDelete.ProfileId})");

    // refresh cache
    var profilesAfterDelete = await consoleHost.ListProfilesAsync(cancellationToken);
});

var profileNameArg = new Argument<string>("profileName") { Description = "Name of the new profile" };
var createProfileCommand = new Command("create", "Create a new empty profile")
{
    Arguments = { profileNameArg }
};
createProfileCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profileName = parseResult.GetRequiredValue<string>(profileNameArg);
    var newProfile = new Profile(Guid.NewGuid(), profileName, ProfileData.Empty);
    await newProfile.SaveAsync(consoleHost.External);
    Console.WriteLine($"Created new profile '{newProfile.DisplayName}' with ID {newProfile.ProfileId}");
});

profileSubCommand.Subcommands.Add(listProfilesCommand);
profileSubCommand.Subcommands.Add(deleteProfileCommand);
profileSubCommand.Subcommands.Add(createProfileCommand);

rootCommand.Subcommands.Add(profileSubCommand);

await rootCommand.Parse(args).InvokeAsync(cancellationToken: consoleHost.ApplicationLifetime.ApplicationStopped);

await host.StopAsync();

await host.WaitForShutdownAsync();

static Profile? GetSelected(IReadOnlyCollection<Profile> allProfiles, string? selected)
{
    if (Guid.TryParse(selected, out var selectedId))
    {
        return allProfiles.SingleOrDefault(p => p.ProfileId == selectedId);
    }
    else if (allProfiles.Count is 1 && string.IsNullOrEmpty(selected))
    {
        return allProfiles.Single();
    }
    else
    {
        var possibleProfiles = allProfiles.Where(p => string.Equals(p.DisplayName, selected, StringComparison.CurrentCultureIgnoreCase)).ToList();

        return possibleProfiles.Count is 1 ? possibleProfiles.Single() : null;
    }
}
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