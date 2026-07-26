using System.Globalization;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;

namespace TianWen.Hosting.Api
{
    /// <summary>
    /// Native-v1 per-OTA live preview: the stretched last-captured frame as a JPEG.
    /// <para>
    /// Per-OTA rather than "the first non-null frame" (which is all the ninaAPI shim's single-OTA
    /// <c>prepared-image</c> can express), because a dual-saddle rig is exactly the case where a remote
    /// operator needs to see which OTA is misbehaving.
    /// </para>
    /// <para>
    /// <b>The <c>X-Frame-Number</c> response header is the change token.</b> Re-encoding a full-frame
    /// preview is not free, so a polling client compares this against what it last drew and skips the
    /// fetch when the camera has not delivered a new frame. It is the same counter the camera state
    /// reports, so a client can also decide to fetch straight from a <c>/session/state</c> poll it was
    /// making anyway. Binary WebSocket push is a later refinement; at 1-2 fps over a LAN this poll is
    /// cheap enough not to need one.
    /// </para>
    /// </summary>
    internal static class PreviewEndpoints
    {
        public static RouteGroupBuilder MapPreviewApi(this IEndpointRouteBuilder routes)
        {
            var group = routes.MapGroup("/api/v1/preview");

            // GET /api/v1/preview/{otaIndex}?quality=&scale=
            group.MapGet("/{otaIndex:int}", async (
                int otaIndex,
                int? quality,
                double? scale,
                HttpContext context,
                IHostedSession hosted,
                CancellationToken ct) =>
            {
                if (hosted.CurrentSession is not { } session)
                {
                    return Results.Json(
                        ResponseEnvelope<string>.Fail("No active session", 404),
                        HostingJsonContext.Default.ResponseEnvelopeString);
                }

                var images = session.LastCapturedImages;
                if (otaIndex < 0 || otaIndex >= images.Length)
                {
                    return Results.Json(
                        ResponseEnvelope<string>.Fail($"OTA index {otaIndex} out of range (0..{images.Length - 1})"),
                        HostingJsonContext.Default.ResponseEnvelopeString);
                }

                if (images[otaIndex] is not { } image)
                {
                    // Distinguished from a bad index: the OTA exists but has not delivered a frame yet
                    // (before the first exposure completes, or after a warm-up that released it).
                    return Results.Json(
                        ResponseEnvelope<string>.Fail($"OTA {otaIndex} has not captured a frame yet", 404),
                        HostingJsonContext.Default.ResponseEnvelopeString);
                }

                var states = session.CameraStates;
                var frameNumber = otaIndex < states.Length ? states[otaIndex].FrameNumber : 0;

                var jpeg = await PreviewEncoder.EncodeJpegAsync(
                    image,
                    quality ?? PreviewEncoder.DefaultQuality,
                    scale ?? 1.0,
                    ct);

                context.Response.Headers["X-Frame-Number"] = frameNumber.ToString(CultureInfo.InvariantCulture);
                return Results.Bytes(jpeg, "image/jpeg");
            });

            return group;
        }
    }
}
