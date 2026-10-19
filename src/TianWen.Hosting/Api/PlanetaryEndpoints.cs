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

        // A recording of the capture going on (P5 part 5d): it finishes its duration unwatched, and a stop ends it sooner. Named
        // for the planet the mount points at and the filter in the beam, which it reads first (#1179).
        routes.MapPost("/api/v1/planetary/record", async (PlanetaryRecordRequestDto request, NodePlanetary planetary, CancellationToken ct) =>
            EnvelopeResults.Json(await planetary.RecordAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto));

        routes.MapDelete("/api/v1/planetary/record", (NodePlanetary planetary) =>
            EnvelopeResults.Json(planetary.StopRecording(), HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto));

        // A mono filter-wheel sequence on the capture going on (#1340): one file per step, the wheel and the focuser claimed
        // until it ends, which it does by itself once its sets are taken or on a stop.
        routes.MapPost("/api/v1/planetary/sequence", async (PlanetarySequenceRequestDto request, NodePlanetary planetary, CancellationToken ct) =>
            EnvelopeResults.Json(await planetary.SequenceAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto));

        // Reframing on the full sensor (#1033): one full-sensor still, served as planetary/fullframe, and the window moved to a
        // proposal on it. Both are jobs: a still is an exposure and a whole sensor's download with the stream stopped.
        routes.MapPost("/api/v1/planetary/fullframe", (NodePlanetary planetary) =>
            EnvelopeResults.Json(planetary.TakeFullFrame(), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        routes.MapPost("/api/v1/planetary/reframe", (PlanetaryReframeRequestDto request, NodePlanetary planetary) =>
            EnvelopeResults.Json(planetary.Reframe(request), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        // Finding the planet (#1684): a full-sensor still solved, blind where nothing local can, and then, once the operator says so,
        // the mount synced to it and sent to the planet. Both are jobs.
        routes.MapPost("/api/v1/planetary/find", async (PlanetaryFindRequestDto request, NodePlanetary planetary, CancellationToken ct) =>
            EnvelopeResults.Json(await planetary.FindAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        routes.MapPost("/api/v1/planetary/find/go", async (PlanetaryFindGoRequestDto request, NodePlanetary planetary, CancellationToken ct) =>
            EnvelopeResults.Json(await planetary.FindGoAsync(request, ct), HostingJsonContext.Default.ResponseEnvelopeJobDto));

        routes.MapDelete("/api/v1/planetary/sequence", async (NodePlanetary planetary, CancellationToken ct) =>
            EnvelopeResults.Json(await planetary.StopSequenceAsync(ct), HostingJsonContext.Default.ResponseEnvelopePlanetaryStateDto));
    }
}
