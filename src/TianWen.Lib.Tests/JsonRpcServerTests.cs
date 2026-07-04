using Shouldly;
using System;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Exercises <see cref="JsonRpcServer"/> against the real <see cref="JsonRpcClient"/> over an actual
/// loopback TCP connection (no external process): result round-trip + id-correlation, a void method
/// (<c>result:null</c>), and <see cref="JsonRpcException"/> propagation from a throwing handler. This
/// is the pair the out-of-process ASCOM COM host runs on.
/// </summary>
public class JsonRpcServerTests
{
    private static JsonRpcRequestHandler MakeHandler() => (method, @params, result, _) =>
    {
        switch (method)
        {
            case "add":
                result.WriteNumberValue(@params[0].GetInt32() + @params[1].GetInt32());
                break;
            case "ping":
                result.WriteStringValue("pong");
                break;
            case "noop":
                break; // writes nothing -> server emits result:null
            case "boom":
                throw new JsonRpcException("kaboom", -7);
            default:
                throw new JsonRpcException($"unknown method '{method}'");
        }
        return ValueTask.CompletedTask;
    };

    [Fact]
    public async Task GivenServerAndClientOverLoopbackWhenCallingThenResultsErrorsAndVoidsRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;

        using var server = new JsonRpcServer(MakeHandler());
        var port = server.Start();
        var serverTask = server.RunAsync(ct);

        var connection = new JsonRpcOverTcpConnection();
        await connection.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
        using var client = new JsonRpcClient(connection);
        _ = client.ReceiveLoopAsync(ct);

        // result round-trip + id correlation
        using (var r = await client.CallAsync("add", w =>
        {
            w.WriteStartArray();
            w.WriteNumberValue(2);
            w.WriteNumberValue(3);
            w.WriteEndArray();
        }, ct))
        {
            r.RootElement.GetProperty("result").GetInt32().ShouldBe(5);
        }

        using (var r = await client.CallAsync("ping", null, ct))
        {
            r.RootElement.GetProperty("result").GetString().ShouldBe("pong");
        }

        // void method -> result:null
        using (var r = await client.CallAsync("noop", null, ct))
        {
            r.RootElement.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        // throwing handler -> error object -> JsonRpcException on the client
        var ex = await Should.ThrowAsync<JsonRpcException>(async () => await client.CallAsync("boom", null, ct));
        ex.Message.ShouldBe("kaboom");
        ex.Code.ShouldBe(-7);

        connection.Dispose();
    }
}
