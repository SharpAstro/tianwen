using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Hosting.Api;
using TianWen.Lib.Hosting.Dto;

namespace TianWen.Lib.Hosting.WebSocket;

/// <summary>
/// Manages connected WebSocket clients and broadcasts events to all of them.
/// Thread-safe — clients can connect/disconnect at any time.
/// </summary>
internal sealed class EventHub
{
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _clients = new();

    public string AddClient(System.Net.WebSockets.WebSocket socket)
    {
        var id = Guid.NewGuid().ToString("N");
        _clients.TryAdd(id, socket);
        return id;
    }

    public void RemoveClient(string id)
    {
        _clients.TryRemove(id, out _);
    }

    /// <summary>
    /// Broadcasts a typed event to all connected WebSocket clients using the ninaAPI envelope format.
    /// </summary>
    public async ValueTask BroadcastAsync(WebSocketEventDto eventDto, CancellationToken cancellationToken = default)
    {
        var envelope = new ResponseEnvelope<WebSocketEventDto>(eventDto, "", 200, true, "Socket");
        var json = JsonSerializer.Serialize(envelope, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var (id, socket) in _clients)
        {
            if (socket.State is not WebSocketState.Open)
            {
                _clients.TryRemove(id, out _);
                continue;
            }

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch (WebSocketException)
            {
                _clients.TryRemove(id, out _);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public int ClientCount => _clients.Count;
}
