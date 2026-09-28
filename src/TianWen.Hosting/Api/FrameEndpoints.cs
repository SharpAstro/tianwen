using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

        // A planetary capture's own (P5 part 5, #934): the live frame as the camera gave it, and the rolling master.
        group.MapGet("/planetary/live/latest", (int? after, NodeFrames frames) =>
            Serve(frames.Named(FrameSources.PlanetaryLive), after, "The planetary live view"));

        group.MapGet("/planetary/master/latest", (int? after, NodeFrames frames) =>
            Serve(frames.Named(FrameSources.PlanetaryMaster), after, "The planetary stack"));

        // The same two as streams (P5 part 5c): drop-to-latest over a WebSocket, a live view at the rate its client takes.
        routes.Map(FrameStreamWire.PathOf(FrameSources.PlanetaryLive), (HttpContext context, NodeFrames frames, IHostApplicationLifetime lifetime) =>
            StreamAsync(context, frames, lifetime, FrameSources.PlanetaryLive));
        routes.Map(FrameStreamWire.PathOf(FrameSources.PlanetaryMaster), (HttpContext context, NodeFrames frames, IHostApplicationLifetime lifetime) =>
            StreamAsync(context, frames, lifetime, FrameSources.PlanetaryMaster));

        return group;
    }

    /// <summary>How long a stream's close waits for the client's answer before the socket is dropped.</summary>
    private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A run's own source as a stream (<see cref="FrameStreamWire"/>): for each ask the client makes, the newest frame, or
    /// the first newer than the last one sent, so a client that falls behind skips frames rather than queueing them. It
    /// ends when the client closes it, and as the host starts stopping: the host waits for its open requests out of its
    /// whole shutdown budget (#985), so the node closes it then and gives the client a moment to answer.
    /// </summary>
    /// <remarks>
    /// A client on this machine's socket that asks for the shared-memory carrier (P4b, #932) gets each frame in a slot of
    /// the stream's own section instead, and the slot's name (<see cref="FrameSlotDto"/>). The section is the stream's and
    /// goes with it. A node that cannot make one (no <see cref="NodeSharedMemory"/>, or the system refused the memory) says
    /// so once and sends that stream's frames as bytes: a live view is never refused for its carrier.
    /// </remarks>
    private static async Task StreamAsync(HttpContext context, NodeFrames frames, IHostApplicationLifetime lifetime, string source)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connections only");
            return;
        }

        var slots = context.Request.Query[FrameStreamWire.CarrierQuery] == FrameStreamWire.SharedMemory && NodeEndpoints.CameOverTheSocket(context)
            ? context.RequestServices.GetService<NodeSharedMemory>()?.CreateWriter()
            : null;
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        // Ends the sends: the client closing the stream, the request aborted, or the host stopping.
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        // Ends the reads, and only after the node's own close, so the client's answer to it can still arrive.
        using var reading = new CancellationTokenSource();
        using var asks = new SemaphoreSlim(0, 1);
        var receiving = ReceiveAsksAsync(socket, asks, ending, reading.Token);
        var sent = 0;
        try
        {
            while (true)
            {
                await asks.WaitAsync(ending.Token);
                while (true)
                {
                    // Taken before the source is read, so a frame published in between wakes this rather than being waited past.
                    var next = frames.NextPublish;
                    var shown = frames.Named(source);
                    if (shown.Number == sent || shown.Frame is not { } frame)
                    {
                        await next.WaitAsync(ending.Token);
                        continue;
                    }
                    if (!frame.TryLease(out var lease))
                    {
                        // Replaced while it was read: its successor is already out, and the next look finds it.
                        await Task.Yield();
                        continue;
                    }
                    using (lease)
                    {
                        if (slots is not null && TryWriteSlot(slots, shown.Number, lease.Image, source, context) is { } slot)
                        {
                            await FrameStreamWire.WriteSlotAsync(socket, FrameSlotDto.Of(shown.Number, slot), ending.Token);
                        }
                        else
                        {
                            if (slots is not null)
                            {
                                slots.Dispose();
                                slots = null;
                            }
                            await FrameStreamWire.WriteAsync(socket, shown.Number, lease.Image, ending.Token);
                        }
                    }
                    sent = shown.Number;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            // The client closed it, or the host is stopping.
        }
        catch (WebSocketException)
        {
            // The client went away mid-frame.
        }
        finally
        {
            // The node's close: its answer to the client's, or its own as the host stops.
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var budget = new CancellationTokenSource(CloseBudget);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stream ended", budget.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                    // Best effort: the client may already be gone.
                }
            }
            reading.CancelAfter(CloseBudget);
            await receiving;
            slots?.Dispose();
        }
    }

    // The frame in a slot, or null when the system would not give the section its memory: the stream goes on as bytes.
    private static FrameSlot? TryWriteSlot(FrameSlotWriter slots, int number, Image frame, string source, HttpContext context)
    {
        try
        {
            return slots.Write(number, frame);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(FrameEndpoints))
                .LogWarning(ex, "The {Source} stream could not use shared memory, and sends its frames over the socket", source);
            return null;
        }
    }

    // Reads the client's asks, holding one at a time, until it closes the stream, which ends the sends with it.
    private static async Task ReceiveAsksAsync(System.Net.WebSockets.WebSocket socket, SemaphoreSlim asks, CancellationTokenSource ending,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16];
        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType is WebSocketMessageType.Close)
                {
                    break;
                }
                if (FrameStreamWire.IsAsk(result, buffer.AsSpan(0, result.Count)) && asks.CurrentCount == 0)
                {
                    asks.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client never answered the node's close within the budget: the socket is dropped.
        }
        catch (WebSocketException)
        {
            // The client went away without a close.
        }
        finally
        {
            await ending.CancelAsync();
        }
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
