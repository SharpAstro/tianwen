using System;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Lib.Devices;

namespace TianWen.RemoteClient
{
    /// <summary>
    /// How a client reaches a node: TCP for a remote rig, the machine's socket for the local node
    /// (docs/plans/hardware-in-the-server.md, "Transport"). Everything above it, <see cref="TianWenNodeClient"/>,
    /// <see cref="TianWenEventStream"/> and <see cref="RemoteSessionMirror"/>, is the same either way: one API, one
    /// client and one test surface.
    /// </summary>
    public sealed class NodeTransport
    {
        private NodeTransport(Uri baseAddress, string? socketPath, NodeGrant grant)
        {
            BaseAddress = baseAddress;
            SocketPath = socketPath;
            Grant = grant;
        }

        /// <summary>
        /// A node on the network, at <paramref name="address"/> (its HTTP root), presenting <paramref name="grant"/> on every
        /// request and socket once it holds a token: over TCP a node refuses a command without one (P6b, #1021).
        /// </summary>
        public static NodeTransport OverTcp(Uri address, NodeGrant? grant = null) => new NodeTransport(address, socketPath: null, grant ?? new NodeGrant());

        /// <summary>The node listening on <paramref name="socketPath"/>, this machine's own, which needs no grant.</summary>
        public static NodeTransport OverSocket(string socketPath) => new NodeTransport(NodeSocket.BaseAddress, socketPath, new NodeGrant());

        /// <summary>
        /// The control this client holds on the node. Over TCP it goes with every request and socket; over the socket it is
        /// never needed, since a client of this machine may command it anyway.
        /// </summary>
        public NodeGrant Grant { get; }

        /// <summary>The root every request is made against. For a socket, only its host reaches the node.</summary>
        public Uri BaseAddress { get; }

        /// <summary>The socket the node listens on, or null for a node reached over TCP.</summary>
        public string? SocketPath { get; }

        /// <summary>
        /// An HTTP client for <see cref="TianWenNodeClient"/>, the caller's to dispose. Its timeout is only the
        /// backstop; the per-request budgets in <see cref="NodeTimeouts"/> are what bite.
        /// </summary>
        public HttpClient CreateHttpClient()
        {
            if (SocketPath is null)
            {
                // Over TCP a client takes a linear frame compressed (P4 part 3 of docs/plans/hardware-in-the-server.md):
                // on WiFi or 100 Mbit that roughly halves a frame's transfer. The socket never asks, since there a copy is
                // cheaper than any codec, and the node never compresses a frame there anyway.
                // The grant is read as each request goes, so one granted after this client was made applies at once.
                NodeGrant.Handler? granted = new NodeGrant.Handler(Grant, DecompressionMethods.Brotli | DecompressionMethods.GZip);
                try
                {
                    var overTcp = new HttpClient(granted) { BaseAddress = BaseAddress, Timeout = NodeTimeouts.ClientBackstop };
                    granted = null;
                    return overTcp;
                }
                finally
                {
                    granted?.Dispose();
                }
            }

            // Handed to the client, which disposes it. Nulled once it is, the form CA2000 can follow.
            SocketsHttpHandler? handler = NodeSocket.CreateHandler(SocketPath);
            try
            {
                var client = new HttpClient(handler) { BaseAddress = BaseAddress, Timeout = NodeTimeouts.ClientBackstop };
                handler = null;
                return client;
            }
            finally
            {
                handler?.Dispose();
            }
        }

        /// <summary>The node's event stream over the same transport, the caller's to dispose.</summary>
        public TianWenEventStream CreateEventStream(ITimeProvider timeProvider, ILogger logger)
        {
            if (SocketPath is null)
            {
                return new TianWenEventStream(BaseAddress, timeProvider, logger, grant: Grant);
            }

            // The handler is handed to the invoker, and the invoker to the stream, which disposes it with itself.
            SocketsHttpHandler? handler = NodeSocket.CreateHandler(SocketPath);
            HttpMessageInvoker? invoker = null;
            try
            {
                invoker = new HttpMessageInvoker(handler);
                handler = null;
                var stream = new TianWenEventStream(BaseAddress, timeProvider, logger, invoker: invoker);
                invoker = null;
                return stream;
            }
            finally
            {
                invoker?.Dispose();
                handler?.Dispose();
            }
        }

        /// <summary>
        /// Opens <paramref name="source"/> (a <see cref="Hosting.Dto.FrameSources"/> name the node streams) as a
        /// <see cref="NodeFrameStream"/>, over the same transport; the caller's to dispose. Over this machine's socket it asks
        /// for the frames in shared memory (P4b of docs/plans/hardware-in-the-server.md, #932), which the node may decline.
        /// </summary>
        public Task<NodeFrameStream> OpenFrameStreamAsync(string source, CancellationToken cancellationToken)
            => OpenFrameStreamAsync(source, sharedMemory: SocketPath is not null, cancellationToken);

        /// <summary>
        /// Opens <paramref name="source"/> as <see cref="OpenFrameStreamAsync(string, CancellationToken)"/> does, asking for
        /// shared memory only when <paramref name="sharedMemory"/> says so (a measurement of either carrier). A node reached
        /// over TCP never offers it.
        /// </summary>
        public async Task<NodeFrameStream> OpenFrameStreamAsync(string source, bool sharedMemory, CancellationToken cancellationToken)
        {
            var endpoint = WebSocketUri(BaseAddress, FrameStreamWire.PathOf(source),
                sharedMemory && SocketPath is not null ? $"{FrameStreamWire.CarrierQuery}={FrameStreamWire.SharedMemory}" : null);
            // The handler is handed to the invoker, which disposes it, and the invoker and the socket to the stream: each nulled
            // once handed on, the form CA2000 can follow.
            SocketsHttpHandler? handler = SocketPath is null ? null : NodeSocket.CreateHandler(SocketPath);
            HttpMessageInvoker? invoker = null;
            ClientWebSocket? socket = null;
            try
            {
                if (handler is not null)
                {
                    invoker = new HttpMessageInvoker(handler);
                    handler = null;
                }
                socket = new ClientWebSocket();
                if (SocketPath is null)
                {
                    Grant.Present(socket.Options);
                }
                await socket.ConnectAsync(endpoint, invoker, cancellationToken).ConfigureAwait(false);
                var stream = new NodeFrameStream(socket, invoker);
                socket = null;
                invoker = null;
                return stream;
            }
            finally
            {
                socket?.Dispose();
                invoker?.Dispose();
                handler?.Dispose();
            }
        }

        /// <summary>The <c>ws://</c> (or <c>wss://</c>) form of <paramref name="path"/> on the node at <paramref name="baseAddress"/>.</summary>
        internal static Uri WebSocketUri(Uri baseAddress, string path, string? query = null)
            => new UriBuilder(baseAddress)
            {
                Scheme = baseAddress.Scheme is "https" or "wss" ? "wss" : "ws",
                Path = path,
                Query = query ?? string.Empty,
                Fragment = string.Empty,
            }.Uri;

        /// <summary>Where the node is, for a log line or a message: the socket's path, or the address.</summary>
        public override string ToString() => SocketPath ?? BaseAddress.ToString();
    }
}
