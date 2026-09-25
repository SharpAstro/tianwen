using System;
using System.Net.Http;
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
        private NodeTransport(Uri baseAddress, string? socketPath)
        {
            BaseAddress = baseAddress;
            SocketPath = socketPath;
        }

        /// <summary>A node on the network, at <paramref name="address"/> (its HTTP root).</summary>
        public static NodeTransport OverTcp(Uri address) => new NodeTransport(address, socketPath: null);

        /// <summary>The node listening on <paramref name="socketPath"/>, this machine's own.</summary>
        public static NodeTransport OverSocket(string socketPath) => new NodeTransport(NodeSocket.BaseAddress, socketPath);

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
                return new HttpClient { BaseAddress = BaseAddress, Timeout = NodeTimeouts.ClientBackstop };
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
                return new TianWenEventStream(BaseAddress, timeProvider, logger);
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

        /// <summary>Where the node is, for a log line or a message: the socket's path, or the address.</summary>
        public override string ToString() => SocketPath ?? BaseAddress.ToString();
    }
}
