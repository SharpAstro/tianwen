using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using TianWen.Hosting.Extensions;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// Something that answers <c>GET /api/v1/node</c> as a node would, but is not this build's node: a node of another
/// wire version (an older install still running), or a node under another account on TCP. It holds the socket's lock
/// like a real node, lets it go when it stops, and counts the times it was asked to stop.
/// </summary>
internal sealed class ImpostorNode : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly NodeLock? _held;
    private int _shutdownRequests;

    private ImpostorNode(WebApplication app, NodeLock? held)
    {
        _app = app;
        _held = held;
    }

    public int ShutdownRequests => Volatile.Read(ref _shutdownRequests);

    /// <summary>Where it listens on TCP, when it is not on a socket.</summary>
    public Uri TcpAddress => new Uri(_app.Urls.First().TrimEnd('/') + "/");

    public static async Task<ImpostorNode> StartAsync(string? socketPath, int wireVersion, bool holdsHardware, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        NodeLock? held = null;
        if (socketPath is null)
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else if (NodeLock.TryAcquire(socketPath, out held, out var refusal))
        {
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenOnNodeSocket(held));
        }
        else
        {
            throw new InvalidOperationException($"The impostor could not take the lock on {socketPath}", refusal);
        }

        var app = builder.Build();
        var impostor = new ImpostorNode(app, held);
        app.MapGet("/api/v1/node", () => Results.Json(
            ResponseEnvelope<NodeInfoDto>.Ok(new NodeInfoDto
            {
                NodeId = "impostor",
                Version = "0.0-impostor",
                WireVersion = wireVersion,
                ProcessId = Environment.ProcessId,
                HoldsHardware = holdsHardware,
                NowUtc = DateTimeOffset.UtcNow,
            }),
            HostingJsonContext.Default.ResponseEnvelopeNodeInfoDto));
        app.MapPost("/api/v1/node/shutdown", () =>
        {
            Interlocked.Increment(ref impostor._shutdownRequests);
            app.Lifetime.StopApplication();
            return Results.Json(ResponseEnvelope<string>.Accepted("Stopping"), HostingJsonContext.Default.ResponseEnvelopeString, statusCode: 202);
        });
        // Asked to stop, it stops, as the real server's RunAsync does (a host started with StartAsync only hears the
        // request), and a node that has stopped lets its socket go, as the process's exit would.
        app.Lifetime.ApplicationStopping.Register(() => _ = Task.Run(() => app.StopAsync()));
        app.Lifetime.ApplicationStopped.Register(() => held?.Dispose());

        await app.StartAsync(cancellationToken);
        return impostor;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _held?.Dispose();
    }
}
