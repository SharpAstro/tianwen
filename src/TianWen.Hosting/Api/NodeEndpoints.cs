using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;

namespace TianWen.Hosting.Api;

/// <summary>
/// The node itself, as opposed to what it runs: <c>GET /api/v1/node</c>, which a client reads first to know the
/// node answered, which rig it is and whether the two speak the same wire (docs/plans/hardware-in-the-server.md,
/// "Spawn and lifetime").
/// </summary>
internal static class NodeEndpoints
{
    public static void MapNodeApi(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/v1/node", (NodeIdentity identity, NodeListening listening, EventHub events) =>
            EnvelopeResults.Json(
                ResponseEnvelope<NodeInfoDto>.Ok(new NodeInfoDto
                {
                    NodeId = identity.NodeId,
                    Version = identity.Version,
                    WireVersion = NodeWire.Version,
                    ProcessId = Environment.ProcessId,
                    IsShared = listening.IsShared,
                    ClientsAttached = events.NativeClientCount,
                }),
                HostingJsonContext.Default.ResponseEnvelopeNodeInfoDto));
    }
}
