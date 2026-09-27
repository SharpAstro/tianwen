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
    /// behind skips frames rather than queueing them. Opened through <see cref="NodeTransport.OpenFrameStreamAsync"/>.
    /// </summary>
    public sealed class NodeFrameStream : IAsyncDisposable
    {
        /// <summary>How long a close waits for the node's answer before the socket is simply dropped.</summary>
        private static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(2);

        private readonly ClientWebSocket _socket;
        private readonly HttpMessageInvoker? _invoker;

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
            await FrameStreamWire.AskAsync(_socket, cancellationToken).ConfigureAwait(false);
            return await FrameStreamWire.ReadAsync(_socket, reader, cancellationToken).ConfigureAwait(false);
        }

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
            }
        }
    }
}
