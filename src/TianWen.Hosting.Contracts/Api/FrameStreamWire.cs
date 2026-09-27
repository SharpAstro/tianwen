using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging;

namespace TianWen.Hosting.Api;

/// <summary>
/// A frame source as a stream (P5 part 5c of docs/plans/hardware-in-the-server.md, #934): one WebSocket per source at
/// <see cref="PathOf"/>. The client ASKS for each frame (<see cref="AskAsync"/>, the text <see cref="Next"/>), and the node
/// answers with one binary message: its number for the frame (4 bytes, little endian) and then the frame in
/// <see cref="FrameWire"/>'s shape, exactly what <c>/frames/{source}/latest</c> sends. The answer is the NEWEST frame, or
/// the first newer than the last sent: drop-to-latest, so a client that falls behind skips frames, and a live view runs at
/// the rate its client takes.
/// </summary>
/// <remarks>
/// The client asks, rather than the node sending whenever the last send has gone, because a send is gone once the kernel
/// has the bytes: a loopback socket buffers megabytes, dozens of planetary frames, so a reader that paused was then fed
/// every frame of its pause, seconds stale, one after another (measured, and the reason for the ask).
/// </remarks>
public static class FrameStreamWire
{
    /// <summary>What a client sends to ask for the next frame.</summary>
    public const string Next = "next";

    // Only ever read, by every send and every comparison.
    private static readonly byte[] NextBytes = Encoding.UTF8.GetBytes(Next);

    /// <summary>Where <paramref name="source"/> (a <see cref="Dto.FrameSources"/> name) streams from.</summary>
    public static string PathOf(string source) => $"/api/v1/frames/{source}/stream";

    /// <summary>Asks the node for the next frame; it holds one ask at a time, so a second before the answer is one.</summary>
    public static ValueTask AskAsync(WebSocket socket, CancellationToken cancellationToken)
        => socket.SendAsync(NextBytes.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    /// <summary>Whether a message the node received is a client's ask.</summary>
    public static bool IsAsk(ValueWebSocketReceiveResult result, ReadOnlySpan<byte> received)
        => result is { MessageType: WebSocketMessageType.Text, EndOfMessage: true } && received.SequenceEqual(NextBytes);

    /// <summary>Sends <paramref name="frame"/> as one message. The caller keeps the frame, leased for the call.</summary>
    public static async Task WriteAsync(WebSocket socket, int number, Image frame, CancellationToken cancellationToken)
    {
        var message = new MessageWriteStream(socket);
        var head = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(head, number);
        await message.WriteAsync(head, cancellationToken);
        await FrameWire.WriteAsync(frame, message, cancellationToken);
        await socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }

    /// <summary>
    /// The next frame and its number, read through <paramref name="reader"/> (the image is the caller's to release); null
    /// once the node has closed the stream.
    /// </summary>
    public static async Task<(int Number, Image Frame)?> ReadAsync(WebSocket socket, FrameReader reader, CancellationToken cancellationToken)
    {
        var message = new MessageReadStream(socket);
        var head = new byte[sizeof(int)];
        for (var read = 0; read < head.Length;)
        {
            var got = await message.ReadAsync(head.AsMemory(read), cancellationToken);
            if (got == 0)
            {
                return message.Closed ? null : throw new InvalidDataException("A frame message ended before its number");
            }
            read += got;
        }

        var frame = await reader.ReadAsync(message, cancellationToken);

        // The frame is whole; what is left is the message's end, an empty last fragment.
        var spare = new byte[1];
        while (!message.AtEnd)
        {
            if (await message.ReadAsync(spare, cancellationToken) > 0)
            {
                frame.Release();
                throw new InvalidDataException("A frame message held more than its frame");
            }
        }
        return (BinaryPrimitives.ReadInt32LittleEndian(head), frame);
    }

    /// <summary>Everything written is one message's next fragment; the caller ends the message.</summary>
    private sealed class MessageWriteStream(WebSocket socket) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => buffer.IsEmpty ? ValueTask.CompletedTask : socket.SendAsync(buffer, WebSocketMessageType.Binary, endOfMessage: false, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("A message is written asynchronously");
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>One message's bytes, ending (a read of 0) where the message does, or where the node closed the socket.</summary>
    private sealed class MessageReadStream(WebSocket socket) : Stream
    {
        public bool AtEnd { get; private set; }

        public bool Closed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (!AtEnd && !buffer.IsEmpty)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType is WebSocketMessageType.Close)
                {
                    Closed = true;
                    AtEnd = true;
                    return 0;
                }
                AtEnd = result.EndOfMessage;
                if (result.Count > 0)
                {
                    return result.Count;
                }
            }
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("A message is read asynchronously");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
