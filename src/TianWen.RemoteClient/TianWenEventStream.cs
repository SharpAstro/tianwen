using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TianWen.Hosting.Dto;
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

        private CancellationTokenSource? _cts;
        private Task? _pump;

        /// <param name="nodeBaseAddress">The node's HTTP root; the <c>ws(s)</c> event URI is derived from it.</param>
        /// <param name="socketFactory">Injectable so tests can substitute a fake socket. Defaults to a real
        /// <see cref="ClientWebSocket"/> per connection attempt (they are single-use once closed).</param>
        public TianWenEventStream(
            Uri nodeBaseAddress,
            ITimeProvider timeProvider,
            ILogger logger,
            Func<ClientWebSocket>? socketFactory = null)
        {
            _endpoint = BuildEventUri(nodeBaseAddress);
            _timeProvider = timeProvider;
            _logger = logger;
            _socketFactory = socketFactory ?? (static () => new ClientWebSocket());
        }

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
            await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
            SetConnected(true);
            _logger.LogDebug("Event stream connected to {Endpoint}", _endpoint);

            var buffer = ArrayPool<byte>.Shared.Rent(ReceiveChunkBytes);
            try
            {
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

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                    message.SetLength(0);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void Dispatch(ReadOnlySpan<byte> utf8Json)
        {
            WebSocketEventDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize(utf8Json, HostingJsonContext.Default.WebSocketEventDto);
            }
            catch (JsonException ex)
            {
                // A malformed frame must not tear down a working stream: skip it and keep receiving.
                _logger.LogDebug(ex, "Discarding unparseable event frame from {Endpoint}", _endpoint);
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
            ConnectedChanged?.Invoke(this, connected);
        }
    }
}
