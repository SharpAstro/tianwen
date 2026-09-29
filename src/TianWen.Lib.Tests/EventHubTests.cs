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
using TianWen.Lib.Devices;
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

    /// <summary>Waits until <paramref name="condition"/> holds, bounded by the test's own timeout (its token) and nothing shorter.</summary>
    private static async Task UntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(10, ct);
        }
    }

    [Fact]
    public void AClientCountsAsSeeingAPromptOnlyWhileItsBeatIsFresh()
    {
        // P1 of docs/plans/hardware-in-the-server.md (#917): a socket used to be enough, and a window frozen by a GPU
        // wedge keeps its socket, so it held a prompt, and the night, for ever.
        var clock = new FakeTimeProviderWrapper();
        var hub = new EventHub(queueCapacity: 4, sendTimeout: TimeSpan.FromMinutes(5), clock);
        var client = hub.AddClient(Recording(new ConcurrentQueue<string>()));
        hub.PresentClientCount.ShouldBe(0, "attached, but never said it can see anything");

        hub.RecordBeat(client);
        hub.PresentClientCount.ShouldBe(1);

        clock.Advance(TianWen.Hosting.Api.NodeWire.PresenceLapse);
        hub.PresentClientCount.ShouldBe(1, "a beat as old as the lapse still counts");

        clock.Advance(TimeSpan.FromSeconds(1));
        hub.PresentClientCount.ShouldBe(0, "its window stopped drawing, though its socket is still open");
        hub.CommandingClientCount.ShouldBe(1, "it is still attached");

        hub.RecordBeat(client);
        hub.PresentClientCount.ShouldBe(1, "it drew again");
    }

    [Fact]
    public void AWatcherThatMayNotCommandIsPresentButNeitherAnswersAPromptNorIsTheLastClient()
    {
        // P6b of docs/plans/hardware-in-the-server.md (#1021): a client over TCP without a grant sees everything and can
        // answer nothing, so counted for a prompt it would hold one for an answer that cannot come.
        var hub = new EventHub(queueCapacity: 4, sendTimeout: TimeSpan.FromMinutes(5), new FakeTimeProviderWrapper());
        var granted = true;
        var watcher = hub.AddClient(Recording(new ConcurrentQueue<string>()), mayCommand: static () => false);
        var laptop = hub.AddClient(Recording(new ConcurrentQueue<string>()), mayCommand: () => granted);
        hub.RecordBeat(watcher);
        hub.RecordBeat(laptop);

        hub.PresentClientCount.ShouldBe(2, "both are watching, which keeps an interactive run going");
        hub.AnsweringClientCount.ShouldBe(1, "only the granted one can answer a prompt");
        hub.CommandingClientCount.ShouldBe(1, "only the granted one is asked before its window closes");

        granted = false;
        hub.AnsweringClientCount.ShouldBe(0, "a revoked grant stops counting at once, its socket still open");
        hub.CommandingClientCount.ShouldBe(0);
        hub.PresentClientCount.ShouldBe(2);
    }

    [Fact]
    public void ANinaApiClientNeverCountsAsSeeingAPromptEvenIfItBeats()
    {
        var hub = new EventHub(queueCapacity: 4, sendTimeout: TimeSpan.FromMinutes(5), new FakeTimeProviderWrapper());
        var nina = hub.AddClient(Recording(new ConcurrentQueue<string>()), ninaV2: true);

        hub.RecordBeat(nina);

        hub.PresentClientCount.ShouldBe(0, "Touch N Stars has no route to answer a prompt");
    }

    [Fact(Timeout = 10_000)]
    public async Task AStalledClientHoldsUpNeitherTheBroadcastNorAnyOtherClient()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new EventHub(queueCapacity: 64, sendTimeout: TimeSpan.FromMinutes(5), new SystemTimeProvider());
        hub.AddClient(Stalled());
        var received = new ConcurrentQueue<string>();
        hub.AddClient(Recording(received));

        // Synchronous by design: had it waited for sockets, the stalled one would hold it for ever.
        for (var n = 0; n < 5; n++)
        {
            hub.Broadcast(Event(n));
        }

        await UntilAsync(() => received.Count == 5, ct);
    }

    [Fact(Timeout = 10_000)]
    public async Task EveryClientReceivesTheEventsInTheOrderTheyWereBroadcast()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new EventHub(queueCapacity: 256, sendTimeout: TimeSpan.FromMinutes(5), new SystemTimeProvider());
        var first = new ConcurrentQueue<string>();
        var second = new ConcurrentQueue<string>();
        hub.AddClient(Recording(first));
        hub.AddClient(Recording(second), ninaV2: true);

        for (var n = 0; n < 100; n++)
        {
            hub.Broadcast(Event(n));
        }

        await UntilAsync(() => first.Count == 100 && second.Count == 100, ct);
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
        var ct = TestContext.Current.CancellationToken;
        var hub = new EventHub(queueCapacity: 2, sendTimeout: TimeSpan.FromMinutes(5), new SystemTimeProvider());
        var stalled = Stalled();
        hub.AddClient(stalled);

        // One is taken into the stalled send, two fill the queue, and the next does not fit.
        for (var n = 0; n < 6; n++)
        {
            hub.Broadcast(Event(n));
        }

        await UntilAsync(() => hub.ClientCount == 0, ct);
        stalled.Received(1).Abort();
    }

    [Fact(Timeout = 10_000)]
    public async Task ASendThatNeverCompletesIsGivenUpAfterTheTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var hub = new EventHub(queueCapacity: 64, sendTimeout: TimeSpan.FromMilliseconds(100), new SystemTimeProvider());
        var stalled = Stalled();
        hub.AddClient(stalled);

        hub.Broadcast(Event(0));

        await UntilAsync(() => hub.ClientCount == 0, ct);
        stalled.Received(1).Abort();
    }

    [Fact]
    public void OnlyANativeClientCanAnswerAPrompt()
    {
        // A ninaAPI v2 socket (Touch N Stars) has no prompt route, so it is nobody to hold a prompt for.
        var hub = new EventHub(queueCapacity: 4, sendTimeout: TimeSpan.FromMinutes(5), new SystemTimeProvider());
        hub.RecordBeat(hub.AddClient(Recording(new ConcurrentQueue<string>()), ninaV2: true));

        hub.ClientCount.ShouldBe(1);
        hub.PresentClientCount.ShouldBe(0);

        hub.RecordBeat(hub.AddClient(Recording(new ConcurrentQueue<string>())));
        hub.PresentClientCount.ShouldBe(1);
    }
}
