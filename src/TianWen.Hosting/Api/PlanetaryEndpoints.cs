using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

/// <summary>
/// A live planetary capture as the node's run (P5 part 5 of docs/plans/hardware-in-the-server.md, #934): started, read,
/// adjusted and ended. The rules are <see cref="NodePlanetary"/>'s; the frames are served by <see cref="FrameEndpoints"/>.
/// </summary>
internal static class PlanetaryEndpoints
{
    public static void MapPlanetaryApi(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/planetary", async (PlanetaryRequestDto request, NodePlanetary planetary, CancellationToken ct) =>
            EnvelopeResults.Json(await planetary.StartAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto));

        routes.MapGet("/api/v1/planetary", (NodePlanetary planetary) =>
            EnvelopeResults.Json(planetary.State(), HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto));

        // Staged for the capture to take after its next frame, so answered at once.
        routes.MapPut("/api/v1/planetary/controls", (PlanetaryControlsDto controls, NodePlanetary planetary) =>
            EnvelopeResults.Json(planetary.Controls(controls), HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto));

        routes.MapDelete("/api/v1/planetary", async (NodePlanetary planetary, CancellationToken ct) =>
            EnvelopeResults.Json(await planetary.StopAsync(ct), HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto));
    }
}
