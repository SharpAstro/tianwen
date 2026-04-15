using Console.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pastel;
using System.CommandLine;
using System.Diagnostics;
using System.Text;
using TianWen.Lib.CLI;
using TianWen.Lib.CLI.Plan;
using TianWen.Lib.CLI.View;
using TianWen.Lib.Extensions;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;

System.Console.InputEncoding = Encoding.UTF8;
System.Console.OutputEncoding = Encoding.UTF8;

ConsoleExtensions.Enable();

var isTui = args.Length > 0 && string.Equals(args[0], "tui", StringComparison.OrdinalIgnoreCase);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
builder.Services
    .AddLogging(builder =>
    {
        // Console logger conflicts with TUI alternate screen — only add for non-TUI modes
        if (!isTui)
        {
            builder.AddSimpleConsole(static options =>
            {
                options.IncludeScopes = false;
                options.SingleLine = false;
            });
        }
        builder.AddProvider(new FileLoggerProvider("CLI"));
    })
    .AddExternal()
    .AddAstrometry()
    .AddZWO()
    .AddQHY()
    .AddAscom()
    .AddMeade()
    .AddOnStep()
    .AddIOptron()
    .AddSkywatcher()
    .AddProfiles()
    .AddFake()
    .AddPHD2()
    .AddBuiltInGuider()
    .AddOpenMeteo()
    .AddCanon()
    .AddOpenWeatherMap()
    .AddDevices()
    .AddSessionFactory()
    .AddFitsViewer()
    .AddSingleton<IVirtualTerminal, VirtualTerminal>()
    .AddSingleton<DocumentCache>()
    .AddSingleton<IConsoleHost, ConsoleHost>();

// File logger captures Debug+; console (when present) gets Warning+ unless debugging
builder.Logging.SetMinimumLevel(Debugger.IsAttached ? LogLevel.Debug : LogLevel.Warning);
builder.Logging.AddFilter<FileLoggerProvider>("", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);


using var host = builder.Build();

await host.StartAsync();

var services = host.Services;
var consoleHost = services.GetRequiredService<IConsoleHost>();
// Terminal init is deferred — only initialized when view command actually needs it
var terminal = services.GetRequiredService<IVirtualTerminal>();
var viewerState = services.GetRequiredService<ViewerState>();
var plannerState = services.GetRequiredService<PlannerState>();
var documentCache = services.GetRequiredService<DocumentCache>();

// --- Command tree ---

var selectedProfileOption = new Option<string?>("--active", "-a")
{
    Description = "Profile name or ID to use",
    Recursive = true
};

// Implicit path argument on root command — bare file/dir arg opens the viewer interactively
var implicitPathArg = new Argument<string?>("path")
{
    Description = "FITS file or directory to view (shorthand for 'view <path>')",
    Arity = ArgumentArity.ZeroOrOne
};

var profileSelector = new ProfileSelector(consoleHost, selectedProfileOption);
var viewSubCommand = new ViewSubCommand(consoleHost, viewerState, documentCache);

var rootCommand = new RootCommand
{
    Arguments = { implicitPathArg },
    Options = { selectedProfileOption },
    Subcommands =
    {
        new ProfileSubCommand(consoleHost, selectedProfileOption, profileSelector).Build(),
        new DeviceSubCommand(consoleHost).Build(),
        viewSubCommand.Build(),
        new PlanSubCommand(consoleHost, plannerState, services.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>(), profileSelector).Build(),
        new TuiSubCommand(services, consoleHost, plannerState, profileSelector).Build()
    }
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    var path = parseResult.GetValue(implicitPathArg);
    if (path is not null)
    {
        // Bare path argument → inline view
        await viewSubCommand.RunNonInteractiveAsync(path, ct);
    }
    // No path and no subcommand → show help (default behavior)
});

var parsedResult = rootCommand.Parse(args);
if (parsedResult.Errors.Count is 0)
{
    await parsedResult.InvokeAsync(cancellationToken: consoleHost.ApplicationLifetime.ApplicationStopped);
}
else
{
    foreach (var error in parsedResult.Errors)
    {
        consoleHost.WriteError(error.Message);
    }
}

if (terminal.IsAlternateScreen)
{
    await terminal.DisposeAsync();
}
await host.StopAsync();
await host.WaitForShutdownAsync();
