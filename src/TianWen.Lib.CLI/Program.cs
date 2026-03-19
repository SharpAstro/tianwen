using Console.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pastel;
using System.CommandLine;
using System.Diagnostics;
using System.Text;
using TianWen.Lib.CLI;
using TianWen.Lib.CLI.View;
using TianWen.Lib.Extensions;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;

System.Console.InputEncoding = Encoding.UTF8;
System.Console.OutputEncoding = Encoding.UTF8;

ConsoleExtensions.Enable();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
builder.Services
    .AddLogging(static builder => builder
        .AddSimpleConsole(static options =>
        {
            options.IncludeScopes = false;
            options.SingleLine = false;
        })
        .AddProvider(new FileLoggerProvider("CLI"))
    )
    .AddExternal()
    .AddAstrometry()
    .AddZWO()
    .AddAscom()
    .AddMeade()
    .AddIOptron()
    .AddProfiles()
    .AddFake()
    .AddPHD2()
    .AddDevices()
    .AddSessionFactory()
    .AddFitsViewer()
    .AddSingleton<IVirtualTerminal, VirtualTerminal>()
    .AddSingleton<DocumentCache>()
    .AddSingleton<IConsoleHost, ConsoleHost>();

builder.Logging.SetMinimumLevel(Debugger.IsAttached ? LogLevel.Debug : LogLevel.Warning);

using var host = builder.Build();

await host.StartAsync();

var services = host.Services;
var consoleHost = services.GetRequiredService<IConsoleHost>();
// Terminal init is deferred — only initialized when view command actually needs it
var terminal = services.GetRequiredService<IVirtualTerminal>();
var viewerState = services.GetRequiredService<ViewerState>();
var documentCache = services.GetRequiredService<DocumentCache>();

// --- Command tree ---

var selectedProfileOption = new Option<string?>("--active", "-a")
{
    Description = "Profile name or ID to use",
    Recursive = true
};

var interactiveOption = new Option<bool>("--interactive", "-i")
{
    Description = "Enter alternate-screen interactive mode",
    Recursive = true
};

// Implicit path argument on root command — bare file/dir arg opens the viewer interactively
var implicitPathArg = new Argument<string?>("path")
{
    Description = "FITS file or directory to view (shorthand for 'view <path> -i')",
    Arity = ArgumentArity.ZeroOrOne
};

var viewSubCommand = new ViewSubCommand(consoleHost, viewerState, documentCache, interactiveOption);

var rootCommand = new RootCommand
{
    Arguments = { implicitPathArg },
    Options = { selectedProfileOption, interactiveOption },
    Subcommands =
    {
        new ProfileSubCommand(consoleHost, selectedProfileOption).Build(),
        new DeviceSubCommand(consoleHost).Build(),
        viewSubCommand.Build()
    }
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    var path = parseResult.GetValue(implicitPathArg);
    if (path is not null)
    {
        // Bare path argument → interactive view
        await viewSubCommand.RunInteractiveAsync(path, ct);
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
