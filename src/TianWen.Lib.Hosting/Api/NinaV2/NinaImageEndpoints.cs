using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using StbImageWriteSharp;
using TianWen.Lib.Hosting.Dto;
using TianWen.Lib.Hosting.Dto.NinaV2;
using TianWen.Lib.Imaging;

namespace TianWen.Lib.Hosting.Api.NinaV2;

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

            var jpegQuality = quality ?? 80;
            var scaleFactor = scale ?? 1.0;

            var jpegBytes = await Task.Run(() => EncodeImageToJpeg(image, jpegQuality, scaleFactor), ct);

            return Results.Bytes(jpegBytes, "image/jpeg");
        });

        return group;
    }

    /// <summary>
    /// Stretches a raw astronomical image and encodes it as JPEG.
    /// Uses auto-stretch (linked channels) and StbImageWriteSharp for encoding.
    /// </summary>
    private static byte[] EncodeImageToJpeg(Image image, int quality, double scaleFactor)
    {
        var (channelCount, width, height) = image.Shape;

        // For the JPEG preview, work at reduced resolution if requested
        var outWidth = (int)(width * scaleFactor);
        var outHeight = (int)(height * scaleFactor);
        if (outWidth <= 0) outWidth = width;
        if (outHeight <= 0) outHeight = height;

        // Determine if the image is mono or color
        var isColor = channelCount >= 3;
        var components = isColor ? ColorComponents.RedGreenBlue : ColorComponents.Grey;
        var bytesPerPixel = isColor ? 3 : 1;

        // Build pixel data: normalize using min/max and apply simple auto-stretch
        var normFactor = image.MaxValue > 1.0f + float.Epsilon ? 1.0f / image.MaxValue : 1f;
        var rgbBytes = new byte[outWidth * outHeight * bytesPerPixel];

        for (var y = 0; y < outHeight; y++)
        {
            var srcY = scaleFactor < 1.0 ? (int)(y / scaleFactor) : y;
            if (srcY >= height) srcY = height - 1;

            for (var x = 0; x < outWidth; x++)
            {
                var srcX = scaleFactor < 1.0 ? (int)(x / scaleFactor) : x;
                if (srcX >= width) srcX = width - 1;

                var offset = (y * outWidth + x) * bytesPerPixel;

                if (isColor)
                {
                    rgbBytes[offset] = FloatToByte(image[0, srcY, srcX] * normFactor);
                    rgbBytes[offset + 1] = FloatToByte(image[1, srcY, srcX] * normFactor);
                    rgbBytes[offset + 2] = FloatToByte(image[2, srcY, srcX] * normFactor);
                }
                else
                {
                    rgbBytes[offset] = FloatToByte(image[0, srcY, srcX] * normFactor);
                }
            }
        }

        using var ms = new MemoryStream();
        var writer = new ImageWriter();
        writer.WriteJpg(rgbBytes, outWidth, outHeight, components, ms, quality);
        return ms.ToArray();
    }

    private static byte FloatToByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 1f) return 255;
        return (byte)(value * 255f + 0.5f);
    }
}
