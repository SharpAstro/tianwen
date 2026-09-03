using Console.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pastel;
using System.CommandLine;
using System.Text;
using TianWen.AI.Imaging;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Cli;
using TianWen.Cli.Plan;
using TianWen.Cli.View;
using TianWen.Lib.Extensions;
using TianWen.Lib.Imaging.Enhancement;
using TianWen.Lib.Logging;
using TianWen.UI.Abstractions;
using TianWen.UI.Abstractions.Extensions;

System.Console.InputEncoding = Encoding.UTF8;
System.Console.OutputEncoding = Encoding.UTF8;

ConsoleExtensions.Enable();

// FIRST line of every run, on stdout, before any logging exists. A long run is normally launched
// detached with stdout redirected to a file, and this is the line that says which code produced
// whatever it writes. It is deliberately not a log call: the console floor is Warning in Release,
// so an Information line would be filtered exactly when it is needed, which is the trap that let a
// bake run 100 minutes on a binary two commits stale.
System.Console.WriteLine($"tianwen {TianWen.Lib.BuildInfo.Describe()}");

var isTui = args.Length > 0 && string.Equals(args[0], "tui", StringComparison.OrdinalIgnoreCase);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
builder.Services
    .AddLogging(builder =>
    {
        // Console logger conflicts with TUI alternate screen, only add for non-TUI modes
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
    .AddAlpaca()
    .AddMeade()
    .AddOnStep()
    .AddIOptron()
    .AddSkywatcher()
    .AddGemini()
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
    // Required by the shared AppSignalHandler, which resolves it in its CONSTRUCTOR (unlike its other
    // dependencies, which resolve lazily inside subscribe lambdas) -- so a host missing it cannot start
    // the TUI at all. The TUI has no planetary tab, but the controller does nothing until a capture
    // begins, so registering it costs nothing and keeps the handler host-agnostic. Same reason the TUI
    // hands the handler a standalone SkyMapState: supply what it needs rather than branch inside it.
    .AddSingleton<PlanetaryCaptureController>()
    // RC-preferred: uses RC-Astro (sxt/nxt/bxt) when the CLI is installed and the
    // product is licensed, else falls back to the SETI Astro ONNX enhancers.
    // AddRcAstroAi() calls AddTianWenAi() internally, so the SAS baseline stands.
    .AddRcAstroAi()
    .AddSingleton<IVirtualTerminal, VirtualTerminal>()
    .AddSingleton<DocumentCache>()
    .AddSingleton<IConsoleHost, ConsoleHost>();

// File logger captures Debug+; console (when present) gets Warning+ in Release so the
// stack/solve subcommand output stays scannable. Debug builds get the full Debug firehose
// so diagnostics like [SPCC]/[bgNeut]/[plateSolve] surface during development. Subcommands
// that need a specific diagnostic in Release should surface it deliberately via
// IConsoleHost (see StackSubCommand's `[stack] ...` lines) rather than depending on
// raising the global log floor.
#if DEBUG
builder.Logging.SetMinimumLevel(LogLevel.Debug);
#else
builder.Logging.SetMinimumLevel(LogLevel.Warning);
#endif
builder.Logging.AddFilter<FileLoggerProvider>("", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);


using var host = builder.Build();

await host.StartAsync();

var services = host.Services;
var consoleHost = services.GetRequiredService<IConsoleHost>();
// Terminal init is deferred, only initialized when view command actually needs it
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

// Implicit path argument on root command: bare file/dir arg opens the viewer interactively
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
        new PlanSubCommand(consoleHost, plannerState, services.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>(), services.GetRequiredService<TianWen.Lib.Astrometry.Comets.ICometRepository>(), profileSelector).Build(),
        new StackSubCommand(
            consoleHost,
            services.GetRequiredService<ILogger<TianWen.Lib.Imaging.Stacking.StackingPipeline>>(),
            services.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>(),
            services.GetRequiredService<SharpenPipeline>(),
            // GetService, not GetRequiredService: --remove-stars is the only thing that needs it, and
            // a host with no AI backend must still be able to run an ordinary stack.
            services.GetService<TianWen.Lib.Imaging.Enhancement.IStarRemover>()).Build(),
        new PlanetaryStackSubCommand(
            consoleHost,
            new TianWen.Lib.Imaging.Stacking.MasterPreviewRenderer(
                services.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>(),
                services.GetRequiredService<ILogger<TianWen.Lib.Imaging.Stacking.MasterPreviewRenderer>>())).Build(),
        new SolveSubCommand(
            consoleHost,
            services.GetRequiredService<TianWen.Lib.Astrometry.PlateSolve.IPlateSolverFactory>(),
            services.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>()).Build(),
        new FlatsSubCommand(
            consoleHost,
            services.GetRequiredService<TianWen.Lib.Sequencing.ISessionFactory>(),
            profileSelector).Build(),
        new ImageSubCommand(
            consoleHost,
            services.GetRequiredService<SharpenPipeline>(),
            services.GetRequiredService<IStarRemover>(),
            services.GetRequiredService<IGradientCorrector>(),
            services.GetRequiredService<TianWen.Lib.Imaging.BackgroundExtraction.IBackgroundExtractor>(),
            new TianWen.Lib.Imaging.Stacking.MasterPreviewRenderer(
                services.GetRequiredService<TianWen.Lib.Astrometry.Catalogs.ICelestialObjectDB>(),
                services.GetRequiredService<ILogger<TianWen.Lib.Imaging.Stacking.MasterPreviewRenderer>>()),
            services.GetService<ILogger<ImageSubCommand>>()).Build(),
        new DatasetSubCommand(
            consoleHost,
            // GetService: only gradient-report solves anything, and a host with no catalog must still build a dataset.
            services.GetService<TianWen.Lib.Astrometry.PlateSolve.IPlateSolverFactory>(),
            services.GetService<ILogger<DatasetSubCommand>>()).Build(),
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

// Every subcommand action already returns a considered exit code (1 for a bad invocation, 2 for a
// pipeline that ran and failed). Discarding InvokeAsync's result threw all of them away and let the
// process fall off the end at 0, so `tianwen image sharpen` printed "Sharpen failed: ..." and then
// told its caller it had succeeded -- invisible interactively, and exactly wrong in a script or a CI
// step. The code has to survive the shutdown block below, hence the local.
int exitCode;
var parsedResult = rootCommand.Parse(args);
if (parsedResult.Errors.Count is 0)
{
    exitCode = await parsedResult.InvokeAsync(cancellationToken: consoleHost.ApplicationLifetime.ApplicationStopped);
}
else
{
    foreach (var error in parsedResult.Errors)
    {
        consoleHost.WriteError(error.Message);
    }
    exitCode = 1;
}

if (terminal.IsAlternateScreen)
{
    await terminal.DisposeAsync();
}
await host.StopAsync();
await host.WaitForShutdownAsync();

return exitCode;
