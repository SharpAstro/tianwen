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

        // GET /v2/api/image-history — exposure log in ninaAPI format
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
        });

        // GET /v2/api/prepared-image — last captured image as JPEG
        // Params: quality (int, 1-100), resize (bool), scale (double)
        group.MapGet("/prepared-image", async (IHostedSession hosted, int? quality, double? scale, CancellationToken ct) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return Results.NotFound();
            }

            // Find the first non-null last captured image
            var lastImages = session.LastCapturedImages;
            var image = lastImages.FirstOrDefault(img => img is not null);

            if (image is null)
            {
                return Results.NotFound();
            }

            // Shares the native-v1 preview encoder (see PreviewEncoder): this endpoint used to divide
            // each sample by MaxValue and call it an auto-stretch, which renders a linear sub as a
            // near-black frame. Both surfaces now produce the same rendering as the local viewer.
            var jpegBytes = await PreviewEncoder.EncodeJpegAsync(
                image,
                quality ?? PreviewEncoder.DefaultQuality,
                scale ?? 1.0,
                ct);

            return Results.Bytes(jpegBytes, "image/jpeg");
        });

        return group;
    }

}
