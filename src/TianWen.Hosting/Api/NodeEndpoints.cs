using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.Api;

/// <summary>
/// The node itself, as opposed to what it runs: <c>GET /api/v1/node</c>, which a client reads first to know the
/// node answered, which rig it is and whether the two speak the same wire, and <c>POST /api/v1/node/shutdown</c>
/// (docs/plans/hardware-in-the-server.md, "Spawn and lifetime").
/// </summary>
internal static class NodeEndpoints
{
    public static void MapNodeApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/node", (NodeIdentity identity, NodeListening listening, NodeSettingsStore settings, EventHub events, IDeviceHub hub, IHostedSession hosted, ITimeProvider timeProvider) =>
            EnvelopeResults.Json(
                ResponseEnvelope<NodeInfoDto>.Ok(new NodeInfoDto
                {
                    NodeId = identity.NodeId,
                    Version = identity.Version,
                    WireVersion = NodeWire.Version,
                    ProcessId = Environment.ProcessId,
                    IsShared = listening.IsShared,
                    ShareOnLan = settings.Current.ShareOnLan,
                    ClientsAttached = events.NativeClientCount,
                    HoldsHardware = hub.ConnectedDevices.Count > 0 || hosted.IsRunning,
                    NowUtc = timeProvider.GetUtcNow(),
                }),
                HostingJsonContext.Default.ResponseEnvelopeNodeInfoDto));

        // Stops the node the safe way, the host's own stop (a run ends through its Finalise, the hub's cameras are
        // warmed, P0b item 4), after which the process exits cleanly and its keeper with it. Answered at once: the
        // stop can take as long as a warm-up. Only over the socket, since only a client on this machine may stop the
        // machine's node: "Stop the rig and quit" from the last window, or a client replacing an idle node of another
        // wire version.
        routes.MapPost("/api/v1/node/shutdown", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            if (!CameOverTheSocket(context))
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail("Only a client on this machine's node socket may stop the node", 403),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            lifetime.StopApplication();
            return EnvelopeResults.Json(ResponseEnvelope<string>.Accepted("Stopping"), HostingJsonContext.Default.ResponseEnvelopeString);
        });

        routes.MapPut("/api/v1/node/share", ShareAsync);
    }

    /// <summary>
    /// Turns "Share this rig on the LAN" on or off (decision 3): the machine's setting, kept by the node, and the logon
    /// entry that starts the node while it is on. Only over the socket, since only a user of this machine may expose
    /// it. The listening follows at the node's next start; an idle node a client started restarts to apply it at
    /// once (its keeper starts it again), while one holding hardware keeps running and applies it after.
    /// </summary>
    private static async Task<IResult> ShareAsync(HttpContext context, NodeShareRequest request, NodeSettingsStore settings, NodeListening listening,
        NodeRole role, IDeviceHub hub, IHostedSession hosted, IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
    {
        if (!CameOverTheSocket(context))
        {
            return EnvelopeResults.Json(
                ResponseEnvelope<NodeShareDto>.Fail("Only a client on this machine's node socket may share the rig", 403),
                HostingJsonContext.Default.ResponseEnvelopeNodeShareDto);
        }
        if (context.RequestServices.GetService<INodeLogonStart>() is not { } logon)
        {
            return EnvelopeResults.Json(
                ResponseEnvelope<NodeShareDto>.Fail("This node cannot start itself at logon, so it cannot share the rig", 501),
                HostingJsonContext.Default.ResponseEnvelopeNodeShareDto);
        }

        await settings.SaveAsync(new NodeSettings(request.Shared), cancellationToken);
        logon.Set(request.Shared, Environment.ProcessPath ?? throw new InvalidOperationException("The node cannot tell which executable it is, to start at logon"));

        var applied = listening.IsShared == request.Shared;
        if (!role.Spawned || applied)
        {
            return EnvelopeResults.Json(ResponseEnvelope<NodeShareDto>.Ok(new NodeShareDto
            {
                Shared = request.Shared,
                Listening = listening.IsShared,
                Message = applied
                    ? (request.Shared ? "The rig is shared on the LAN" : "The rig is no longer shared on the LAN")
                    : "Saved. This node was started by hand and listens as its command line says; a node a client starts follows the setting",
            }), HostingJsonContext.Default.ResponseEnvelopeNodeShareDto);
        }

        if (hub.ConnectedDevices.Count > 0 || hosted.IsRunning)
        {
            return EnvelopeResults.Json(ResponseEnvelope<NodeShareDto>.Ok(new NodeShareDto
            {
                Shared = request.Shared,
                Listening = listening.IsShared,
                Message = "Saved. It takes effect when the node next starts: it holds the rig now, so it is not restarted",
            }), HostingJsonContext.Default.ResponseEnvelopeNodeShareDto);
        }

        role.ExitCode = NodeExitCodes.Restart;
        lifetime.StopApplication();
        return EnvelopeResults.Json(ResponseEnvelope<NodeShareDto>.Accepted(new NodeShareDto
        {
            Shared = request.Shared,
            Listening = listening.IsShared,
            Restarting = true,
            Message = "Restarting the node to apply it",
        }), HostingJsonContext.Default.ResponseEnvelopeNodeShareDto);
    }

    /// <summary>
    /// Whether the request reached the node over its Unix domain socket rather than TCP. Kestrel gives every TCP
    /// request its IP addresses and a socket request none, and the node listens on nothing else. (The connection's
    /// end-point feature, which would name the socket, is not one the request can see.)
    /// </summary>
    internal static bool CameOverTheSocket(HttpContext context) =>
        context.Connection.LocalIpAddress is null && context.Connection.RemoteIpAddress is null;
}
