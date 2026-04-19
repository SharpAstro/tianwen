using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TianWen.Hosting.Api;
using TianWen.Hosting.Api.NinaV2;
using TianWen.Hosting.WebSocket;

namespace TianWen.Hosting.Extensions;

public static class HostedSessionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the hosted session service, WebSocket event hub, and event broadcaster.
    /// </summary>
    public static IServiceCollection AddHostedSession(this IServiceCollection services)
    {
        services.AddSingleton<HostedSession>();
        services.AddSingleton<IHostedSession>(sp => sp.GetRequiredService<HostedSession>());
        services.AddSingleton<EventHub>();
        services.AddHostedService<EventBroadcaster>();
        return services;
    }

    /// <summary>
    /// Maps all TianWen Hosting API endpoints and the WebSocket event stream.
    /// Call after <c>app.UseWebSockets()</c>.
    /// </summary>
    public static WebApplication MapHostingApi(this WebApplication app)
    {
        // Native TianWen multi-OTA API (v1)
        app.MapProfileApi();
        app.MapSessionApi();
        app.MapOtaApi();
        app.MapMountApi();
        app.MapGuiderApi();
        app.MapDeviceApi();
        app.MapWebSocketEndpoint();

        // ninaAPI v2 compatibility shim for Touch N Stars
        app.MapNinaSystemApi();
        app.MapNinaEquipmentApi();
        app.MapNinaSequenceApi();
        app.MapNinaImageApi();
        app.MapNinaWebSocketEndpoint();

        return app;
    }

    private static void MapWebSocketEndpoint(this IEndpointRouteBuilder routes)
    {
        routes.Map("/api/v1/events", async (HttpContext context, EventHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only");
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            var clientId = hub.AddClient(ws);

            try
            {
                // Keep connection alive by reading (client may send pings or close)
                var buffer = new byte[256];
                while (ws.State is WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType is WebSocketMessageType.Close)
                    {
                        break;
                    }
                }
            }
            catch (WebSocketException)
            {
                // Client disconnected
            }
            catch (OperationCanceledException)
            {
                // Server shutting down
            }
            finally
            {
                hub.RemoveClient(clientId);
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
                    }
                    catch
                    {
                        // Best effort close
                    }
                }
            }
        });
    }

    /// <summary>
    /// ninaAPI v2 WebSocket endpoint at <c>/v2/socket</c>.
    /// Shares the same <see cref="EventHub"/> as the native endpoint but clients
    /// receive PascalCase JSON (matching ninaAPI v2 conventions).
    /// </summary>
    private static void MapNinaWebSocketEndpoint(this IEndpointRouteBuilder routes)
    {
        routes.Map("/v2/socket", async (HttpContext context, EventHub hub) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only");
                return;
            }

            var ws = await context.WebSockets.AcceptWebSocketAsync();
            var clientId = hub.AddClient(ws, ninaV2: true);

            try
            {
                var buffer = new byte[256];
                while (ws.State is WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType is WebSocketMessageType.Close)
                    {
                        break;
                    }
                    // TNS may send { action: "subscribe", eventType: "..." } — we broadcast all events regardless
                }
            }
            catch (WebSocketException)
            {
                // Client disconnected
            }
            catch (OperationCanceledException)
            {
                // Server shutting down
            }
            finally
            {
                hub.RemoveClient(clientId);
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
                    }
                    catch
                    {
                        // Best effort close
                    }
                }
            }
        });
    }
}
