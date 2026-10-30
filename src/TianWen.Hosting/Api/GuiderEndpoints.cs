using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api;

internal static class GuiderEndpoints
{
    public static RouteGroupBuilder MapGuiderApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/guider");

        group.MapGet("/info", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var dto = GuiderStateDto.FromSession(session);
            return EnvelopeResults.Json(
                ResponseEnvelope<GuiderStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeGuiderStateDto);
        });

        // Calibrating and guiding off the guide camera's loop, outside a session (#1915): the node's run (NodeGuiding), whose
        // refusals name the run in the way. The POST and DELETE are commands, which the node gate lets through only with
        // control; the GET is a read.
        group.MapPost("/calibrate", async (GuidingRequestDto request, NodeGuiding guiding, CancellationToken ct) =>
            EnvelopeResults.Json(await guiding.StartAsync(GuideRunMode.Calibrate, request, ct), HostingJsonContext.Default.ResponseEnvelopeGuidingStateDto));

        group.MapPost("/guide", async (GuidingRequestDto request, NodeGuiding guiding, CancellationToken ct) =>
            EnvelopeResults.Json(await guiding.StartAsync(GuideRunMode.Guide, request, ct), HostingJsonContext.Default.ResponseEnvelopeGuidingStateDto));

        group.MapGet("/run", (NodeGuiding guiding) =>
            EnvelopeResults.Json(guiding.State(), HostingJsonContext.Default.ResponseEnvelopeGuidingStateDto));

        group.MapDelete("/run", async (NodeGuiding guiding, CancellationToken ct) =>
            EnvelopeResults.Json(await guiding.StopAsync(ct), HostingJsonContext.Default.ResponseEnvelopeGuidingStateDto));

        return group;
    }
}
