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
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var dto = GuiderStateDto.FromSession(session);
            return Results.Json(
                ResponseEnvelope<GuiderStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeGuiderStateDto);
        });

        return group;
    }
}
