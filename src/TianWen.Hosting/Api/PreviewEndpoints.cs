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
    /// token only once the encode was done. The token is the frame's own
    /// (<see cref="Lib.Sequencing.ISessionTelemetry.LastCapturedImageNumber"/>, the guider's
    /// <c>LastGuideFrameNumber</c>), which a client compares for difference, not order. Binary WebSocket
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
                IHostedSession hosted,
                CancellationToken ct) =>
            {
                if (hosted.CurrentSession is not { } session)
                {
                    return NoSession();
                }

                var render = await CapturedImagePreview.RenderAsync(
                    session,
                    otaIndex,
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
                IHostedSession hosted,
                CancellationToken ct) =>
            {
                if (hosted.CurrentSession is not { } session)
                {
                    return NoSession();
                }

                var render = await GuidePreview.RenderAsync(
                    session,
                    quality ?? PreviewEncoder.DefaultQuality,
                    scale ?? 1.0,
                    IfNoneMatch(context),
                    ct);

                return Answer(context, render);
            });

            return group;
        }

        private static IResult NoSession() => EnvelopeResults.Json(
            ResponseEnvelope<string>.Fail("No active session", 404),
            HostingJsonContext.Default.ResponseEnvelopeString);

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
