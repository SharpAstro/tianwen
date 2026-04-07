using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Extensions;
using TianWen.Lib.Hosting.Extensions;
using TianWen.Lib.Logging;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddLogging(logging =>
    {
        logging.AddSimpleConsole(static options =>
        {
            options.IncludeScopes = false;
            options.SingleLine = true;
        });
        logging.AddProvider(new FileLoggerProvider("Server"));
    })
    .AddExternal()
    .AddAstrometry()
    .AddZWO()
    .AddQHY()
    .AddAscom()
    .AddMeade()
    .AddIOptron()
    .AddSkywatcher()
    .AddProfiles()
    .AddFake()
    .AddPHD2()
    .AddBuiltInGuider()
    .AddOpenMeteo()
    .AddDevices()
    .AddSessionFactory()
    .AddHostedSession();

builder.Logging.SetMinimumLevel(Debugger.IsAttached ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddFilter<FileLoggerProvider>("", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);

var app = builder.Build();

app.UseWebSockets();
app.MapHostingApi();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var port = app.Configuration["port"] ?? "1888";
logger.LogInformation("TianWen Server starting on port {Port}", port);

app.Run($"http://0.0.0.0:{port}");
