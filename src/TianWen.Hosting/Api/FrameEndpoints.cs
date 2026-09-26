using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
/// leased. The number is the node's token for the source (<see cref="NodeFrames"/>), which names exactly the frame it came
/// with, whoever produced it: a session's sub, or a preview the node took outside one (P5 part 2). Compare for
/// difference, not order.</para>
/// <para><b>The frame is LEASED for the write</b> and released once it is on the wire: the slot keeps it for whoever asks
/// next, and a refused lease means it was replaced mid-read, so the next ask finds its successor.</para>
/// <para><b>Compressed over TCP only, and only when the client asks</b> (P4 part 3; <c>Accept-Encoding</c>: Brotli at its
/// fastest, else gzip at its fastest). A sky frame's noise floor does not compress much (about 2x at best, measured in the
/// plan), so the local socket, where a copy moves a whole frame in about 40 ms, never compresses: a codec would cost more
/// than the bytes it saves. On WiFi or 100 Mbit it roughly halves a frame's transfer.</para>
/// </remarks>
internal static class FrameEndpoints
{
    public static RouteGroupBuilder MapFrameApi(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/v1/frames");

        group.MapGet("/ota/{index:int}/latest", (int index, int? after, NodeFrames frames) =>
            Serve(frames.Ota(index), after, $"OTA {index}"));

        // One guider serves the whole rig, at guiding cadence, so it is its own source rather than an OTA index.
        group.MapGet("/guider/latest", (int? after, NodeFrames frames) =>
            Serve(frames.Guider(), after, "The guider"));

        return group;
    }

    private static IResult Serve(NodeFrames.Shown shown, int? after, string what)
    {
        if (after == shown.Number && shown.Number != 0)
        {
            return new FrameResult(shown.Number, null);
        }
        if (shown.Frame is not { } published)
        {
            return Missing($"{what} has no frame to show yet", 404);
        }
        if (!published.TryLease(out var lease))
        {
            return Missing($"{what}'s frame was replaced while it was read; ask again", 404);
        }
        return new FrameResult(shown.Number, lease);
    }

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
                response.Headers.Vary = "Accept-Encoding";
                var encoding = NodeEndpoints.CameOverTheSocket(httpContext) ? null : Encoding(httpContext.Request);
                if (encoding is null)
                {
                    await FrameWire.WriteAsync(held.Image, response.Body, httpContext.RequestAborted);
                    return;
                }

                response.Headers.ContentEncoding = encoding;
                await using Stream compressing = encoding == "br"
                    ? new BrotliStream(response.Body, CompressionLevel.Fastest, leaveOpen: true)
                    : new GZipStream(response.Body, CompressionLevel.Fastest, leaveOpen: true);
                await FrameWire.WriteAsync(held.Image, compressing, httpContext.RequestAborted);
            }
            finally
            {
                lease?.Dispose();
            }
        }

        /// <summary>
        /// The encoding a TCP client asked for, preferring Brotli; null when it asked for neither, or refused both with
        /// <c>q=0</c>.
        /// </summary>
        private static string? Encoding(HttpRequest request)
        {
            var accepted = request.GetTypedHeaders().AcceptEncoding;
            bool Accepts(string coding) => accepted.Any(a => a.Value.Equals(coding, StringComparison.OrdinalIgnoreCase) && a.Quality is not 0);
            return Accepts("br") ? "br" : Accepts("gzip") ? "gzip" : null;
        }
    }
}
