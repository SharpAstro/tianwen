using System;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;
using TianWen.Hosting.Dto.NinaV2;

namespace TianWen.Hosting.Api.NinaV2;

/// <summary>
/// ninaAPI v2 image endpoints: image-history, prepared-image.
/// </summary>
internal static class NinaImageEndpoints
{
    public static RouteGroupBuilder MapNinaImageApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v2/api");

        // GET /v2/api/image-history: exposure log in ninaAPI format
        group.MapGet("/image-history", (IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.Json(
                    ResponseEnvelope<NinaImageHistoryDto[]>.Ok([]),
                    NinaApiJsonContext.Default.ResponseEnvelopeNinaImageHistoryDtoArray);
            }

            var log = session.ExposureLog;
            var dtos = new NinaImageHistoryDto[log.Length];
            for (var i = 0; i < log.Length; i++)
            {
                dtos[i] = NinaImageHistoryDto.FromEntry(log[i], i);
            }

            return Results.Json(
                ResponseEnvelope<NinaImageHistoryDto[]>.Ok(dtos),
                NinaApiJsonContext.Default.ResponseEnvelopeNinaImageHistoryDtoArray);
        }).ReadsOnly();

        // GET /v2/api/prepared-image: last captured image as JPEG
        // Params: quality (int, 1-100), resize (bool), scale (double)
        group.MapGet("/prepared-image", async (NodeFrames frames, int? quality, double? scale, CancellationToken ct) =>
        {
            // The first OTA with a frame, rendered by the native-v1 preview's own path (FramePreview): the frame is
            // someone else's, so it is leased for the encode, and it goes through the shared stretch. This endpoint used
            // to read the slot bare, and before that divided each sample by MaxValue and called it an auto-stretch,
            // rendering a linear sub near-black.
            NodeFrames.Shown? first = null;
            for (var i = 0; i < frames.OtaCount && first is null; i++)
            {
                if (frames.Ota(i) is { Frame: not null } shown)
                {
                    first = shown;
                }
            }
            if (first is not { } found)
            {
                return Results.NotFound();
            }

            var render = await FramePreview.RenderAsync(
                found,
                "The camera",
                quality ?? PreviewEncoder.DefaultQuality,
                scale ?? 1.0,
                ifNoneMatch: null,
                ct);

            return render.Jpeg is { } jpegBytes ? Results.Bytes(jpegBytes, "image/jpeg") : Results.NotFound();
        }).ReadsOnly();

        return group;
    }

}
