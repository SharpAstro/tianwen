using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TianWen.Hosting.Dto;
using TianWen.Lib.Imaging;
using TianWen.Lib.Sequencing;

namespace TianWen.Hosting.Api;

/// <summary>
/// Linear frames over the socket (P4 part 2 of docs/plans/hardware-in-the-server.md, #931): the frame a source shows
/// now, in <see cref="FrameWire"/>'s binary shape, for a client that needs it linear (the viewer's stretch, statistics,
/// star profile, plate solve and snapshot save), which the preview JPEG is not.
/// </summary>
/// <remarks>
/// <para><b>The number comes first, and it answers before the frame is touched.</b> A request names the number of the
/// frame it holds (<c>after</c>); when the source still shows that one the answer is a 204 with the number, and nothing is
/// leased. The number is the source's own change token (a slot's <see cref="ISessionTelemetry.LastCapturedImageNumber"/>,
/// the guider's <see cref="ISessionTelemetry.LastGuideFrameNumber"/>), read BEFORE the frame, so it can name a frame older
/// than the pixels served (one wasted refetch) but never a newer one (a skipped frame). Compare for difference, not
/// order: a new run numbers from the start.</para>
/// <para><b>The frame is LEASED for the write</b> and released once it is on the wire: the slot keeps it for whoever asks
/// next, and a refused lease means it was replaced mid-read, so the next ask finds its successor.</para>
/// </remarks>
internal static class FrameEndpoints
{
    public static RouteGroupBuilder MapFrameApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/frames");

        group.MapGet("/ota/{index:int}/latest", (int index, int? after, IHostedSession hosted) =>
        {
            if (hosted.CurrentSession is not { } session)
            {
                return NoSession();
            }

            var number = session.LastCapturedImageNumber(index);
            var images = session.LastCapturedImages;
            if ((uint)index >= (uint)images.Length)
            {
                return Missing($"OTA index {index} out of range (0..{images.Length - 1})", 400);
            }
            return Serve(number, after, images[index], $"OTA {index}");
        });

        // One guider serves the whole rig, at guiding cadence, so it is its own source rather than an OTA index.
        group.MapGet("/guider/latest", (int? after, IHostedSession hosted) =>
            hosted.CurrentSession is { } session
                ? Serve(session.LastGuideFrameNumber, after, session.LastGuideFrame, "The guider")
                : NoSession());

        return group;
    }

    private static IResult Serve(int number, int? after, Image? published, string what)
    {
        if (after == number && number != 0)
        {
            return new FrameResult(number, null);
        }
        if (published is null)
        {
            return Missing($"{what} has no frame to show yet", 404);
        }
        if (!published.TryLease(out var lease))
        {
            return Missing($"{what}'s frame was replaced while it was read; ask again", 404);
        }
        return new FrameResult(number, lease);
    }

    private static IResult NoSession() => Missing("No session: a frame is a run's", 404);

    private static IResult Missing(string error, int status) => EnvelopeResults.Json(
        ResponseEnvelope<string>.Fail(error, status),
        HostingJsonContext.Default.ResponseEnvelopeString);

    /// <summary>
    /// A frame on the wire with its number in <see cref="PreviewHeaders.FrameNumber"/>, or, with no lease, the 204 that
    /// says the client already holds the frame the source shows. The lease is released once the frame is written, or its
    /// write has failed (the client gone mid-frame).
    /// </summary>
    private sealed class FrameResult(int number, ImageLease? lease) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                var response = httpContext.Response;
                response.Headers[PreviewHeaders.FrameNumber] = number.ToString(CultureInfo.InvariantCulture);
                response.Headers.CacheControl = "no-store";
                if (lease is not { } held)
                {
                    response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }

                response.StatusCode = StatusCodes.Status200OK;
                response.ContentType = FrameWire.ContentType;
                await FrameWire.WriteAsync(held.Image, response.Body, httpContext.RequestAborted);
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }
}
