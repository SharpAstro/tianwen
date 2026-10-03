using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

/// <summary>
/// A live view as the node's run (P4 of docs/plans/live-session-preview.md, #1111): started, read, adjusted and ended. The
/// rules are <see cref="NodeLiveView"/>'s; the frames are served by <see cref="FrameEndpoints"/>.
/// </summary>
internal static class LiveViewEndpoints
{
    public static void MapLiveViewApi(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/live", async (LiveViewRequestDto request, NodeLiveView live, CancellationToken ct) =>
            EnvelopeResults.Json(await live.StartAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopeLiveViewStateDto));

        routes.MapGet("/api/v1/live", (NodeLiveView live) =>
            EnvelopeResults.Json(live.State(), HostingJsonContext.Default.ResponseEnvelopeLiveViewStateDto));

        // Staged for the capture to take after its next frame, so answered at once.
        routes.MapPut("/api/v1/live/controls", (LiveViewControlsDto controls, NodeLiveView live) =>
            EnvelopeResults.Json(live.Controls(controls), HostingJsonContext.Default.ResponseEnvelopeLiveViewStateDto));

        routes.MapDelete("/api/v1/live", async (NodeLiveView live, CancellationToken ct) =>
            EnvelopeResults.Json(await live.StopAsync(ct), HostingJsonContext.Default.ResponseEnvelopeLiveViewStateDto));
    }
}
