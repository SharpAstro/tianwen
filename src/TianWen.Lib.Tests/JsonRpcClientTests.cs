using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TianWen.Lib.Connections;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// Pins the shared <see cref="JsonRpcClient"/> (extracted from the PHD2 driver, reused by the
/// out-of-process ASCOM COM host) against a fake line connection: id correlation on success,
/// <see cref="JsonRpcException"/> on an <c>error</c> response, and notification routing.
/// </summary>
public class JsonRpcClientTests
{
    /// <summary>In-memory <see cref="IUtf8TextBasedConnection"/>: captures written requests, replays queued inbound lines.</summary>
    private sealed class FakeLineConnection : IUtf8TextBasedConnection
    {
        private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
        public ConcurrentQueue<string> Written { get; } = new();

        public void PushInbound(string line) => _inbound.Writer.TryWrite(line);

        public CommunicationProtocol HighLevelProtocol => CommunicationProtocol.JsonRPC;
        public bool IsConnected => true;
        public ValueTask ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await _inbound.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask<bool> WriteLineAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            Written.Enqueue(Encoding.UTF8.GetString(message.Span));
            return ValueTask.FromResult(true);
        }

        public void Dispose() => _inbound.Writer.TryComplete();
    }

    [Fact]
    public async Task GivenAResultResponseWhenCallingThenTheCorrelatedResultIsReturned()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeLineConnection();
        using var client = new JsonRpcClient(conn);
        var loop = client.ReceiveLoopAsync(ct);

        // First call gets id 1 (Interlocked.Increment from 0).
        var callTask = client.CallAsync("get", w =>
        {
            w.WriteStartArray();
            w.WriteStringValue("Connected");
            w.WriteEndArray();
        }, ct);

        conn.PushInbound("""{"jsonrpc":"2.0","id":1,"result":true}""");

        using var response = await callTask;
        response.RootElement.GetProperty("result").GetBoolean().ShouldBeTrue();

        // The request went out with method + id + params.
        conn.Written.TryDequeue(out var req).ShouldBeTrue();
        using var reqDoc = JsonDocument.Parse(req!);
        reqDoc.RootElement.GetProperty("method").GetString().ShouldBe("get");
        reqDoc.RootElement.GetProperty("id").GetInt64().ShouldBe(1);
        reqDoc.RootElement.GetProperty("params")[0].GetString().ShouldBe("Connected");
    }

    [Fact]
    public async Task GivenAnErrorResponseWhenCallingThenJsonRpcExceptionIsThrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeLineConnection();
        using var client = new JsonRpcClient(conn);
        _ = client.ReceiveLoopAsync(ct);

        var callTask = client.CallAsync("set", null, ct);
        conn.PushInbound("""{"jsonrpc":"2.0","id":1,"error":{"code":-2146233088,"message":"boom"}}""");

        var ex = await Should.ThrowAsync<JsonRpcException>(async () => await callTask);
        ex.Message.ShouldBe("boom");
        ex.Code.ShouldBe(-2146233088);
    }

    [Fact]
    public async Task GivenAMessageWithoutIdWhenReceivedThenItIsRoutedAsANotification()
    {
        var ct = TestContext.Current.CancellationToken;
        var conn = new FakeLineConnection();
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var client = new JsonRpcClient(conn, onNotification: (doc, _) =>
        {
            seen.TrySetResult(doc.RootElement.GetProperty("method").GetString() ?? "");
            doc.Dispose();
            return ValueTask.CompletedTask;
        });
        _ = client.ReceiveLoopAsync(ct);

        conn.PushInbound("""{"jsonrpc":"2.0","method":"GuideStep","params":{"dx":1.0}}""");

        (await seen.Task.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBe("GuideStep");
    }
}
