using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using TianWen.Hosting.Dto;
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
/// <para>The client asks, rather than the node sending whenever the last send has gone, because a send is gone once the kernel
/// has the bytes: a loopback socket buffers megabytes, dozens of planetary frames, so a reader that paused was then fed
/// every frame of its pause, seconds stale, one after another (measured, and the reason for the ask).</para>
/// <para><b>Over this machine's socket a client may ask for the frames in shared memory</b> (P4b, #932): it opens the
/// stream with <c>?carrier=</c><see cref="SharedMemory"/>, and the node answers each ask with a text message instead, a
/// <see cref="FrameSlotDto"/> naming the slot the frame is in, which the client copies out. The node decides: over TCP, or
/// when it cannot make the section, the answer is the frame itself, so a client reads whichever kind comes
/// (<see cref="ReadAnswerAsync"/>).</para>
/// </remarks>
public static class FrameStreamWire
{
    /// <summary>What a client sends to ask for the next frame.</summary>
    public const string Next = "next";

    // Only ever read, by every send and every comparison.
    private static readonly byte[] NextBytes = Encoding.UTF8.GetBytes(Next);

    /// <summary>Where <paramref name="source"/> (a <see cref="Dto.FrameSources"/> name) streams from.</summary>
    public static string PathOf(string source) => $"/api/v1/frames/{source}/stream";

    /// <summary>The query key naming the carrier a client asks for.</summary>
    public const string CarrierQuery = "carrier";

    /// <summary>The carrier value asking for frames in shared memory: honoured over this machine's socket only.</summary>
    public const string SharedMemory = "shared-memory";

    /// <summary>
    /// Answers an ask with <paramref name="slot"/>, the frame waiting in shared memory: one text message. The frame stays
    /// the node's; the slot is overwritten only by a later answer.
    /// </summary>
    public static ValueTask WriteSlotAsync(WebSocket socket, FrameSlotDto slot, CancellationToken cancellationToken)
        => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(slot, HostingJsonContext.Default.FrameSlotDto).AsMemory(),
            WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

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
    /// once the node has closed the stream. For a stream opened without the shared-memory carrier, which a node answers
    /// with frames alone.
    /// </summary>
    public static async Task<(int Number, Image Frame)?> ReadAsync(WebSocket socket, FrameReader reader, CancellationToken cancellationToken)
    {
        if (await ReadAnswerAsync(socket, reader, cancellationToken) is not { } answer)
        {
            return null;
        }
        return answer.Frame is { } frame
            ? (answer.Number, frame)
            : throw new InvalidDataException("The node answered with a shared-memory slot, which this stream did not ask for");
    }

    /// <summary>
    /// The node's answer to an ask: the frame itself, read through <paramref name="reader"/> (the image is the caller's to
    /// release), or the shared-memory slot it waits in; null once the node has closed the stream.
    /// </summary>
    public static async Task<FrameAnswer?> ReadAnswerAsync(WebSocket socket, FrameReader reader, CancellationToken cancellationToken)
    {
        var message = new MessageReadStream(socket);
        var head = new byte[sizeof(int)];
        var read = 0;
        while (read < head.Length)
        {
            var got = await message.ReadAsync(head.AsMemory(read), cancellationToken);
            if (got == 0)
            {
                break;
            }
            read += got;
        }
        if (message.Closed)
        {
            return null;
        }

        if (message.MessageType is WebSocketMessageType.Text)
        {
            // A slot: small JSON, read whole.
            using var text = new MemoryStream();
            text.Write(head, 0, read);
            await message.CopyToAsync(text, cancellationToken);
            var slot = JsonSerializer.Deserialize(text.GetBuffer().AsSpan(0, (int)text.Length), HostingJsonContext.Default.FrameSlotDto)
                ?? throw new InvalidDataException("A slot message with nothing in it");
            return new FrameAnswer(slot.Number, null, slot);
        }
        if (read < head.Length)
        {
            throw new InvalidDataException("A frame message ended before its number");
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
        return new FrameAnswer(BinaryPrimitives.ReadInt32LittleEndian(head), frame, null);
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

        /// <summary>The kind of message, once its first fragment has arrived.</summary>
        public WebSocketMessageType? MessageType { get; private set; }

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
                MessageType ??= result.MessageType;
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

/// <summary>
/// A node's answer to a stream's ask (<see cref="FrameStreamWire.ReadAnswerAsync"/>): the frame itself, or the slot it waits
/// in (<see cref="FrameSlotDto"/>), with the node's number for it either way.
/// </summary>
public readonly record struct FrameAnswer(int Number, Image? Frame, FrameSlotDto? Slot);
