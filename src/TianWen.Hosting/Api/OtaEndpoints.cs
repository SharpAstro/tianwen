using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Lib.Devices;
using TianWen.Hosting.Dto;
using TianWen.Lib.Sequencing;
// Disambiguate from Microsoft.AspNetCore.Http.ISession (ambient via the Web SDK).
using ISession = TianWen.Lib.Sequencing.ISession;

namespace TianWen.Hosting.Api;

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

            if (!TryGetOta(session, index, out _))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"OTA index {index} out of range (0..{session.Setup.Telescopes.Length - 1})"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            var metrics = index < session.LastFrameMetrics.Length ? session.LastFrameMetrics[index] : default;
            var dto = OtaCameraStateDto.FromState(index, session.CameraStates[index], metrics);
            return Results.Json(
                ResponseEnvelope<OtaCameraStateDto>.Ok(dto),
                HostingJsonContext.Default.ResponseEnvelopeOtaCameraStateDto);
        });

        // Focuser move
        group.MapPost("/{index:int}/focuser/move", async (int index, int position, IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (!TryGetOta(session, index, out var ota))
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"OTA index {index} out of range"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (ota.Focuser?.Driver is not { Connected: true } focuser)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"OTA {index} has no connected focuser"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            await focuser.BeginMoveAsync(position, ct);
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Moving focuser to position {position}"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // Focuser halt
        group.MapPost("/{index:int}/focuser/stop", async (int index, IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (!TryGetOta(session, index, out var ota) || ota.Focuser?.Driver is not { Connected: true } focuser)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"OTA {index} has no connected focuser"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            await focuser.BeginHaltAsync(ct);
            return Results.Json(
                ResponseEnvelope<string>.Ok("Focuser halted"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        // Filter wheel change
        group.MapPost("/{index:int}/filterwheel/change", async (int index, int position, IHostedSession hosted, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail("No active session", 404),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            if (!TryGetOta(session, index, out var ota) || ota.FilterWheel?.Driver is not { Connected: true } fw)
            {
                return Results.Json(
                    ResponseEnvelope<string>.Fail($"OTA {index} has no connected filter wheel"),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            await fw.BeginMoveAsync(position, ct);
            return Results.Json(
                ResponseEnvelope<string>.Ok($"Changing filter to position {position}"),
                HostingJsonContext.Default.ResponseEnvelopeString);
        });

        return group;
    }

    private static bool TryGetOta(ISession session, int index, out OTA ota)
    {
        if (index >= 0 && index < session.Setup.Telescopes.Length)
        {
            ota = session.Setup.Telescopes[index];
            return true;
        }
        ota = default!;
        return false;
    }
}
