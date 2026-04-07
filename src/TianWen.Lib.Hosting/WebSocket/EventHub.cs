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
/// Supports two client pools: native (camelCase) and ninaAPI v2 (PascalCase).
/// Thread-safe — clients can connect/disconnect at any time.
/// </summary>
internal sealed class EventHub
{
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _nativeClients = new();
    private readonly ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> _ninaClients = new();

    public string AddClient(System.Net.WebSockets.WebSocket socket, bool ninaV2 = false)
    {
        var id = Guid.NewGuid().ToString("N");
        (ninaV2 ? _ninaClients : _nativeClients).TryAdd(id, socket);
        return id;
    }

    public void RemoveClient(string id)
    {
        _nativeClients.TryRemove(id, out _);
        _ninaClients.TryRemove(id, out _);
    }

    /// <summary>
    /// Broadcasts a typed event to all connected WebSocket clients.
    /// Native clients get camelCase, ninaAPI v2 clients get PascalCase.
    /// </summary>
    public async ValueTask BroadcastAsync(WebSocketEventDto eventDto, CancellationToken cancellationToken = default)
    {
        var envelope = new ResponseEnvelope<WebSocketEventDto>(eventDto, "", 200, true, "Socket");

        if (_nativeClients.Count > 0)
        {
            var nativeJson = JsonSerializer.Serialize(envelope, HostingJsonContext.Default.ResponseEnvelopeWebSocketEventDto);
            var nativeBytes = Encoding.UTF8.GetBytes(nativeJson);
            await SendToClientsAsync(_nativeClients, nativeBytes, cancellationToken);
        }

        if (_ninaClients.Count > 0)
        {
            var ninaJson = JsonSerializer.Serialize(envelope, NinaApiJsonContext.Default.ResponseEnvelopeWebSocketEventDto);
            var ninaBytes = Encoding.UTF8.GetBytes(ninaJson);
            await SendToClientsAsync(_ninaClients, ninaBytes, cancellationToken);
        }
    }

    private static async ValueTask SendToClientsAsync(ConcurrentDictionary<string, System.Net.WebSockets.WebSocket> clients, byte[] bytes, CancellationToken cancellationToken)
    {
        foreach (var (id, socket) in clients)
        {
            if (socket.State is not WebSocketState.Open)
            {
                clients.TryRemove(id, out _);
                continue;
            }

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
            catch (WebSocketException)
            {
                clients.TryRemove(id, out _);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public int ClientCount => _nativeClients.Count + _ninaClients.Count;
}
