using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

/// <summary>
/// Polar alignment as the node's run (P5 part 4 of docs/plans/hardware-in-the-server.md, #934): started, read and ended.
/// The rules are <see cref="NodePolarAlignment"/>'s.
/// </summary>
internal static class PolarAlignmentEndpoints
{
    public static void MapPolarAlignmentApi(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/polar", async (PolarAlignmentRequestDto request, NodePolarAlignment polar, CancellationToken ct) =>
            EnvelopeResults.Json(await polar.StartAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopePolarStateDto));

        routes.MapGet("/api/v1/polar", (NodePolarAlignment polar) =>
            EnvelopeResults.Json(polar.State(), HostingJsonContext.Default.ResponseEnvelopePolarStateDto));

        // Done and Cancel alike, answered at once: the restore that follows takes longer than a request may.
        routes.MapDelete("/api/v1/polar", (NodePolarAlignment polar) =>
            EnvelopeResults.Json(polar.Stop(), HostingJsonContext.Default.ResponseEnvelopePolarStateDto));
    }
}
