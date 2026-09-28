using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting.Api;
using TianWen.Lib.Imaging;

namespace TianWen.RemoteClient
{
    /// <summary>
    /// A frame source of a node as a stream (<see cref="FrameStreamWire"/>, P5 part 5c of
    /// docs/plans/hardware-in-the-server.md): the newest frame whenever the last one has been read, so a view that falls
    /// behind skips frames rather than queueing them. Opened through <see cref="NodeTransport.OpenFrameStreamAsync(string, CancellationToken)"/>.
    /// Over this machine's socket a frame may come in shared memory (P4b, #932): the node names its slot, and the stream copies
    /// it out into the same recycled planes, so a caller never knows which carrier brought it.
    /// </summary>
    public sealed class NodeFrameStream : IAsyncDisposable
    {
        /// <summary>How long a close waits for the node's answer before the socket is simply dropped.</summary>
        private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(2);

        private readonly ClientWebSocket _socket;
        private readonly HttpMessageInvoker? _invoker;
        private FrameSlotReader? _slots;

        internal NodeFrameStream(ClientWebSocket socket, HttpMessageInvoker? invoker)
        {
            _socket = socket;
            _invoker = invoker;
        }

        /// <summary>
        /// Asks for the next frame and reads it, with the node's number for it, through <paramref name="reader"/> (one per
        /// source: releasing a frame gives its planes back to it); null once the node has ended the stream. The frame is the
        /// newest the node has as it is asked, or the first newer than the last one read.
        /// </summary>
        public async Task<(int Number, Image Frame)?> ReadAsync(FrameReader reader, CancellationToken cancellationToken)
        {
            while (true)
            {
                await FrameStreamWire.AskAsync(_socket, cancellationToken).ConfigureAwait(false);
                if (await FrameStreamWire.ReadAnswerAsync(_socket, reader, cancellationToken).ConfigureAwait(false) is not { } answer)
                {
                    return null;
                }
                if (answer.Frame is { } frame)
                {
                    return (answer.Number, frame);
                }
                if (answer.Slot is { } slot && (_slots ??= new FrameSlotReader()).TryRead(slot.ToSlot(), reader) is { } copied)
                {
                    FramesFromSharedMemory++;
                    return (answer.Number, copied);
                }
                // Written again before it was copied: the next ask names a newer frame.
            }
        }

        /// <summary>How many frames came through shared memory rather than the socket.</summary>
        public int FramesFromSharedMemory { get; private set; }

        /// <summary>Frames the node wrote over before this stream had copied them, each answered by asking again.</summary>
        public int TornFrames => _slots?.Torn ?? 0;

        /// <summary>Drops the stream with no close, as a client that dies does: for a test of what the node does then.</summary>
        internal void Abort() => _socket.Abort();

        /// <summary>Closes the stream, which ends it on the node too.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State is WebSocketState.Open)
                {
                    using var budget = new CancellationTokenSource(CloseBudget);
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", budget.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                // Best effort: the node or the socket may be gone already, and disposing drops it either way.
            }
            finally
            {
                _socket.Dispose();
                _invoker?.Dispose();
                _slots?.Dispose();
            }
        }
    }
}
