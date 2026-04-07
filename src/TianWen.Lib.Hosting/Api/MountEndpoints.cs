using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Hosting.Dto;

namespace TianWen.Lib.Hosting.Api;

internal static class MountEndpoints
{
    public static RouteGroupBuilder MapMountApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/mount");

        group.MapGet("/info", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var dto = MountStateDto.FromState(session.MountState);
            return Results.Json(
                ResponseEnvelope<MountStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeMountStateDto);
        });

        return group;
    }
}
