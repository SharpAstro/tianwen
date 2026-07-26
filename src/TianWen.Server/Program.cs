using System.Diagnostics;
using LAN.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Lib;
using TianWen.Lib.Extensions;
using TianWen.Hosting.Extensions;
using TianWen.Lib.Logging;

var builder = WebApplication.CreateBuilder(args);

var port = builder.Configuration["port"] ?? "1888";

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
    // RC-Astro (preferred when present + licensed) + SAS ONNX fallback for the enhance endpoint.
    // Registers SharpenPipeline; the RC-vs-SAS probe is deferred to first use, so this is cheap.
    .AddRcAstroAi()
    .AddHostedSession();

// LAN peer discovery (docs/plans/remote-profile.md): announces this node as "tianwen-server" so a
// remote GUI can find it and bind to it across restarts via the stable node id. The id file lives
// alongside the rest of TianWen's AppData (mirrors IExternal.AppDataFolder's %LOCALAPPDATA%\TianWen).
//
// ANNOUNCE-ONLY, deliberately (Listen = false). Discovery is strictly one-way: the GUI discovers rigs,
// a rig never discovers anything. A server that also kept a peer table would invite exactly the design
// we don't want -- a rig binding another rig's profile, or two rigs mirroring each other -- and makes
// "who owns the equipment" impossible to reason about. The server is only ever a bind TARGET.
builder.Services.AddLanDiscovery(o =>
{
    o.ServiceName = "tianwen-server";
    o.ServicePort = int.Parse(port);
    o.Listen = false;
    o.StableNodeIdPath = Path.Combine(
        Environment.SpecialFolder.LocalApplicationData.CreateAppSubFolder("TianWen").FullName,
        "lan-node-id.txt");
});

builder.Logging.SetMinimumLevel(Debugger.IsAttached ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddFilter<FileLoggerProvider>("", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);

var app = builder.Build();

app.UseWebSockets();
app.MapHostingApi();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
// Resolving IPeerTable here (rather than lazily on first hosted-service tick) forces LanDiscovery's
// stable node id to be loaded/minted synchronously, so it's available to log before app.Run() blocks.
var lanNodeId = app.Services.GetRequiredService<IPeerTable>().NodeId;
logger.LogInformation("TianWen Server starting on port {Port} (LAN node {NodeId})", port, lanNodeId);

app.Run($"http://0.0.0.0:{port}");
