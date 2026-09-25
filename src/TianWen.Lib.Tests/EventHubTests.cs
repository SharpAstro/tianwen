using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shouldly;
using TianWen.Hosting.Dto;
using TianWen.Hosting.WebSocket;
using Xunit;

namespace TianWen.Lib.Tests;

/// <summary>
/// The node's WebSocket fan-out (P0b item 7 of docs/plans/hardware-in-the-server.md, #752). A broadcast queues
/// and returns; each client has one sender of its own, bounded by a queue and a send timeout, so a stalled
/// client costs nobody else anything, and a client that cannot keep up is dropped to resync by polling.
/// </summary>
public class EventHubTests
{
    private static WebSocketEventDto Event(int n) => new WebSocketEventDto
    {
        Event = "NOTIFICATION",
        Data = new Dictionary<string, object?> { ["Message"] = $"event {n}" },
    };

    /// <summary>A client that takes every send at once and keeps what it was sent, in order.</summary>
    private static WebSocket Recording(ConcurrentQueue<string> received)
    {
        var socket = Substitute.For<WebSocket>();
        socket.State.Returns(WebSocketState.Open);
        socket.SendAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                received.Enqueue(Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(0).Span));
                return ValueTask.CompletedTask;
            });
        return socket;
    }

    /// <summary>A client whose sends never complete: a peer that stopped reading, its TCP window full.</summary>
    private static WebSocket Stalled()
    {
        var socket = Substitute.For<WebSocket>();
        socket.State.Returns(WebSocketState.Open);
        socket.SendAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<WebSocketMessageType>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask(Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3))));
        return socket;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        condition().ShouldBeTrue();
    }

    [Fact(Timeout = 10_000)]
    public async Task AStalledClientHoldsUpNeitherTheBroadcastNorAnyOtherClient()
    {
        var hub = new EventHub(queueCapacity: 64, sendTimeout: TimeSpan.FromMinutes(5));
        hub.AddClient(Stalled());
        var received = new ConcurrentQueue<string>();
        hub.AddClient(Recording(received));

        // Synchronous by design: had it waited for sockets, the stalled one would hold it for ever.
        for (var n = 0; n < 5; n++)
        {
            hub.Broadcast(Event(n));
        }

        await UntilAsync(() => received.Count == 5);
    }

    [Fact(Timeout = 10_000)]
    public async Task EveryClientReceivesTheEventsInTheOrderTheyWereBroadcast()
    {
        var hub = new EventHub(queueCapacity: 256, sendTimeout: TimeSpan.FromMinutes(5));
        var first = new ConcurrentQueue<string>();
        var second = new ConcurrentQueue<string>();
        hub.AddClient(Recording(first));
        hub.AddClient(Recording(second), ninaV2: true);

        for (var n = 0; n < 100; n++)
        {
            hub.Broadcast(Event(n));
        }

        await UntilAsync(() => first.Count == 100 && second.Count == 100);
        var index = 0;
        foreach (var message in first)
        {
            message.ShouldContain($"event {index++}\"");
        }
        index = 0;
        foreach (var message in second)
        {
            message.ShouldContain($"event {index++}\"");
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task AClientThatFallsBehindIsDroppedSoItResyncsByPolling()
    {
        var hub = new EventHub(queueCapacity: 2, sendTimeout: TimeSpan.FromMinutes(5));
        var stalled = Stalled();
        hub.AddClient(stalled);

        // One is taken into the stalled send, two fill the queue, and the next does not fit.
        for (var n = 0; n < 6; n++)
        {
            hub.Broadcast(Event(n));
        }

        await UntilAsync(() => hub.ClientCount == 0);
        stalled.Received(1).Abort();
    }

    [Fact(Timeout = 10_000)]
    public async Task ASendThatNeverCompletesIsGivenUpAfterTheTimeout()
    {
        var hub = new EventHub(queueCapacity: 64, sendTimeout: TimeSpan.FromMilliseconds(100));
        var stalled = Stalled();
        hub.AddClient(stalled);

        hub.Broadcast(Event(0));

        await UntilAsync(() => hub.ClientCount == 0);
        stalled.Received(1).Abort();
    }

    [Fact]
    public void OnlyANativeClientCanAnswerAPrompt()
    {
        // A ninaAPI v2 socket (Touch N Stars) has no prompt route, so it is nobody to hold a prompt for.
        var hub = new EventHub(queueCapacity: 4, sendTimeout: TimeSpan.FromMinutes(5));
        hub.AddClient(Recording(new ConcurrentQueue<string>()), ninaV2: true);

        hub.ClientCount.ShouldBe(1);
        hub.PromptObserverCount.ShouldBe(0);

        hub.AddClient(Recording(new ConcurrentQueue<string>()));
        hub.PromptObserverCount.ShouldBe(1);
    }
}
