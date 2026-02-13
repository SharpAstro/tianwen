using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pastel;
using System.CommandLine;
using System.Diagnostics;
using System.Text;
using TianWen.Lib.CLI;
using TianWen.Lib.Extensions;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

ConsoleExtensions.Enable();

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

builder.Logging.SetMinimumLevel(Debugger.IsAttached ? LogLevel.Debug : LogLevel.Warning);

using var host = builder.Build();

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
        new ProfileSubCommand(consoleHost, selectedProfileOption).Build(),
        new DeviceSubCommand(consoleHost).Build()
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
        consoleHost.WriteError(error.Message);
    }
}

await host.StopAsync();

await host.WaitForShutdownAsync();