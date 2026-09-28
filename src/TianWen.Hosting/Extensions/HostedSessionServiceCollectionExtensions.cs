using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Api.Alpaca;
using TianWen.Hosting.Api.NinaV2;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Extensions;

public static class HostedSessionServiceCollectionExtensions
{
    /// <summary>
    /// How long an interactive run goes on once no client is present (<c>--detach-grace</c>), in place of the default minute:
    /// registered after <see cref="AddHostedSession"/>, whose default it replaces.
    /// </summary>
    public static IServiceCollection AddNodeDetachGrace(this IServiceCollection services, TimeSpan grace)
        => services.AddSingleton(new NodeRunWatchOptions(grace, NodeRunWatchOptions.Default.Poll));

    /// <summary>
    /// Registers the hosted session service, WebSocket event hub, and event broadcaster.
    /// </summary>
    public static IServiceCollection AddHostedSession(this IServiceCollection services)
    {
        services.AddSingleton<HostedSession>();
        services.AddSingleton<IHostedSession>(sp => sp.GetRequiredService<HostedSession>());
        services.AddSingleton<EventHub>();
        // Who may command the node over TCP (P6b of docs/plans/hardware-in-the-server.md, decision 13, #1021): grants,
        // requests for control, and the other applications allowed. The machine's resolver names an address; a test
        // registers its own first.
        services.TryAddSingleton<LAN.Lib.IHostNameResolver, LAN.Lib.DnsHostNameResolver>();
        services.AddSingleton<NodeAccess>();
        // The crash journal (P1 of docs/plans/hardware-in-the-server.md): kept only by a node that says where, which
        // tianwen-server does beside its socket. Registered FIRST of the hosted services so it stops LAST, after the
        // run and the hub's cameras have come down, and writes what is left of them.
        services.TryAddSingleton(NodeJournalOptions.None);
        services.AddSingleton<NodeJournalService>();
        services.AddHostedService(sp => sp.GetRequiredService<NodeJournalService>());
        // P3 of docs/plans/mount-safety-limits.md: enforces a configured mount limit against any
        // connected mount whether or not a session owns it. Node-scoped (survives across sessions
        // starting and ending), not part of IHostedSession itself.
        services.AddHostedService<MountLimitWatcherService>();
        // The device plane's read side (P2 part 1 of docs/plans/hardware-in-the-server.md, #929): reads every connected
        // device a run does not hold, answers GET /api/v1/devices/state and pushes DEVICE-STATE.
        services.AddSingleton<DeviceStatePoller>();
        services.AddHostedService(sp => sp.GetRequiredService<DeviceStatePoller>());
        // Its second part: connect, disconnect and warm-and-disconnect as the node's jobs, one per device at a time.
        services.AddSingleton<DeviceOperations>();
        // The node's runs that are not a session (P5, #934): a dark library first, through the CLI's own capture.
        services.TryAddSingleton<DarkFrameRun>();
        services.AddSingleton<NodeDarkLibrary>();
        // The frame each source shows, whoever took it, and the node's preview exposures outside a session (P5 part 2).
        services.AddSingleton<NodeFrames>();
        services.AddSingleton<NodePreviews>();
        // Polar alignment as the node's run (P5 part 4), and the watch that ends an interactive run nobody is watching.
        services.AddSingleton<NodePolarAlignment>();
        // A live planetary capture as the node's run (P5 part 5), stacked here, with its live frame and master served as frames.
        services.AddSingleton<NodePlanetary>();
        services.TryAddSingleton(NodeRunWatchOptions.Default);
        services.AddHostedService<NodeRunWatch>();
        // The node's one profile writer (P3 part 1, #930): every profile write, whoever asked, pushes PROFILE-CHANGED.
        services.AddSingleton<NodeProfiles>();
        // Where a masked device setting goes (P3 part 4). A host that composes AddExternal has chosen one already; any
        // other keeps its secrets in a file under its own data folder.
        services.TryAddSingleton<ICredentialStore>(sp => new FileCredentialStore(sp.GetRequiredService<IExternal>()));
        // What a run writes back into its profile as it ends (the backlash mirror, P3 part 3). Registered before the host
        // of the runs, so it stops after it and a run's last write is awaited.
        services.AddHostedService<NodeRunProfileWrites>();
        // Single-flight server-side AI enhancer behind POST /api/v1/image/enhance. The SharpenPipeline
        // is OPTIONAL -- it is registered only by AddRcAstroAi()/AddTianWenAi(), which a host (the
        // functional-test host, or a server with no AI models) need not wire. Resolve it via GetService
        // (not GetRequiredService) so a no-AI host still starts; the enhancer then reports
        // IsAvailable == false and the endpoint returns 503. Using GetService also drops the old
        // ordering requirement (AddRcAstroAi before AddHostedSession), since it resolves lazily at
        // activation time when every registration is already in place. Constructing it spawns no
        // rc-astro process -- the RC-vs-in-house choice is deferred to the first EnhanceAsync (DeferredEnhancer).
        services.AddSingleton<HostedImageEnhancer>(sp => new HostedImageEnhancer(
            sp.GetService<TianWen.Lib.Imaging.Enhancement.SharpenPipeline>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<HostedImageEnhancer>>()));
        // The node's slow operations (discovery first): started by a request, run on the node's token.
        services.AddSingleton<NodeJobs>();
        services.AddHostedService<EventBroadcaster>();

        // Which node this is (GET /api/v1/node), and where it listens. A host that listens registers its own
        // NodeListening; one that registers none is described as listening nowhere.
        services.AddSingleton(sp => NodeIdentity.Load(sp.GetRequiredService<IExternal>()));
        services.TryAddSingleton(new NodeListening(SocketPath: null, LanPort: null));
        // How the node was started and will end, and its machine settings: a host that knows better registers its own.
        // No INodeLogonStart here: a host that may start the node at logon registers one, so a test can never write
        // the user's real entry by forgetting to replace a default.
        services.TryAddSingleton(new NodeRole(spawned: false));
        services.TryAddSingleton(sp => new NodeSettingsStore(sp.GetRequiredService<IExternal>(), NodeSettings.Default));
        // Discovery on the node verifies its active profile's pinned serial ports first, as the GUI's does.
        services.TryAddSingleton<TianWen.Lib.Devices.Discovery.IPinnedSerialPortsProvider, NodePinnedSerialPorts>();

        // The host starts and stops the node's runs. It never used to: registered only as IHostedSession,
        // StartAsync (device discovery) never ran and StopAsync never stopped a run, so a SIGTERM abandoned
        // a session mid-night with its drivers unparked and uncooled. Registered LAST, because hosted
        // services stop in reverse order: the run ends through its Finalise before the others go.
        services.AddHostedService(sp => sp.GetRequiredService<HostedSession>());

        // Long enough for the stop above: a session's Finalise (warm-up, park, covers), then the hub's own
        // cameras warmed. The default is 30 s, which cut every warm-up off. A service manager's own stop
        // timeout must allow as long (systemd TimeoutStopSec).
        services.Configure<HostOptions>(options => options.ShutdownTimeout = HostedSession.ShutdownBudget);

        // Register the source-gen JSON contexts with the HTTP JSON options so minimal-API
        // request-body binding (the POST/PUT endpoints that take a complex body --
        // CreateProfileRequest, PendingTarget, SetProfileRequest) resolves type metadata
        // statically instead of via reflection. Required for the native-AOT tianwen-server:
        // without it, body binding throws NotSupportedException at runtime under AOT.
        // Responses already pass an explicit JsonTypeInfo to Results.Json, so they don't rely
        // on this chain. Hosting (camelCase) is inserted first; Nina (PascalCase) second -- the
        // only implicitly-bound types are the three Hosting request DTOs, so there is no clash.
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HostingJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(1, NinaApiJsonContext.Default);
            // Alpaca device plane. Its envelopes are PascalCase per the ASCOM spec, which is exactly why
            // it needs its own context rather than sharing the camelCase one above.
            options.SerializerOptions.TypeInfoResolverChain.Insert(2, AlpacaServerJsonContext.Default);
        });

        return services;
    }

    /// <summary>
    /// Maps all TianWen Hosting API endpoints and the WebSocket event stream.
    /// Call after <c>app.UseWebSockets()</c>.
    /// </summary>
    public static WebApplication MapHostingApi(this WebApplication app)
    {
        // Who may command the node over TCP (P6b, decision 13, #1021), the one place a command is let through or refused.
        // It reads which surface a route belongs to from the metadata each group below puts on it.
        app.Use(NodeAccessGate.GateAsync);

        // Native TianWen multi-OTA API (v1)
        var native = app.MapGroup(string.Empty).WithMetadata(new NodeProtocolMetadata(NodeProtocol.Native));
        native.MapNodeApi();
        native.MapAccessApi();
        native.MapProfileApi();
        native.MapSessionApi();
        native.MapOtaApi();
        native.MapMountApi();
        native.MapGuiderApi();
        native.MapDeviceApi();
        native.MapJobApi();
        native.MapImageApi();
        native.MapPreviewApi();
        native.MapFrameApi();
        native.MapDarkLibraryApi();
        native.MapPolarAlignmentApi();
        native.MapPlanetaryApi();
        native.MapWebSocketEndpoint();

        // ASCOM Alpaca DEVICE plane (docs/plans/remote-profile.md P5). Shares the /api/v1 prefix, which
        // the Alpaca spec fixes -- no collision, because native v1 uses session/devices/profiles/preview/
        // image and none of those is an ASCOM device type.
        app.MapGroup(string.Empty).WithMetadata(new NodeProtocolMetadata(NodeProtocol.Alpaca)).MapAlpacaApi();

        // ninaAPI v2 compatibility shim for Touch N Stars. Its commands are GETs, so a route of it is a command unless it
        // says it only reads (ReadsOnly).
        var nina = app.MapGroup(string.Empty).WithMetadata(new NodeProtocolMetadata(NodeProtocol.NinaV2));
        nina.MapNinaSystemApi();
        nina.MapNinaEquipmentApi();
        nina.MapNinaSequenceApi();
        nina.MapNinaImageApi();
        nina.MapNinaWebSocketEndpoint();

        return app;
    }

    private static void MapWebSocketEndpoint(this IEndpointRouteBuilder routes)
    {
        routes.Map("/api/v1/events", (HttpContext context, EventHub hub, IHostApplicationLifetime lifetime) =>
            ServeEventSocketAsync(context, hub, lifetime, ninaV2: false));
    }

    /// <summary>
    /// ninaAPI v2 WebSocket endpoint at <c>/v2/socket</c>.
    /// Shares the same <see cref="EventHub"/> as the native endpoint but clients
    /// receive PascalCase JSON (matching ninaAPI v2 conventions).
    /// </summary>
    private static void MapNinaWebSocketEndpoint(this IEndpointRouteBuilder routes)
    {
        // TNS may send { action: "subscribe", eventType: "..." }; every event is broadcast regardless.
        routes.Map("/v2/socket", (HttpContext context, EventHub hub, IHostApplicationLifetime lifetime) =>
            ServeEventSocketAsync(context, hub, lifetime, ninaV2: true)).ReadsOnly();
    }

    /// <summary>
    /// One client's connection, for both sockets: registered with the hub, whose sender for it does all the
    /// writing, while this reads until the client closes or its socket is aborted (a client the hub dropped
    /// for falling behind, or for a send that timed out), or the host starts stopping.
    /// </summary>
    /// <remarks>
    /// The host waits for its open requests before it stops, for up to its whole shutdown budget (30 minutes on a node),
    /// and the run's <c>Finalise</c> and the cameras' warm-up share that budget. So a socket that ended only with its client
    /// held every stop with a client attached for all of it (#985). Stopping aborts the socket instead, as the node dying
    /// would: the client reconnects with its usual backoff, and finds the node gone or back.
    /// </remarks>
    private static async Task ServeEventSocketAsync(HttpContext context, EventHub hub, IHostApplicationLifetime lifetime, bool ninaV2)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("WebSocket connections only");
            return;
        }

        // Whether this client may command the node, asked again at every count: only one that may is someone a prompt waits
        // for, or the last client a quit asks (P6b, #1021). A watcher over TCP without a grant sees everything, and counts
        // only as present, which keeps an interactive run it is watching going.
        var mayCommand = ninaV2
            ? static () => false
            : await context.RequestServices.GetRequiredService<NodeAccess>().MayCommandOverTimeAsync(context, context.RequestAborted);
        var ws = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = hub.AddClient(ws, ninaV2, mayCommand);
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);

        try
        {
            // Read until the client closes. A native client's presence beat is the one message it sends: the proof
            // its window still draws, without which it stops counting as someone who can answer a prompt.
            var buffer = new byte[256];
            while (ws.State is WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ending.Token);
                if (result.MessageType is WebSocketMessageType.Close)
                {
                    break;
                }
                if (!ninaV2 && result is { MessageType: WebSocketMessageType.Text, EndOfMessage: true }
                    && System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count) == NodeWire.PresenceBeat)
                {
                    hub.RecordBeat(clientId);
                }
            }
        }
        catch (WebSocketException)
        {
            // Client disconnected, or the hub aborted the socket.
        }
        catch (OperationCanceledException)
        {
            // The host is stopping, or the request was aborted: the cancelled read has aborted the socket.
        }
        finally
        {
            hub.RemoveClient(clientId);
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
                }
                catch
                {
                    // Best effort close
                }
            }
        }
    }
}
