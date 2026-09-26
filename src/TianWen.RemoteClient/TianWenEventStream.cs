using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib;
using TianWen.Lib.Devices;

namespace TianWen.RemoteClient
{
    /// <summary>
    /// Subscribes to a node's WebSocket push stream (<c>/api/v1/events</c>) and re-raises each
    /// <see cref="WebSocketEventDto"/> locally.
    /// <para>
    /// The stream is a <b>latency optimisation, never the source of truth</b>: a mirror still polls
    /// <c>/session/state</c>, so a dropped or missed event costs a moment of staleness rather than a
    /// wrong screen. That is what lets reconnection be a plain backoff loop with no replay protocol,
    /// no sequence numbers and no resync handshake.
    /// </para>
    /// <para>
    /// Reconnect delay uses <see cref="ITimeProvider.SleepAsync"/>, not <c>Task.Delay</c>, so a
    /// fake-clock test drives the backoff deterministically instead of waiting out real seconds.
    /// </para>
    /// </summary>
    public sealed class TianWenEventStream : IAsyncDisposable
    {
        // The server pushes small JSON objects (a phase change, a frame record). 64 KiB is far beyond
        // anything the current event set produces, and a frame larger than this is treated as a
        // protocol error rather than grown into, so a misbehaving peer cannot drive unbounded growth.
        private const int MaxMessageBytes = 64 * 1024;
        private const int ReceiveChunkBytes = 4 * 1024;

        private readonly Uri _endpoint;
        private readonly ITimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly Func<ClientWebSocket> _socketFactory;
        // The connection the socket's upgrade request goes over: the node's Unix socket for the local node, null
        // (the default TCP connection) for a remote rig. Owned: disposed with the stream.
        private readonly HttpMessageInvoker? _invoker;

        private CancellationTokenSource? _cts;
        private Task? _pump;
        // 1 once this connection has warned about an undecodable frame; reset on every connect.
        private int _warnedUnparseable;
        // 1 once the host's loop has beaten since the last beat was sent (Beat).
        private int _beatAsked;

        /// <param name="nodeBaseAddress">The node's HTTP root; the <c>ws(s)</c> event URI is derived from it.</param>
        /// <param name="socketFactory">Injectable so tests can substitute a fake socket. Defaults to a real
        /// <see cref="ClientWebSocket"/> per connection attempt (they are single-use once closed).</param>
        /// <param name="invoker">What carries the upgrade request, when it is not a TCP connection to
        /// <paramref name="nodeBaseAddress"/>: the local node's socket (<see cref="NodeTransport"/>). The stream owns it.</param>
        public TianWenEventStream(
            Uri nodeBaseAddress,
            ITimeProvider timeProvider,
            ILogger logger,
            Func<ClientWebSocket>? socketFactory = null,
            HttpMessageInvoker? invoker = null)
        {
            _endpoint = BuildEventUri(nodeBaseAddress);
            _timeProvider = timeProvider;
            _logger = logger;
            _socketFactory = socketFactory ?? (static () => new ClientWebSocket());
            _invoker = invoker;
        }

        /// <summary>
        /// Tells the node this client can SEE a prompt: call it from the loop that draws the client (every iteration is
        /// fine; it only records), never from a timer or a thread of its own. The stream sends one
        /// <see cref="NodeWire.PresenceBeat"/> per <see cref="NodeWire.PresenceBeatInterval"/> while it has been called
        /// since the last, so a window whose loop freezes falls silent, and within
        /// <see cref="NodeWire.PresenceLapse"/> the node stops holding a prompt for it (P1 of
        /// docs/plans/hardware-in-the-server.md, #917).
        /// </summary>
        public void Beat() => Volatile.Write(ref _beatAsked, 1);

        /// <summary>Raised on the receive loop's thread for every decoded push event.</summary>
        public event EventHandler<WebSocketEventDto>? EventReceived;

        /// <summary>Raised when the connected state changes, so a UI can show a live/stale badge.</summary>
        public event EventHandler<bool>? ConnectedChanged;

        /// <summary>Whether the socket is currently open.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>The derived <c>ws://host/api/v1/events</c> URI, exposed for logging and tests.</summary>
        public Uri Endpoint => _endpoint;

        /// <summary>
        /// Starts the connect/receive/reconnect pump. Idempotent -- a second call while running is a
        /// no-op, so a caller does not have to track whether it already started.
        /// </summary>
        public void Start(CancellationToken cancellationToken)
        {
            if (_pump is not null)
            {
                return;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (_cts is { } cts)
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }

            if (_pump is { } pump)
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: our own cancellation unwinding the pump.
                }
                _pump = null;
            }

            _cts?.Dispose();
            _cts = null;
            _invoker?.Dispose();
        }

        /// <summary>
        /// <c>http(s)://host:port/</c> -> <c>ws(s)://host:port/api/v1/events</c>. A separate method so
        /// the mapping is unit-testable without opening a socket.
        /// </summary>
        internal static Uri BuildEventUri(Uri nodeBaseAddress)
        {
            var builder = new UriBuilder(nodeBaseAddress)
            {
                Scheme = nodeBaseAddress.Scheme is "https" or "wss" ? "wss" : "ws",
                Path = "/api/v1/events",
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return builder.Uri;
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            // Backoff caps at 30 s: a rig that is powered off should not be probed every second all
            // night, but must reattach promptly once it is back.
            var backoff = TimeSpan.FromSeconds(1);
            var maxBackoff = TimeSpan.FromSeconds(30);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndReceiveAsync(cancellationToken).ConfigureAwait(false);
                    // A clean close means the node went away deliberately; restart from the short delay.
                    backoff = TimeSpan.FromSeconds(1);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Log the cancellation rather than swallowing it silently, then unwind.
                    _logger.LogDebug("Event stream to {Endpoint} cancelled", _endpoint);
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or System.Net.Http.HttpRequestException or IOException)
                {
                    _logger.LogDebug(ex, "Event stream to {Endpoint} dropped, retrying in {Backoff}", _endpoint, backoff);
                }
                finally
                {
                    SetConnected(false);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await _timeProvider.SleepAsync(backoff, cancellationToken).ConfigureAwait(false);
                backoff = backoff < maxBackoff ? backoff + backoff : maxBackoff;
                if (backoff > maxBackoff)
                {
                    backoff = maxBackoff;
                }
            }
        }

        private async Task ConnectAndReceiveAsync(CancellationToken cancellationToken)
        {
            using var socket = _socketFactory();
            await socket.ConnectAsync(_endpoint, _invoker, cancellationToken).ConfigureAwait(false);
            SetConnected(true);
            _logger.LogDebug("Event stream connected to {Endpoint}", _endpoint);

            // The beats go out beside the receive loop, one sender, the only one: a WebSocket allows one send and one
            // receive at a time. They end with this connection.
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var beats = SendBeatsAsync(socket, connection.Token);
            try
            {
                await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await connection.CancelAsync().ConfigureAwait(false);
                try
                {
                    await beats.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
                {
                    // The connection ended under a send: nothing more to beat on.
                }
            }
        }

        /// <summary>Sends a presence beat each interval in which the host's loop asked for one (<see cref="Beat"/>).</summary>
        private async Task SendBeatsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && socket.State is WebSocketState.Open)
            {
                await _timeProvider.SleepAsync(NodeWire.PresenceBeatInterval, cancellationToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _beatAsked, 0) == 1 && socket.State is WebSocketState.Open)
                {
                    await socket.SendAsync(PresenceBeatUtf8, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static readonly ReadOnlyMemory<byte> PresenceBeatUtf8 = System.Text.Encoding.UTF8.GetBytes(NodeWire.PresenceBeat);

        private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            using var buffer = ArrayPoolHelper.Rent<byte>(ReceiveChunkBytes);
            using var message = new MemoryStream();
            while (socket.State is WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType is WebSocketMessageType.Close)
                {
                    break;
                }

                if (message.Length + result.Count > MaxMessageBytes)
                {
                    _logger.LogWarning("Event stream {Endpoint} sent an oversized message (> {Max} bytes), reconnecting",
                        _endpoint, MaxMessageBytes);
                    return;
                }

                message.Write(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                {
                    continue;
                }

                Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                message.SetLength(0);
            }
        }

        private void Dispatch(ReadOnlySpan<byte> utf8Json)
        {
            // The node sends every event inside a ResponseEnvelope, the same envelope as its HTTP answers (the
            // ninaAPI socket shares it, PascalCase, for Touch N Stars). This used to decode a bare
            // WebSocketEventDto, so every frame failed to parse and was dropped while the stream reported
            // itself connected (P0b item 5, #752).
            WebSocketEventDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize(utf8Json, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto)?.Response;
            }
            catch (JsonException ex)
            {
                // A malformed frame must not tear down a working stream: skip it and keep receiving. But say so
                // out loud, once per connection: a stream that parses nothing is a contract break between node
                // and client, and at Debug level it read as a quiet rig for as long as it lasted.
                if (Interlocked.Exchange(ref _warnedUnparseable, 1) == 0)
                {
                    _logger.LogWarning(ex, "Discarding unparseable event frames from {Endpoint}: the node and this client disagree on the event format", _endpoint);
                }
                else
                {
                    _logger.LogDebug(ex, "Discarding unparseable event frame from {Endpoint}", _endpoint);
                }
                return;
            }

            if (dto is not null)
            {
                EventReceived?.Invoke(this, dto);
            }
        }

        private void SetConnected(bool connected)
        {
            if (IsConnected == connected)
            {
                return;
            }
            IsConnected = connected;
            if (connected)
            {
                Volatile.Write(ref _warnedUnparseable, 0);
            }
            ConnectedChanged?.Invoke(this, connected);
        }
    }
}
