using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

/// <summary>
/// A dark library as the node's run (P5 part 1 of docs/plans/hardware-in-the-server.md, #934): started, read and stopped.
/// The rules are <see cref="NodeDarkLibrary"/>'s.
/// </summary>
internal static class DarkLibraryEndpoints
{
    public static void MapDarkLibraryApi(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/api/v1/darks", async (DarkLibraryRequestDto request, NodeDarkLibrary darks) =>
            EnvelopeResults.Json(await darks.StartAsync(request), HostingJsonContext.Default.ResponseEnvelopeDarkLibraryStateDto));

        routes.MapGet("/api/v1/darks", (NodeDarkLibrary darks) =>
            EnvelopeResults.Json(darks.State(), HostingJsonContext.Default.ResponseEnvelopeDarkLibraryStateDto));

        // Answered once the run has ended, after the frame it had in hand: a stop is short, so the client need not poll.
        routes.MapDelete("/api/v1/darks", async (NodeDarkLibrary darks, CancellationToken ct) =>
            EnvelopeResults.Json(await darks.StopAsync(ct), HostingJsonContext.Default.ResponseEnvelopeDarkLibraryStateDto));
    }
}
