using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Hosting;
using TianWen.Hosting.Api;
using TianWen.Hosting.Dto;
using Xunit;

namespace TianWen.Lib.Tests.Functional;

/// <summary>
/// A frame stream's close budgets end on the node's injected clock (#1952): the node's own close, and the wait for the
/// client's answer to it. The budget is set to an hour, the client never answers (or the close never completes), and the
/// fake clock is advanced past it: on the wall clock the stream would hold for the hour, the pump would give up on a
/// stalled stream, and the test would fail. The endpoint is driven directly over a scripted socket, since a real client
/// answers a close as soon as it reads one and nothing outside the endpoint can say when it has ended.
/// </summary>
public class FrameStreamCloseBudgetTests
{
    private static readonly TimeSpan Long = TimeSpan.FromHours(1);

    /// <summary>A socket that takes no asks, and answers nothing the node does to it unless the test says so.</summary>
    private sealed class ScriptedSocket(bool closeNeverCompletes) : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        /// <summary>Completes once the node has begun its own close.</summary>
        public TaskCompletionSource CloseBegun { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether the node gave up on a close that never completed.</summary>
        public bool CloseWasGivenUp { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override WebSocketState State => _state;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseBegun.TrySetResult();
            if (closeNeverCompletes)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CloseWasGivenUp = true;
                    throw;
                }
            }
            _state = WebSocketState.CloseSent;
        }

        public override void Dispose()
        {
        }

        /// <summary>No ask and no answer ever comes: the read ends only when the node cancels it.</summary>
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay above only ends by cancellation");
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class ScriptedWebSocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class StoppableLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new CancellationTokenSource();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();

        public void Dispose() => _stopping.Dispose();
    }

    /// <summary>
    /// Runs a stream whose host starts stopping once it is open, and pumps the fake clock until it has ended. Returns the
    /// fake time that took.
    /// </summary>
    private static async Task<TimeSpan> StopAndPumpAsync(ScriptedSocket socket, FakeTimeProviderWrapper clock, CancellationToken ct)
    {
        using var lifetime = new StoppableLifetime();
        using var services = new ServiceCollection().AddSingleton(new FrameStreamOptions(Long)).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Features.Set<IHttpWebSocketFeature>(new ScriptedWebSocketFeature(socket));

        var run = FrameEndpoints.StreamAsync(context, new NodeFrames(Substitute.For<IHostedSession>()), lifetime, clock, FrameSources.PlanetaryLive);
        lifetime.StopApplication();
        await socket.CloseBegun.Task.WaitAsync(ct);
        run.IsCompleted.ShouldBeFalse("the client has not answered and the clock has not moved");

        // A stalled pump throws once a day of fake time has passed without the stream ending: on the wall clock no timer is
        // on this clock at all, and each step is the coarsest.
        var pumped = await clock.PumpUntilCompletedAsync(run, maxFakeTime: TimeSpan.FromHours(24), coarsestStep: Long, cancellationToken: ct);
        await run.WaitAsync(ct);
        return pumped;
    }

    [Fact(Timeout = 60_000)]
    public async Task AClientThatNeverAnswersTheNodesCloseIsDroppedAtTheCloseBudget()
    {
        var clock = new FakeTimeProviderWrapper();
        var socket = new ScriptedSocket(closeNeverCompletes: false);

        var pumped = await StopAndPumpAsync(socket, clock, TestContext.Current.CancellationToken);

        pumped.ShouldBeGreaterThanOrEqualTo(Long, "only the clock reaching the budget ends the wait for the client's answer");
        socket.CloseWasGivenUp.ShouldBeFalse("the node's own close completed");
    }

    [Fact(Timeout = 60_000)]
    public async Task ACloseThatNeverCompletesIsGivenUpAtTheCloseBudget()
    {
        var clock = new FakeTimeProviderWrapper();
        var socket = new ScriptedSocket(closeNeverCompletes: true);

        var pumped = await StopAndPumpAsync(socket, clock, TestContext.Current.CancellationToken);

        socket.CloseWasGivenUp.ShouldBeTrue("the close budget ran out on the clock");
        pumped.ShouldBeGreaterThanOrEqualTo(Long * 2, "the close's own budget, then the wait for the client's answer: two budgets, one after the other");
    }
}
