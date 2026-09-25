using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        routes.MapGet("/api/v1/node", (NodeIdentity identity, NodeListening listening, EventHub events, IDeviceHub hub, IHostedSession hosted) =>
            EnvelopeResults.Json(
                ResponseEnvelope<NodeInfoDto>.Ok(new NodeInfoDto
                {
                    NodeId = identity.NodeId,
                    Version = identity.Version,
                    WireVersion = NodeWire.Version,
                    ProcessId = Environment.ProcessId,
                    IsShared = listening.IsShared,
                    ClientsAttached = events.NativeClientCount,
                    HoldsHardware = hub.ConnectedDevices.Count > 0 || hosted.IsRunning,
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
    }

    /// <summary>
    /// Whether the request reached the node over its Unix domain socket rather than TCP. Kestrel gives every TCP
    /// request its IP addresses and a socket request none, and the node listens on nothing else. (The connection's
    /// end-point feature, which would name the socket, is not one the request can see.)
    /// </summary>
    internal static bool CameOverTheSocket(HttpContext context) =>
        context.Connection.LocalIpAddress is null && context.Connection.RemoteIpAddress is null;
}
