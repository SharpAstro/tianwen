using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Lib.Devices;

namespace TianWen.Hosting.WebSocket;

/// <summary>
/// The node's connected WebSocket clients, and the one way an event reaches them. Two pools, native
/// (camelCase) and ninaAPI v2 (PascalCase); clients come and go at any time, from any thread.
/// </summary>
/// <remarks>
/// <para><b>A broadcast never waits for a socket.</b> Every client has a bounded queue drained by ONE sender,
/// its own. A broadcast serialises the event once per pool and puts it on each client's queue, and that is
/// all it does. It used to send to every client in turn on the broadcasting thread, so one stalled client
/// held up every later client and every later broadcast, without bound, and nothing timed a send out (P0b
/// item 7 of docs/plans/hardware-in-the-server.md, #752).</para>
/// <para><b>A client that cannot keep up is closed, not waited for.</b> A full queue, or a send that outlasts
/// the send timeout, aborts that client's socket. Nothing that matters is lost: polling is the authoritative
/// channel and the WebSocket a latency hint, so the client resyncs from <c>/session/state</c> and reconnects.</para>
/// <para><b>Order holds per client.</b> One sender per socket, in the order its queue was written, so a source
/// that raises its events in sequence (a session's phases) reaches every client in that sequence.</para>
/// </remarks>
internal sealed class EventHub
{
    /// <summary>How far behind a client may fall before it is closed: at guiding cadence, minutes of events.</summary>
    internal const int DefaultQueueCapacity = 256;

    /// <summary>How long one send may take before its client is given up. A healthy LAN send takes milliseconds.</summary>
    internal static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, EventClient> _clients = new ConcurrentDictionary<string, EventClient>();
    private readonly int _queueCapacity;
    private readonly TimeSpan _sendTimeout;
    private readonly ITimeProvider _timeProvider;
    private readonly ILogger _logger;

    public EventHub(ITimeProvider timeProvider, ILogger<EventHub> logger)
        : this(DefaultQueueCapacity, DefaultSendTimeout, timeProvider, logger)
    {
    }

    internal EventHub(int queueCapacity, TimeSpan sendTimeout, ITimeProvider timeProvider, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        _queueCapacity = queueCapacity;
        _sendTimeout = sendTimeout;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger.Instance;
    }

    public int ClientCount => _clients.Count;

    /// <summary>
    /// The clients that can answer a prompt: the native ones. A ninaAPI v2 socket (Touch N Stars) has no
    /// prompt route, so it is nobody to wait for; it used to count, and held every prompt indefinitely (P0b
    /// item 13 of docs/plans/hardware-in-the-server.md, #752).
    /// </summary>
    /// <remarks>
    /// Only a native client whose PRESENCE BEAT is fresh (<see cref="NodeWire.PresenceLapse"/>) counts. A registered
    /// socket used to be enough, and a window frozen by a GPU wedge keeps its socket open, so it held a prompt, and
    /// with it the night, for ever (P1 of docs/plans/hardware-in-the-server.md, #917). A client beats from the loop
    /// that draws it, so one that stops drawing stops counting within a few seconds, and the prompt gets the
    /// session's unattended answer as if nobody were attached.
    /// </remarks>
    public int PromptObserverCount
    {
        get
        {
            var now = _timeProvider.GetUtcNow().UtcTicks;
            var count = 0;
            foreach (var (_, client) in _clients)
            {
                if (!client.NinaV2 && now - client.LastBeatTicks <= NodeWire.PresenceLapse.Ticks)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Records a presence beat from client <paramref name="id"/> (<see cref="NodeWire.PresenceBeat"/>).</summary>
    public void RecordBeat(string id)
    {
        if (_clients.TryGetValue(id, out var client))
        {
            client.LastBeatTicks = _timeProvider.GetUtcNow().UtcTicks;
        }
    }

    /// <summary>
    /// The TianWen clients attached (<c>GET /api/v1/node</c>'s <c>ClientsAttached</c>): every socket but the
    /// ninaAPI ones, which are other applications watching.
    /// </summary>
    public int NativeClientCount
    {
        get
        {
            var count = 0;
            foreach (var (_, client) in _clients)
            {
                if (!client.NinaV2)
                {
                    count++;
                }
            }
            return count;
        }
    }

    /// <summary>Registers a connected socket and starts its sender. The caller removes it when the socket closes.</summary>
    public string AddClient(System.Net.WebSockets.WebSocket socket, bool ninaV2 = false)
    {
        var id = Guid.NewGuid().ToString("N");
        var client = new EventClient(socket, ninaV2, _queueCapacity);
        _clients[id] = client;
        _ = Task.Run(() => SendQueuedAsync(id, client), CancellationToken.None);
        return id;
    }

    public void RemoveClient(string id)
    {
        if (_clients.TryRemove(id, out var client))
        {
            client.Stop();
        }
    }

    /// <summary>
    /// Queues <paramref name="eventDto"/> for every client, native clients in camelCase and ninaAPI v2 clients
    /// in PascalCase. Returns once it is queued; the sends happen on each client's own sender.
    /// </summary>
    public void Broadcast(WebSocketEventDto eventDto)
    {
        // Which pools are listening, so each is serialised at most once and before anything is queued: an
        // event that fails to serialise then reaches nobody, rather than some clients and not others.
        var anyNative = false;
        var anyNina = false;
        foreach (var (_, client) in _clients)
        {
            anyNative |= !client.NinaV2;
            anyNina |= client.NinaV2;
        }
        if (!anyNative && !anyNina)
        {
            return;
        }

        var envelope = new ResponseEnvelope<WebSocketEventDto>(eventDto, "", 200, true, "Socket");
        var native = anyNative ? JsonSerializer.SerializeToUtf8Bytes(envelope, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto) : null;
        var nina = anyNina ? JsonSerializer.SerializeToUtf8Bytes(envelope, NinaApiJsonContext.Default.ResponseEnvelopeWebSocketEventDto) : null;

        foreach (var (id, client) in _clients)
        {
            // A client that joined after the scan finds its pool unserialised; it gets the next event.
            if ((client.NinaV2 ? nina : native) is not { } message)
            {
                continue;
            }

            if (client.Socket.State is not WebSocketState.Open)
            {
                Evict(id, client, "its socket is no longer open");
            }
            else if (!client.Queue.Writer.TryWrite(message))
            {
                Evict(id, client, $"it fell {_queueCapacity} events behind");
            }
        }
    }

    /// <summary>The one sender a client has: its queue, in order, each send bounded by the send timeout.</summary>
    private async Task SendQueuedAsync(string id, EventClient client)
    {
        try
        {
            await foreach (var message in client.Queue.Reader.ReadAllAsync(client.Stopping))
            {
                using var send = CancellationTokenSource.CreateLinkedTokenSource(client.Stopping);
                send.CancelAfter(_sendTimeout);
                await client.Socket.SendAsync((ReadOnlyMemory<byte>)message, WebSocketMessageType.Text, endOfMessage: true, send.Token);
            }
        }
        catch (OperationCanceledException) when (client.Stopping.IsCancellationRequested)
        {
            // Removed: its endpoint closes the socket.
        }
        catch (OperationCanceledException)
        {
            // The send outlasted its timeout. A cancelled send aborts the socket, and the endpoint's receive
            // loop then ends and removes it; evicting here stops the queue filling in the meantime.
            Evict(id, client, $"a send took longer than {_sendTimeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException)
        {
            Evict(id, client, ex.Message);
        }
    }

    /// <summary>Drops a client that cannot be served, and aborts its socket so its endpoint lets go of it too.</summary>
    private void Evict(string id, EventClient client, string why)
    {
        // Only while this id still names this client: a client that was already removed, or evicted by
        // another thread, is not dropped twice.
        if (!_clients.TryRemove(new KeyValuePair<string, EventClient>(id, client)))
        {
            return;
        }

        _logger.LogInformation("WebSocket client {ClientId} dropped because {Why}; it resyncs by polling", id, why);
        client.Stop();
        client.Socket.Abort();
    }

    private sealed class EventClient(System.Net.WebSockets.WebSocket socket, bool ninaV2, int queueCapacity)
    {
        // Never disposed: it has no timer, so there is nothing for Dispose to release, and a send that reads
        // the token after a dispose would throw rather than end.
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

        public System.Net.WebSockets.WebSocket Socket { get; } = socket;

        public bool NinaV2 { get; } = ninaV2;

        private long _lastBeatTicks = long.MinValue / 2;

        /// <summary>When the client last beat, in UTC ticks; long ago until its first beat.</summary>
        public long LastBeatTicks
        {
            get => Volatile.Read(ref _lastBeatTicks);
            set => Volatile.Write(ref _lastBeatTicks, value);
        }

        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // TryWrite, never WriteAsync: a full queue answers false at once and the client is dropped.
            FullMode = BoundedChannelFullMode.Wait,
        });

        public CancellationToken Stopping => _stopping.Token;

        public void Stop()
        {
            Queue.Writer.TryComplete();
            _stopping.Cancel();
        }
    }
}
