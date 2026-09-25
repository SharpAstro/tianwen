using System.Diagnostics;
using LAN.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Lib;
using TianWen.Lib.Extensions;
using TianWen.Hosting.Extensions;
using TianWen.Lib.Logging;

// The machine's node listens on its socket, however it was started (docs/plans/hardware-in-the-server.md,
// "Transport"): a node run by hand IS the local node, so a GUI finds it rather than starting a second one onto
// the same hardware. --socket names another, for a test or a developer's isolated node.
if (!NodeArguments.TryParse(args, out var node, out var invalid))
{
    Console.Error.WriteLine(invalid);
    return NodeExitCodes.InvalidArguments;
}

var socketPath = node.SocketPath;

// One node per socket: the lock, not the socket file, decides it, and only its holder may clear a stale socket.
if (!NodeLock.TryAcquire(socketPath, out var held, out var refusal))
{
    Console.Error.WriteLine(await NodeLock.DescribeHolderAsync(socketPath, refusal, CancellationToken.None));
    return NodeExitCodes.AlreadyRunning;
}

using (held)
{
    // No args: the command line is the node's own (NodeArguments), not the host configuration's.
    var builder = WebApplication.CreateBuilder();
    var listening = new NodeListening(socketPath, node.LocalOnly ? null : node.Port);

    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenOnNodeSocket(held);
        if (listening.LanPort is { } lanPort)
        {
            kestrel.ListenAnyIP(lanPort);
        }
    });

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
        .AddPlayerOne()
        .AddToupTek()
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
        .AddHostedSession()
        .AddSingleton(listening);

    // LAN peer discovery (docs/plans/remote-profile.md): announces this node as "tianwen-server" so a remote GUI
    // can find it and bind to it across restarts via the stable node id, which it reads from the same file as
    // GET /api/v1/node (NodeIdentity). Only a node the LAN can reach announces itself.
    //
    // ANNOUNCE-ONLY, deliberately (Listen = false). Discovery is strictly one-way: the GUI discovers rigs,
    // a rig never discovers anything. A server that also kept a peer table would invite exactly the design
    // we don't want -- a rig binding another rig's profile, or two rigs mirroring each other -- and makes
    // "who owns the equipment" impossible to reason about. The server is only ever a bind TARGET.
    if (listening.LanPort is { } announcedPort)
    {
        builder.Services.AddLanDiscovery(o =>
        {
            o.ServiceName = "tianwen-server";
            o.ServicePort = announcedPort;
            o.Listen = false;
            o.StableNodeIdPath = Path.Combine(
                Environment.SpecialFolder.LocalApplicationData.CreateAppSubFolder("TianWen").FullName,
                NodeIdentity.IdFileName);
        });
    }

    builder.Logging.SetMinimumLevel(Debugger.IsAttached ? LogLevel.Debug : LogLevel.Information);
    builder.Logging.AddFilter<FileLoggerProvider>("", LogLevel.Debug);
    builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
    builder.Logging.AddFilter("System", LogLevel.Warning);

    var app = builder.Build();

    app.UseWebSockets();
    app.MapHostingApi();
    app.RestrictNodeSocketToItsOwner(held);

    // Resolved before anything reads the id file's neighbour: the node's id is minted here on a first start, and the
    // LAN announcement then reads the same file rather than minting a second one beside it.
    var identity = app.Services.GetRequiredService<NodeIdentity>();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("TianWen Server {Version} starting on {Socket}{Lan} (node {NodeId})",
        identity.Version, socketPath, listening.LanPort is { } p ? $" and TCP {p}" : "", identity.NodeId);

    await app.RunAsync();
}

return NodeExitCodes.Stopped;
