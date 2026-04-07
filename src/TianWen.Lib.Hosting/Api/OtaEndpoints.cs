using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Hosting.Dto;

namespace TianWen.Lib.Hosting.Api;

internal static class OtaEndpoints
{
    public static RouteGroupBuilder MapOtaApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/ota");

        // List all OTAs
        group.MapGet("/", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var otas = new OtaInfoDto[session.Setup.Telescopes.Length];
            for (var i = 0; i < session.Setup.Telescopes.Length; i++)
            {
                otas[i] = OtaInfoDto.FromOta(i, session.Setup.Telescopes[i]);
            }

            return Results.Json(
                ResponseEnvelope<OtaInfoDto[]>.Ok(otas),
                HostingJsonContext.Default.ResponseEnvelopeOtaInfoDtoArray);
        });

        // Per-OTA camera state
        group.MapGet("/{index:int}/camera/info", (int index, IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (index < 0 || index >= session.CameraStates.Length)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"OTA index {index} out of range (0..{session.CameraStates.Length - 1})"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var metrics = index < session.LastFrameMetrics.Length ? session.LastFrameMetrics[index] : default;
            var dto = OtaCameraStateDto.FromState(index, session.CameraStates[index], metrics);
            return Results.Json(
                ResponseEnvelope<OtaCameraStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeOtaCameraStateDto);
        });

        return group;
    }
}
