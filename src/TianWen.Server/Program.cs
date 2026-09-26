using System.Diagnostics;
using LAN.Lib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TianWen.AI.Imaging.RcAstro;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Lib;
using TianWen.Lib.Devices;
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

// A client starts the keeper, never the node itself: the keeper starts the node and starts it again if it crashes
// ("Spawn and lifetime", decision 10). It takes no lock and holds no hardware.
if (node.Keeper)
{
    using var keeperLogging = LoggerFactory.Create(logging => logging.AddProvider(new FileLoggerProvider("Keeper")));
    var keeperLogger = keeperLogging.CreateLogger("TianWen.Keeper");
    if (!OperatingSystem.IsWindows())
    {
        NodeDetachment.FromTheClient(keeperLogger);
    }

    var nodePath = Environment.ProcessPath ?? throw new InvalidOperationException("The keeper cannot tell which executable it is, to start the node");
    return await NodeKeeper.ForProcess(nodePath, node.ForTheNode(), TimeProvider.System, keeperLogger).RunAsync(CancellationToken.None);
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
    // The machine's settings, read at every start: a node a client started listens on the LAN only while the rig is
    // shared (decision 3), and a node run by hand as its command line says.
    var settings = await NodeSettings.LoadAsync(TianWenDataRoot.Directory, CancellationToken.None);
    var listening = NodeListeningDecision.For(node, settings);
    var role = new NodeRole(node.Spawned);

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
            if (node.Spawned)
            {
                // A spawned node has no terminal, and its standard streams are the null device: its log file is where
                // it writes, and nothing else (the host's defaults would add the console and, on Windows, the event log).
                logging.ClearProviders();
            }
            else
            {
                logging.AddSimpleConsole(static options =>
                {
                    options.IncludeScopes = false;
                    options.SingleLine = true;
                });
            }
            logging.AddProvider(new FileLoggerProvider("Server"));
        })
        .AddExternal()
        .AddAstrometry()
        .AddProfiles()
        .AddFake()
        .AddBuiltInGuider()
        .AddDevices()
        .AddSessionFactory()
        // RC-Astro (preferred when present + licensed) + SAS ONNX fallback for the enhance endpoint.
        // Registers SharpenPipeline; the RC-vs-SAS probe is deferred to first use, so this is cheap.
        .AddRcAstroAi()
        .AddHostedSession()
        .AddSingleton(listening)
        .AddSingleton(role)
        .AddSingleton(sp => new NodeSettingsStore(sp.GetRequiredService<IExternal>(), settings))
        .AddSingleton(NodeLogonStart.ForThisUser());

    // Every source that reaches hardware or the network, left out of a node told --fake-devices: a test's node, or a
    // demonstration's, must not probe the serial ports and cameras of the machine it runs on.
    if (!node.FakeDevicesOnly)
    {
        builder.Services
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
            .AddPHD2()
            .AddOpenMeteo()
            .AddCanon()
            .AddOpenWeatherMap();
    }

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
            o.StableNodeIdPath = Path.Combine(TianWenDataRoot.Directory.FullName, NodeIdentity.IdFileName);
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
    logger.LogInformation("TianWen Server {Version} starting on {Socket}{Lan} (node {NodeId}){Spawned}{Fake}",
        identity.Version, socketPath, listening.LanPort is { } p ? $" and TCP {p}" : "", identity.NodeId,
        node.Spawned ? ", started by a client" : "", node.FakeDevicesOnly ? ", fake devices only" : "");

    await app.RunAsync();
    // Stopped, or asked to restart to apply a setting it reads only at start (its keeper starts it again).
    return role.ExitCode;
}
