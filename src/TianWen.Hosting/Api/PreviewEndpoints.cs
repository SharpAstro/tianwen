using System;
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
    /// <b>The change token is a conditional GET.</b> Every picture carries its frame's token as
    /// <c>X-Frame-Number</c> and as an <c>ETag</c>, and a request whose <c>If-None-Match</c> names the
    /// current one is answered 304 with no body, before the frame is leased or encoded: re-encoding a
    /// full-frame preview is not free, and it used to happen on every poll, with the client comparing the
    /// token only once the encode was done. The token is the node's for the source (<see cref="NodeFrames"/>),
    /// the same the linear frames carry, which a client compares for difference, not order. Binary WebSocket
    /// push is a later refinement; at 1-2 fps over a LAN this poll is cheap enough not to need one.
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
                NodeFrames frames,
                CancellationToken ct) =>
            {
                var render = await FramePreview.RenderAsync(
                    frames.Ota(otaIndex),
                    $"OTA {otaIndex}",
                    quality ?? PreviewEncoder.DefaultQuality,
                    scale ?? 1.0,
                    IfNoneMatch(context),
                    ct);

                return Answer(context, render);
            });

            // GET /api/v1/preview/guider?quality=&scale=
            // Not an OTA index: one guider serves the whole rig, its frames arrive at guiding cadence
            // rather than per sub, and a remote guider view needs it precisely while the science cameras
            // are mid-exposure with nothing new to show. The int route constraint above keeps the two
            // apart. Same token contract, so the same polling logic applies.
            group.MapGet("/guider", async (
                int? quality,
                double? scale,
                HttpContext context,
                NodeFrames frames,
                CancellationToken ct) =>
            {
                var render = await FramePreview.RenderAsync(
                    frames.Guider(),
                    "The guider",
                    quality ?? PreviewEncoder.DefaultQuality,
                    scale ?? 1.0,
                    IfNoneMatch(context),
                    ct);

                return Answer(context, render);
            });

            // A preview exposure outside a session, and a snapshot of the frame an OTA shows (P5 part 2, #934). The
            // exposure is a job the node finishes; its frame is then the OTA's, on every route above.
            group.MapPost("/ota/{otaIndex:int}/exposure", async (int otaIndex, PreviewExposureRequestDto request, NodePreviews previews, CancellationToken ct) =>
                EnvelopeResults.Json(await previews.StartExposureAsync(otaIndex, request, ct), HostingJsonContext.Default.ResponseEnvelopeJobDto));

            group.MapPost("/ota/{otaIndex:int}/snapshot", async (int otaIndex, NodePreviews previews) =>
                EnvelopeResults.Json(await previews.SaveSnapshotAsync(otaIndex), HostingJsonContext.Default.ResponseEnvelopeString));

            // A plate solve of the frame an OTA shows, and solve and sync (P5 part 3): each a job; a solve's result lives
            // at /solution, with the token of the frame it belongs to.
            group.MapPost("/ota/{otaIndex:int}/solve", (int otaIndex, NodePreviews previews) =>
                EnvelopeResults.Json(previews.StartSolve(otaIndex), HostingJsonContext.Default.ResponseEnvelopeJobDto));

            group.MapGet("/ota/{otaIndex:int}/solution", (int otaIndex, NodePreviews previews) =>
                EnvelopeResults.Json(previews.Solution(otaIndex), HostingJsonContext.Default.ResponseEnvelopePlateSolutionDto));

            group.MapPost("/ota/{otaIndex:int}/solve-sync", async (int otaIndex, PreviewExposureRequestDto request, NodePreviews previews, CancellationToken ct) =>
                EnvelopeResults.Json(await previews.StartSolveSyncAsync(otaIndex, request, ct), HostingJsonContext.Default.ResponseEnvelopeJobDto));

            return group;
        }

        private static IResult Answer(HttpContext context, PreviewRender render)
        {
            if (render.Failure is { } failure)
            {
                return EnvelopeResults.Json(
                    ResponseEnvelope<string>.Fail(failure, render.StatusCode),
                    HostingJsonContext.Default.ResponseEnvelopeString);
            }

            // The token rides on the picture AND on the 304, so a client that sent none learns it and one
            // that sent the current one keeps it. no-cache makes an HTTP cache ask every time rather than
            // serve a stored picture, which for a live frame is always the question worth asking.
            var token = render.FrameNumber.ToString(CultureInfo.InvariantCulture);
            var headers = context.Response.Headers;
            headers[PreviewHeaders.FrameNumber] = token;
            headers.ETag = $"\"{token}\"";
            headers.CacheControl = "no-cache";

            return render.Jpeg is { } jpeg
                ? Results.Bytes(jpeg, "image/jpeg")
                : Results.StatusCode(StatusCodes.Status304NotModified);
        }

        /// <summary>
        /// The one token a client names in <c>If-None-Match</c>, or <see langword="null"/>. A list or a
        /// wildcard is not something this API's clients send, so it simply gets the picture; the weak form is
        /// accepted, since If-None-Match compares weakly.
        /// </summary>
        private static int? IfNoneMatch(HttpContext context)
        {
            var tags = context.Request.GetTypedHeaders().IfNoneMatch;
            return tags.Count == 1
                && int.TryParse(tags[0].Tag.AsSpan().Trim('"'), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;
        }
    }
}
